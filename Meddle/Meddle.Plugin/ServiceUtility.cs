using Dalamud.Interface.Windowing;
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
               .AddSingleton<ITab, AnimationLibraryTab>()
               .AddSingleton<MainWindow>()
               .AddSingleton(new WindowSystem("XivBlend"))
               .AddHostedService<WindowManager>();
    }

    public static IServiceCollection AddServices(this IServiceCollection services)
    {
        return services
               .AddSingleton<AnimationPropAssetExporter>()
               .AddSingleton<AnimationVfxAssetExporter>()
               .AddSingleton<AnimationLibraryService>()
               .AddSingleton<BlenderAnimationBrowserInstaller>()
               .AddSingleton<CharacterPartProvenanceService>()
               .AddSingleton<ComposerFactory>()
               .AddSingleton<PenumbraAnimationModService>()
               .AddSingleton<PbdHooks>()
               .AddSingleton<QuickBlendExportService>()
               .AddSingleton<ResolverService>()
               .AddSingleton<RsfWatcher>()
               .AddSingleton<StainProvider>()
               .AddSingleton<VanillaPapAnimationExporter>();
    }
}
