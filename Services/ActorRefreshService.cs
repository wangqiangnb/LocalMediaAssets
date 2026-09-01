using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 演员刷新协调服务：
/// 1. 监听 <see cref="ILibraryManager.ItemUpdated"/>，Person 元数据变化后自动重建索引；
/// 2. 提供单演员刷新入口：调用 Jellyfin 原生 <see cref="BaseItem.RefreshMetadata"/>（Default 模式，
///    不指定 Provider、不禁用任何刮削源，让 Jellyfin 自行处理），完成后把该演员素材落盘到本地
///    并重建索引，使前端轮询的状态立即生效。
/// </summary>
public sealed class ActorRefreshService : IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly PersonImageIndexer _indexer;
    private readonly IActorDatabase _db;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ActorRefreshService> _logger;

    private readonly ConcurrentDictionary<string, byte> _refreshing = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private readonly object _sync = new();
    private DateTime _lastEventRebuildUtc = DateTime.MinValue;

    private static readonly TimeSpan EventRebuildCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="ActorRefreshService"/> class.
    /// </summary>
    public ActorRefreshService(
        ILibraryManager libraryManager,
        PersonImageIndexer indexer,
        IActorDatabase db,
        IFileSystem fileSystem,
        ILogger<ActorRefreshService> logger)
    {
        _libraryManager = libraryManager;
        _indexer = indexer;
        _db = db;
        _fileSystem = fileSystem;
        _logger = logger;

        // 监听 Person 元数据变化（刷新完成/库扫描后），自动重建索引保持与磁盘一致
        _libraryManager.ItemUpdated += OnItemUpdated;
    }

    /// <summary>
    /// 判断某演员是否正在刷新中（前端轮询用）。
    /// </summary>
    public bool IsRefreshing(string? actorName)
        => !string.IsNullOrWhiteSpace(actorName) && _refreshing.ContainsKey(Normalize(actorName));

    /// <summary>
    /// 启动单演员刷新（立即返回，后台执行）。
    /// 已在该演员刷新中时忽略重复请求。
    /// </summary>
    public void StartRefresh(string actorName)
    {
        var key = Normalize(actorName);
        if (!_refreshing.TryAdd(key, 0))
        {
            _logger.LogDebug("LocalMediaAssets：演员 {Name} 已在刷新中，忽略重复请求", actorName);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshActorCoreAsync(actorName).ConfigureAwait(false);
            }
            finally
            {
                _refreshing.TryRemove(key, out _);
            }
        });
    }

    private async Task RefreshActorCoreAsync(string actorName)
    {
        try
        {
            var person = _libraryManager.GetPerson(actorName);
            if (person is null)
            {
                _logger.LogWarning("LocalMediaAssets：未找到演员 {Name}，刷新已跳过", actorName);
                return;
            }

            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return;
            }

            // 让 Jellyfin 自行处理：Default 模式，不指定 Provider，不禁用任何刮削源。
            // 本地有素材则本地提供器快速返回；本地没有则由网络下载器兜底（通用性保留）。
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.Default
            };

            await person.RefreshMetadata(options, CancellationToken.None).ConfigureAwait(false);

            // 刷新完成后：把 Jellyfin Person 的信息（简介）写入数据库主存储
            if (!string.IsNullOrWhiteSpace(person.Overview))
            {
                _db.SaveInfo(actorName, person.Overview, null, config);
            }

            // 同步：把该演员的 Jellyfin 刮削照片/备用库照片补充进主存储并设为默认，
            // 分发到各视频目录，并重建索引（让 Status 立即反映新素材）
            await Task.Run(() => _db.Sync(config, SyncTrigger.Actor)).ConfigureAwait(false);

            _logger.LogInformation("LocalMediaAssets：演员 {Name} 刷新完成，已更新信息、同步素材并重建索引", actorName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：演员 {Name} 刷新失败", actorName);
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        // 只关心 Person 的元数据变化（刷新完成/库扫描后自动更新索引）
        if (e.Item is not Person)
        {
            return;
        }

        // 事件节流：30 秒内最多由事件触发一次全量重建，避免频繁遍历
        lock (_sync)
        {
            if (DateTime.UtcNow - _lastEventRebuildUtc < EventRebuildCooldown)
            {
                return;
            }

            _lastEventRebuildUtc = DateTime.UtcNow;
        }

        _ = RebuildIndexAsync();
    }

    private async Task RebuildIndexAsync()
    {
        await _rebuildLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return;
            }

            _indexer.Rebuild(config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：索引重建失败");
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private static string Normalize(string name)
        => name.Trim().ToLowerInvariant();

    /// <inheritdoc />
    public void Dispose()
    {
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _rebuildLock.Dispose();
    }
}
