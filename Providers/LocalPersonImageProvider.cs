using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.LocalMediaAssets.Services;
using Jellyfin.Plugin.LocalMediaAssets.Utils;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Providers;

/// <summary>
/// 演员照片本地提供器：按「视频目录 → 演员库」顺序为 Person 提供本地照片。
/// </summary>
public sealed class LocalPersonImageProvider : ILocalImageProvider, IHasOrder
{
    private readonly PersonImageIndexer _indexer;
    private readonly ILogger<LocalPersonImageProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalPersonImageProvider"/> class.
    /// </summary>
    public LocalPersonImageProvider(PersonImageIndexer indexer, ILogger<LocalPersonImageProvider> logger)
    {
        _indexer = indexer;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "LocalMediaAssets Person Images";

    /// <inheritdoc />
    public int Order => -10;

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is Person;

    /// <inheritdoc />
    public IEnumerable<LocalImageInfo> GetImages(BaseItem item, IDirectoryService directoryService)
    {
        if (item is not Person person)
        {
            yield break;
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            yield break;
        }

        var path = _indexer.FindPersonImage(person.Name, config);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            yield break;
        }

        _logger.LogDebug("LocalMediaAssets：为演员 {Name} 使用本地照片 {Path}", person.Name, path);
        yield return new LocalImageInfo
        {
            FileInfo = FileMetadataFactory.Create(path),
            Type = ImageType.Primary
        };
    }
}
