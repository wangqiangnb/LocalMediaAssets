using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LocalMediaAssets.Configuration;
using Jellyfin.Plugin.LocalMediaAssets.Utils;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Providers;

/// <summary>
/// 剧照提供器：把视频目录下 extrafanart/ 文件夹里的图片作为多张 Backdrop 提供给详情页。
/// </summary>
public sealed class StillsBackdropProvider : ILocalImageProvider, IHasOrder
{
    private static readonly string[] SupportedExtensions = BaseItem.SupportedImageExtensions;

    private readonly ILogger<StillsBackdropProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StillsBackdropProvider"/> class.
    /// </summary>
    public StillsBackdropProvider(ILogger<StillsBackdropProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "LocalMediaAssets Stills";

    /// <inheritdoc />
    public int Order => -10;

    /// <inheritdoc />
    public bool Supports(BaseItem item)
        => item is Movie or Series or Episode or Video;

    /// <inheritdoc />
    public IEnumerable<LocalImageInfo> GetImages(BaseItem item, IDirectoryService directoryService)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableStillsAsBackdrops)
        {
            yield break;
        }

        if (string.IsNullOrEmpty(item.Path))
        {
            yield break;
        }

        // 文件夹类型（如剧集、季）的 Path 就是文件夹本身，其余取父目录
        var baseDir = item is Folder ? item.Path : Path.GetDirectoryName(item.Path);
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
        {
            yield break;
        }

        var stillsFolderName = PluginConfiguration.IsValidFolderName(config.StillsFolderName) ? config.StillsFolderName.Trim() : "extrafanart";
        var stillsDir = Path.Combine(baseDir, stillsFolderName);
        if (!Directory.Exists(stillsDir))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(stillsDir)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), System.StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, System.StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：读取剧照目录失败 {Dir}", stillsDir);
            yield break;
        }

        var count = 0;
        foreach (var file in files)
        {
            if (count >= config.MaxStills)
            {
                yield break;
            }

            yield return new LocalImageInfo
            {
                FileInfo = FileMetadataFactory.Create(file),
                Type = ImageType.Backdrop
            };
            count++;
        }

        if (count > 0)
        {
            _logger.LogDebug("LocalMediaAssets：为 {Item} 提供 {Count} 张剧照", item.Name, count);
        }
    }
}
