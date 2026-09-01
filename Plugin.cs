using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.LocalMediaAssets.Configuration;
using Jellyfin.Plugin.LocalMediaAssets.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging;

[assembly: Guid("8f2a9d0e-3c4b-4a5f-9e6d-1b2c3d4e5f60")]

namespace Jellyfin.Plugin.LocalMediaAssets;

/// <summary>
/// LocalMediaAssets 插件入口。
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    /// <param name="serverConfigurationManager">The server configuration manager（用于自愈式 Web 补丁）。</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IServerConfigurationManager serverConfigurationManager,
        MediaBrowser.Controller.IServerApplicationHost applicationHost,
        Microsoft.Extensions.Logging.ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // 启动时确保 jellyfin-web/index.html 已注入剧照脚本（幂等；Jellyfin 升级后自动恢复）
        WebPatch.EnsureApplied(serverConfigurationManager.ApplicationPaths, logger);

        // 启动时自动把插件自身注册为存储库（无需用户操作）：
        // 使插件详情页正常显示「设置」按钮、消除"从存储库获取插件详情时发生错误"的警告，
        // 并保证换机器后把 DLL 放进插件目录即功能完整。
        EnsureRepositoryRegistered(serverConfigurationManager, applicationHost.HttpPort, logger);
    }

    private void EnsureRepositoryRegistered(IServerConfigurationManager configManager, int httpPort, Microsoft.Extensions.Logging.ILogger logger)
    {
        try
        {
            var repoUrl = $"http://localhost:{httpPort}/LocalMediaAssets/repository.json";

            var repos = configManager.Configuration.PluginRepositories ?? [];
            if (repos.Any(r => string.Equals(r.Url, repoUrl, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var list = repos.ToList();
            list.Add(new RepositoryInfo
            {
                Name = "LocalMediaAssets",
                Url = repoUrl,
                Enabled = true
            });
            configManager.Configuration.PluginRepositories = list.ToArray();
            configManager.SaveConfiguration();

            logger.LogInformation("LocalMediaAssets：已自动注册插件存储库 {Url}", repoUrl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LocalMediaAssets：自动注册插件存储库失败");
        }
    }

    /// <inheritdoc />
    public override string Name => "LocalMediaAssets";

    /// <inheritdoc />
    public override string Description => "本地化媒体素材：演员照片/简介跟随媒体文件，详情页展示预览图与预告片，支持演员库同步与 NFO 补写，换机器无需重新刮削。";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = "Jellyfin.Plugin.LocalMediaAssets.ConfigurationPage.configPage.html"
            }
        ];
    }
}
