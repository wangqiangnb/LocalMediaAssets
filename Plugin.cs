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
        _logger = logger;
        _applicationPaths = applicationPaths;

        // 启动时确保 jellyfin-web/index.html 已注入详情页优化脚本（幂等、带标记可精确移除）
        WebPatch.EnsureApplied(serverConfigurationManager.ApplicationPaths, logger);
    }

    private readonly Microsoft.Extensions.Logging.ILogger<Plugin> _logger;
    private readonly IApplicationPaths _applicationPaths;

    /// <summary>
    /// 卸载插件时：精确移除注入在 jellyfin-web/index.html 中的脚本行，不影响其他插件对该文件的修改。
    /// </summary>
    public override void OnUninstalling()
    {
        try
        {
            var index = Path.Combine(_applicationPaths.WebPath, "index.html");
            WebPatch.Restore(index, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：卸载时移除 Web 脚本失败");
        }

        base.OnUninstalling();
    }

    /// <inheritdoc />
    public override string Name => "LocalMediaAssets";

    /// <inheritdoc />
    public override string Description => "Jellyfin 演员界面优化与预览图优化。项目主页：https://github.com/wangqiangnb/LocalMediaAssets";

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
