using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LocalMediaAssets.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 演员素材索引（兼容壳，委托 <see cref="ActorIndex"/>）。
/// 保留旧版对外接口（FindPersonImage / FindPersonImages / FindPersonInfoFile /
/// Rebuild / InvalidateCache / RebuildStaging / FindStagingImages），
/// 内部统一走 ActorIndex 单索引（四来源聚合 + mtime 增量）。
/// </summary>
public sealed class PersonImageIndexer
{
    private readonly ActorIndex _actorIndex;
    private readonly ILogger<PersonImageIndexer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersonImageIndexer"/> class.
    /// </summary>
    public PersonImageIndexer(ActorIndex actorIndex, ILogger<PersonImageIndexer> logger)
    {
        _actorIndex = actorIndex;
        _logger = logger;
    }

    /// <summary>
    /// 使索引缓存失效（下次访问全量重建）。
    /// </summary>
    public void InvalidateCache() => _actorIndex.InvalidateCache();

    /// <summary>
    /// 立即重建索引（四来源聚合）。
    /// </summary>
    public void Rebuild(PluginConfiguration config) => _actorIndex.Rebuild(config);

    /// <summary>
    /// 重建备用库索引（兼容：备用库已并入统一索引，这里等价于全量重建）。
    /// </summary>
    public int RebuildStaging(PluginConfiguration config) => _actorIndex.Rebuild(config);

    /// <summary>
    /// 按优先级查找演员默认照片。
    /// </summary>
    public string? FindPersonImage(string personName, PluginConfiguration config)
    {
        var photo = _actorIndex.FindDefaultPhoto(personName, config);
        return photo?.Path;
    }

    /// <summary>
    /// 按优先级返回演员的全部照片（默认图排最前）。
    /// </summary>
    public IReadOnlyList<string> FindPersonImages(string personName, PluginConfiguration config)
    {
        var photos = _actorIndex.FindPhotos(personName, config);
        return photos
            .Where(p => !string.IsNullOrEmpty(p.Path))
            .Select(p => p.Path!)
            .ToList();
    }

    /// <summary>
    /// 返回备用库中某演员的照片（兼容：统一索引中 Staging 来源的照片）。
    /// </summary>
    public IReadOnlyList<string> FindStagingImages(string personName, PluginConfiguration config)
    {
        var photos = _actorIndex.FindPhotos(personName, config);
        return photos
            .Where(p => p.Source == ActorSource.Staging && !string.IsNullOrEmpty(p.Path))
            .Select(p => p.Path!)
            .ToList();
    }

    /// <summary>
    /// 按优先级查找演员信息文件（.json）。
    /// </summary>
    public string? FindPersonInfoFile(string personName, PluginConfiguration config)
        => _actorIndex.FindInfoFile(personName, config);
}
