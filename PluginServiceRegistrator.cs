using Jellyfin.Plugin.LocalMediaAssets.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LocalMediaAssets;

/// <summary>
/// 把插件的内部服务注册进 Jellyfin 的依赖注入容器。
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PersonImageIndexer>();
        serviceCollection.AddSingleton<ActorAssetExporter>();
        serviceCollection.AddSingleton<UserSettingsManager>();
    }
}
