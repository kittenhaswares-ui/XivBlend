using System.Reflection;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Meddle.Plugin.Services;
using Meddle.Plugin.UI;
using Meddle.Plugin.UI.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Meddle.Plugin;

public static class ServiceUtility
{
    public static IServiceCollection AddUi(this IServiceCollection services)
    {
        return services
               .AddSingleton<ITab, QuickBlendExportTab>()
               .AddSingleton<MainWindow>()
               .AddSingleton(new WindowSystem("XivBlend"))
               .AddHostedService<WindowManager>();
    }

    public static IServiceCollection AddServices(this IServiceCollection services, IDalamudPluginInterface pluginInterface)
    {
        var serviceTypes = Assembly.GetExecutingAssembly().GetTypes()
                                   .Where(t => t is {IsClass: true, IsAbstract: false} && typeof(IService).IsAssignableFrom(t));
        foreach (var serviceType in serviceTypes)
        {
            services.AddSingleton(serviceType);
        }
        
        return services;
    }
}
