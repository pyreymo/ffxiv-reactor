using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using FFReactor.Windows;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace FFReactor;

public unsafe class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] internal static IGameInteropProvider HookProvider { get; set; } = null!;

    [PluginService] public IPluginLog Log { get; private set; } = null!;

    private const string MainWindowCmd = "/py";
    private const string ExcelWindowCmd = "/ex";

    private readonly HashSet<uint> reviveSpellIds = [125, 173, 3603, 24287];
    private const uint RaiseStatusId = 148;

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
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(MainWindowCmd, new CommandInfo(OnMainWindowCommand)
        {
            HelpMessage = "打开插件主窗口"
        });

        ExcelWindow = new ExcelWindow(this);
        WindowSystem.AddWindow(ExcelWindow);

        CommandManager.AddHandler(ExcelWindowCmd, new CommandInfo(OnExcelWindowCommand)
        {
            HelpMessage = "打开数据预览窗口"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        var useActionAddress = (nint)ActionManager.MemberFunctionPointers.UseAction;
        useActionHook = HookProvider.HookFromAddress<UseActionDelegate>(useActionAddress, UseActionDetour);
        useActionHook.Enable();

        Framework.Update += OnUpdate;
    }

    private bool UseActionDetour(ActionManager* actionManager, ActionType actionType, uint actionID, ulong targetID, uint param4, uint param5, uint param6, void* param7)
    {
        var actionSuccessful = useActionHook!.Original(actionManager, actionType, actionID, targetID, param4, param5, param6, param7);

        if (actionSuccessful && actionType == ActionType.Action && reviveSpellIds.Contains(actionID))
        {
            pendingTargetId = targetID;
            statusCheckStartTime = Environment.TickCount64;
        }

        return actionSuccessful;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!pendingTargetId.HasValue) return;

        var player = ObjectTable.LocalPlayer;
        if (player == null) return;

        if (player.IsCasting && reviveSpellIds.Contains(player.CastActionId))
        {
            statusCheckStartTime = Environment.TickCount64;
            return;
        }

        if (Environment.TickCount64 - statusCheckStartTime > 3000)
        {
            pendingTargetId = null;
            return;
        }

        var target = ObjectTable.SearchById((uint)pendingTargetId.Value);

        if (target is IBattleChara battleChara)
        {
            foreach (var status in battleChara.StatusList)
            {
                if (status.StatusId == RaiseStatusId && status.SourceId == player.GameObjectId)
                {
                    Chat.SendMessage($"/p 【{target.Name}】抢救成功");

                    pendingTargetId = null;
                    return;
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

        useActionHook?.Disable();
        useActionHook?.Dispose();
        Framework.Update -= OnUpdate;

        ECommonsMain.Dispose();
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
