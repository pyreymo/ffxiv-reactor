using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SamplePlugin.Windows;

namespace SamplePlugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public IPluginLog Log { get; private set; } = null!;

    private const string MainWindowCmd = "/py";
    private const string ExcelWindowCmd = "/ex";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("SamplePlugin");
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
