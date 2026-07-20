using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Meddle.Plugin.Services;
using Meddle.Plugin.Utils;
using Meddle.Utils.Files.SqPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OtterTex;

namespace Meddle.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IHost? app;
    private readonly ILogger pluginLog;
    public static ILogger<Plugin> Logger { get; private set; } = NullLogger<Plugin>.Instance;
    public static INotificationManager NotificationManager { get; private set; } = null!;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        var service = new Service();
        pluginInterface.Inject(service);
        
        var dLogger = service.GetLog() ?? throw new InvalidOperationException("Service log is null");
        pluginLog = new PluginSerilogWrapper(dLogger.Logger);
        pluginLog.LogDebug("XivBlend Plugin initializing...");
        Meddle.Utils.Global.Logger = pluginLog;
        
        try
        {
#if HAS_LOCAL_CS
            FFXIVClientStructs.Interop.Generated.Addresses.Register();
            InteropGenerator.Runtime.Resolver.GetInstance.Setup();
            InteropGenerator.Runtime.Resolver.GetInstance.Resolve();
#endif
            
            var config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            pluginInterface.Inject(config);
            config.Migrate();

            var host = Host.CreateDefaultBuilder();
            host.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Trace);
                var loggerProvider = new PluginLoggerProvider(config);
                pluginInterface.Inject(loggerProvider);
                logging.AddProvider(loggerProvider);
            });

            host.ConfigureServices(services =>
            {
                services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);
                service.RegisterServices(services);
                services
                    .AddServices(pluginInterface)    
                    .AddSingleton(config)
                    .AddUi()
                    .AddSingleton(new SqPack(Environment.CurrentDirectory));
            });

            app = host.Build();
            Logger = app.Services.GetRequiredService<ILogger<Plugin>>();
            NotificationManager = app.Services.GetRequiredService<INotificationManager>();
            Meddle.Utils.Global.Logger = app.Services.GetRequiredService<ILogger<Meddle.Utils.Global>>();
            NativeDll.Initialize(app.Services.GetRequiredService<IDalamudPluginInterface>().AssemblyLocation.DirectoryName);
            var pack = app.Services.GetRequiredService<SqPack>();
            pack.RsfData = config.RsfConfig.GetRsfData();
            app.Services.GetRequiredService<RsfWatcher>();

            app.Start();
        }
        catch (Exception e)
        {
            pluginLog.LogError(e, "Failed to initialize plugin");
            Dispose();
            // Do not leave Dalamud showing a loaded plugin with no command or UI.
            // Rethrowing makes initialization failures visible and actionable.
            throw;
        }
    }

    public void Dispose()
    {
        app?.StopAsync();
        app?.WaitForShutdown();
        app?.Dispose();
        pluginLog.LogDebug("Plugin disposed");
    }
}
