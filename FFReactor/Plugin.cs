using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFReactor.Windows;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace FFReactor;

public unsafe class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; set; } = null!;
    [PluginService] internal static IFramework Framework { get; set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] internal static IGameInteropProvider HookProvider { get; set; } = null!;

    [PluginService] public IPluginLog Log { get; private set; } = null!;

    private const string MainWindowCmd = "/py";
    private const string ExcelWindowCmd = "/ex";

    // 四个奶妈的复活技能 ID
    private readonly HashSet<uint> reviveSpellIds = [125, 173, 3603, 24287];
    private const uint RaiseStatusId = 148;

    // 声明 UseAction 的委托类型和 Hook 实例
    private delegate bool UseActionDelegate(ActionManager* actionManager, ActionType actionType, uint actionID, ulong targetID, uint param4, uint param5, uint param6, void* param7);
    private readonly Hook<UseActionDelegate>? useActionHook;

    private ulong? pendingTargetId = null;
    private long statusCheckStartTime = 0;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("FFReactor");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private ExcelWindow ExcelWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Config + Main 窗口实例化
        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(MainWindowCmd, new CommandInfo(OnMainWindowCommand)
        {
            HelpMessage = "打开插件主窗口"
        });

        // Excel 窗口实例化
        ExcelWindow = new ExcelWindow(this);
        WindowSystem.AddWindow(ExcelWindow);

        CommandManager.AddHandler(ExcelWindowCmd, new CommandInfo(OnExcelWindowCommand)
        {
            HelpMessage = "打开数据预览窗口"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // 注册 UI 事件
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // 1. 初始化 Hook：从 FFXIVClientStructs 获取 UseAction 的内存地址并挂载我们的拦截函数
        var useActionAddress = (nint)ActionManager.MemberFunctionPointers.UseAction;
        useActionHook = HookProvider.HookFromAddress<UseActionDelegate>(useActionAddress, UseActionDetour);
        useActionHook.Enable();

        // 2. 绑定每帧更新事件，用于后续的状态轮询
        Framework.Update += OnUpdate;
    }

    // 这是我们拦截到的 UseAction 动作
    private bool UseActionDetour(ActionManager* actionManager, ActionType actionType, uint actionID, ulong targetID, uint param4, uint param5, uint param6, void* param7)
    {
        // 先调用游戏原本的 UseAction 函数，并获取返回值
        // 返回 true 代表技能确实用出去了（没有因为卡硬直、超距等原因在客户端被拦下）
        var actionSuccessful = useActionHook!.Original(actionManager, actionType, actionID, targetID, param4, param5, param6, param7);

        // 如果技能释放成功，且是常规技能(Action)，且是我们的复活技能
        if (actionSuccessful && actionType == ActionType.Action && reviveSpellIds.Contains(actionID))
        {
            // 记录目标 ID 并开始计时
            pendingTargetId = targetID;
            statusCheckStartTime = Environment.TickCount64;
        }

        return actionSuccessful;
    }

    private void OnUpdate(IFramework framework)
    {
        // 如果没有正在等待复活判定的目标，直接跳过
        if (!pendingTargetId.HasValue) return;

        // 获取本地玩家状态
        var player = ObjectTable.LocalPlayer;
        if (player == null) return;

        // 如果玩家正在读条，且读的是复活技能，说明技能还在蓄力中
        if (player.IsCasting && reviveSpellIds.Contains(player.CastActionId))
        {
            // 不断重置起始时间，把 3 秒的倒计时“冻结”住
            statusCheckStartTime = Environment.TickCount64;
            return; // 还在读条，没必要检查 Buff，直接等待下一帧
        }

        // 走到这里说明：读条已经结束，或者这是一个即刻咏唱（根本没有读条）
        // 给服务器 3000 毫秒 (3秒) 的宽限时间来返回复活状态
        if (Environment.TickCount64 - statusCheckStartTime > 3000)
        {
            // 超过 3 秒没拿到 Buff（可能是读条被打断、目标卡视野等），清空状态
            pendingTargetId = null;
            return;
        }

        // 尝试从对象表中获取目标实体
        var target = ObjectTable.SearchById((uint)pendingTargetId.Value);

        if (target is IBattleChara battleChara)
        {
            // 轮询目标的 Buff 列表
            foreach (var status in battleChara.StatusList)
            {
                // 严谨起见，不仅判断状态是 148，还可以顺便判断这个复活是你给的（SourceId）
                // 避免别人刚好在你读条结束前零点几秒把人拉起来了，你跟着发一句错位提示
                if (status.StatusId == RaiseStatusId && status.SourceId == player.GameObjectId)
                {
                    // 检测到 148 状态！发送只有自己可见的本地提示
                    ChatGui.Print($"我复活了 {target.Name}！");

                    // 任务完成，清空等待状态
                    pendingTargetId = null;
                    return; // 找到就退出当前方法
                }
            }
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        ExcelWindow.Dispose();

        CommandManager.RemoveHandler(MainWindowCmd);
        CommandManager.RemoveHandler(ExcelWindowCmd);
    }

    private void OnMainWindowCommand(string command, string args)
    {
        MainWindow.Toggle();
    }
    private void OnExcelWindowCommand(string command, string args)
    {
        ExcelWindow.Toggle();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
    public void ToggleExcelUi() => ExcelWindow.Toggle();
}
