using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Meddle.Plugin.UI.Windows;

public class WindowManager : IHostedService, IDisposable
{
    private const string Command = "/xivblend";
    private readonly ICommandManager commandManager;
    private readonly Configuration config;
    private readonly ILogger<WindowManager> log;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly MainWindow mainWindow;
    private readonly WindowSystem windowSystem;

    private bool disposed;

    public WindowManager(
        MainWindow mainWindow,
        WindowSystem windowSystem,
        IDalamudPluginInterface pluginInterface,
        ILogger<WindowManager> log,
        Configuration config,
        ICommandManager commandManager)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.config = config;
        this.commandManager = commandManager;
        this.mainWindow = mainWindow;
        this.windowSystem = windowSystem;
    }
    
    public void Dispose()
    {
        if (!disposed)
        {
            log.LogDebug("Disposing window manager");
            commandManager.RemoveHandler(Command);
            config.OnConfigurationSaved -= OnSave;
            pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
            pluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
            pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
            windowSystem.RemoveAllWindows();
            disposed = true;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        windowSystem.AddWindow(mainWindow);

        config.OnConfigurationSaved += OnSave;
        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;
        pluginInterface.UiBuilder.DisableGposeUiHide = config.DisableGposeUiHide;
        pluginInterface.UiBuilder.DisableCutsceneUiHide = config.DisableCutsceneUiHide;
        pluginInterface.UiBuilder.DisableAutomaticUiHide = config.DisableAutomaticUiHide;
        pluginInterface.UiBuilder.DisableUserUiHide = config.DisableUserUiHide;

        if (config.OpenOnLoad)
        {
            OpenMainUi();
        }

        commandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the menu"
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    private void OnSave()
    {
        pluginInterface.UiBuilder.DisableGposeUiHide = config.DisableGposeUiHide;
        pluginInterface.UiBuilder.DisableCutsceneUiHide = config.DisableCutsceneUiHide;
        pluginInterface.UiBuilder.DisableAutomaticUiHide = config.DisableAutomaticUiHide;
        pluginInterface.UiBuilder.DisableUserUiHide = config.DisableUserUiHide;
    }

    public void OpenMainUi()
    {
        mainWindow.IsOpen = true;
        mainWindow.BringToFront();
    }

    private void OnCommand(string command, string args)
    {
        OpenMainUi();
    }
}
