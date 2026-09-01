using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LocalMediaAssets.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 演员数据同步引擎（统一更新入口）：
/// - 来源：备用库（Staging）、视频目录（Video）、主演员库（Library）中的新照片；
/// - 目标：按存储模式（Video+Library 默认：权威在演员库，分发到各视频 actors/）；
/// - 规则：哈希去重（同演员）、墓碑防回加、同步不替换默认照片（缺失时自愈）。
/// </summary>
public sealed class ActorSyncEngine
{
    private readonly ActorIndex _index;
    private readonly DeletedPhotoStore _deletedPhotos;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ActorSyncEngine> _logger;
    private readonly object _syncLock = new();

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".tbn", ".gif", ".svg"];

    /// <summary>
    /// Initializes a new instance of the <see cref="ActorSyncEngine"/> class.
    /// </summary>
    public ActorSyncEngine(
        ActorIndex index,
        DeletedPhotoStore deletedPhotos,
        ILibraryManager libraryManager,
        ILogger<ActorSyncEngine> logger)
    {
        _index = index;
        _deletedPhotos = deletedPhotos;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// 执行同步。
    /// - 常规同步（手动/定时/扫描后）：四个来源（演员库/视频目录/备用库/Jellyfin）全部检查；
    /// - 详情页触发刮削后（SyncTrigger.Actor）：只监视 Jellyfin 源（刮削结果），补图进主存储。
    /// </summary>
    public SyncResult Run(PluginConfiguration config, SyncTrigger trigger, ActorDatabase db)
    {
        lock (_syncLock)
        {
            var result = new SyncResult();
            try
            {
                var mode = config.StorageMode ?? "VideoOnly";
                if (mode == "Disabled")
                {
                    // 数据库关闭：不参与任何同步
                    _index.Rebuild(config);
                    return result;
                }

                // 1. 刷新索引（感知最新文件）
                _index.Rebuild(config);

                if (trigger == SyncTrigger.Actor)
                {
                    // 详情页触发刮削后：单独监视 Jellyfin 源（不检查备用库/视频分发等）
                    result.AddedPhotos += SyncFromJellyfin(config, result);
                    _index.Rebuild(config);
                    _logger.LogInformation(
                        "LocalMediaAssets：刮削后同步完成（仅 Jellyfin 源）trigger={Trigger} 新增={Added} 墓碑跳过={Tomb}",
                        trigger, result.AddedPhotos, result.TombstoneSkipped);
                    return result;
                }

                // 2. 常规同步：四来源全检查
                //    从备用库补充到主存储
                result.AddedPhotos += SyncFromStaging(config, result);

                //    从 Jellyfin 刮削源补充（本地无照片时，把 Jellyfin Person 图复制进主存储并设为默认）
                result.AddedPhotos += SyncFromJellyfin(config, result);

                //    从视频目录/其他来源补充（已聚合在索引中；对缺失默认照片自愈）
                result.AddedPhotos += RepairMissingDefaults(config, result);

                //    按存储模式分发默认照片+信息到各视频 actors/（跟随视频走）
                DistributeToVideos(config, result);

                // 3. 重建索引（写入/移动后）
                _index.Rebuild(config);

                _logger.LogInformation(
                    "LocalMediaAssets：同步完成 trigger={Trigger} 新增={Added} 重复={Dup} 墓碑跳过={Tomb} 信息更新={Info}",
                    trigger, result.AddedPhotos, result.DuplicateSkipped, result.TombstoneSkipped, result.UpdatedInfos);
            }
            catch (Exception ex)
            {
                result.Errors++;
                _logger.LogError(ex, "LocalMediaAssets：同步失败");
            }

            return result;
        }
    }

    /// <summary>
    /// 从备用库补充照片到主存储（权威：演员库）。
    /// </summary>
    private int SyncFromStaging(PluginConfiguration config, SyncResult result)
    {
        var staging = config.ActorStagingPath?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(staging) || !Directory.Exists(staging))
        {
            return 0;
        }

        var mode = config.StorageMode ?? "VideoOnly";
        var copied = 0;
        foreach (var file in EnumerateStagingFiles(staging, config))
        {
            try
            {
                var actorName = ActorDatabase.SafeFileName(GetBaseName(file));
                if (string.IsNullOrEmpty(actorName))
                {
                    continue;
                }

                // 墓碑检查
                if (_deletedPhotos.IsDeleted(actorName, file))
                {
                    result.TombstoneSkipped++;
                    continue;
                }

                // 目标目录：Library 相关模式 → 演员库 People/<首字>/；VideoOnly → 参演视频 actors/
                var targetDirs = GetTargetDirsForActor(actorName, config);
                if (targetDirs.Count == 0)
                {
                    continue;
                }

                var ext = Path.GetExtension(file);
                var rawName = Path.GetFileNameWithoutExtension(file);
                var isExact = string.Equals(rawName, actorName, StringComparison.OrdinalIgnoreCase);

                var wroteAny = false;
                foreach (var actorDir in targetDirs)
                {
                    Directory.CreateDirectory(actorDir);

                    string targetFile;
                    if (isExact)
                    {
                        targetFile = Path.Combine(actorDir, actorName + ext);
                        if (File.Exists(targetFile))
                        {
                            // 已存在默认图：哈希比对，相同则跳过；不同则作为附加图（不覆盖默认）
                            if (HashesEqual(file, targetFile))
                            {
                                result.DuplicateSkipped++;
                                continue;
                            }

                            var counter = 2;
                            targetFile = Path.Combine(actorDir, actorName + "_" + counter + ext);
                            while (File.Exists(targetFile))
                            {
                                counter++;
                                targetFile = Path.Combine(actorDir, actorName + "_" + counter + ext);
                            }
                        }
                    }
                    else
                    {
                        // 附加图：与已有照片哈希比对去重
                        var existing = Directory.EnumerateFiles(actorDir)
                            .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                            .FirstOrDefault(f => HashesEqual(f, file));
                        if (existing is not null)
                        {
                            result.DuplicateSkipped++;
                            continue;
                        }

                        var counter = 2;
                        targetFile = Path.Combine(actorDir, actorName + "_" + counter + ext);
                        while (File.Exists(targetFile))
                        {
                            counter++;
                            targetFile = Path.Combine(actorDir, actorName + "_" + counter + ext);
                        }
                    }

                    try
                    {
                        File.Copy(file, targetFile, overwrite: false);
                        wroteAny = true;
                        _logger.LogInformation("LocalMediaAssets：外部库照片已补充 {Source} → {Target}", file, targetFile);
                    }
                    catch (Exception ex)
                    {
                        result.Errors++;
                        _logger.LogWarning(ex, "LocalMediaAssets：补充外部库照片失败 {File}", file);
                    }
                }

                if (wroteAny)
                {
                    copied++;
                }
            }
            catch (Exception ex)
            {
                result.Errors++;
                _logger.LogWarning(ex, "LocalMediaAssets：补充外部库照片失败 {File}", file);
            }
        }

        return copied;
    }

    /// <summary>
    /// 某演员的目标写入目录：Library 模式 → 演员库 People/&lt;首字&gt;/；
    /// VideoOnly → 该演员参演的所有视频 actors/ 目录。
    /// </summary>
    private List<string> GetTargetDirsForActor(string actorName, PluginConfiguration config)
    {
        var dirs = new List<string>();
        var mode = config.StorageMode ?? "VideoOnly";

        if (mode is "Video+Library" or "LibraryOnly")
        {
            if (!string.IsNullOrWhiteSpace(config.ActorLibraryPath))
            {
                var libDir = Path.Combine(
                    config.ActorLibraryPath.Trim(),
                    PluginConfiguration.IsValidFolderName(config.PeopleFolderName) ? config.PeopleFolderName.Trim() : "People");
                dirs.Add(Path.Combine(libDir, ActorDatabase.GetSubDir(actorName)));
            }
        }
        else if (mode == "VideoOnly")
        {
            foreach (var d in GetVideoDirsForActor(actorName))
            {
                dirs.Add(Path.Combine(d, _index.GetActorFolderNameFor(config) ?? "actors"));
            }
        }

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 从 Jellyfin 刮削源补充照片：本地（可写来源）无照片时，把 Jellyfin 自身存储的
    /// Person 图（metadata/People/...）复制进主存储目标目录（严格同名 = 设为默认）。
    /// 尊重删除墓碑（被删过的图不回加）。
    /// </summary>
    private int SyncFromJellyfin(PluginConfiguration config, SyncResult result)
    {
        if (!config.JellyfinSourceEnabled)
        {
            return 0;
        }

        var copied = 0;
        foreach (var name in _index.ListActorNames(config))
        {
            try
            {
                var photos = _index.FindPhotos(name, config);
                var hasLocal = photos.Any(p => p.Source is ActorSource.Library or ActorSource.Video
                    && !string.IsNullOrEmpty(p.Path) && File.Exists(p.Path));
                if (hasLocal)
                {
                    continue; // 本地已有照片：Jellyfin 只是兜底来源
                }

                var jf = photos.FirstOrDefault(p => p.Source == ActorSource.Jellyfin
                    && !string.IsNullOrEmpty(p.Path) && File.Exists(p.Path));
                if (jf is null || string.IsNullOrEmpty(jf.Path))
                {
                    continue;
                }

                // 墓碑：该图被用户删过则跳过（避免回加）
                if (_deletedPhotos.IsDeleted(name, jf.Path))
                {
                    result.TombstoneSkipped++;
                    continue;
                }

                var dirs = GetTargetDirsForActor(name, config);
                if (dirs.Count == 0)
                {
                    continue;
                }

                var dir = dirs[0];
                Directory.CreateDirectory(dir);

                var safeName = ActorDatabase.SafeFileName(name);
                var ext = Path.GetExtension(jf.Path);
                if (string.IsNullOrEmpty(ext))
                {
                    ext = ".jpg";
                }

                var target = Path.Combine(dir, safeName + ext);
                if (File.Exists(target))
                {
                    continue;
                }

                File.Copy(jf.Path, target, overwrite: false);
                _logger.LogInformation("LocalMediaAssets：从 Jellyfin 刮削源同步演员照片 {Name} → {Target}", name, target);
                copied++;
                result.AddedPhotos++;
            }
            catch (Exception ex)
            {
                result.Errors++;
                _logger.LogDebug(ex, "LocalMediaAssets：从 Jellyfin 源同步失败 {Name}", name);
            }
        }

        return copied;
    }

    /// <summary>
    /// 自愈：默认照片缺失时从剩余照片重设（写入严格同名 + 更新信息标记）。
    /// </summary>
    private int RepairMissingDefaults(PluginConfiguration config, SyncResult result)
    {
        var fixedCount = 0;
        var names = _index.ListActorNames(config).ToList();
        foreach (var name in names)
        {
            try
            {
                var photos = _index.FindPhotos(name, config);
                var hasExact = photos.Any(p => !string.IsNullOrEmpty(p.Path)
                    && ActorDatabase.IsExactDefaultName(p.Path, name)
                    && p.Source is ActorSource.Library or ActorSource.Video);
                if (hasExact)
                {
                    continue;
                }

                // 无默认照片：从可写来源照片中取最大者为默认
                var candidate = photos
                    .Where(p => !string.IsNullOrEmpty(p.Path) && File.Exists(p.Path)
                        && p.Source is ActorSource.Library or ActorSource.Video)
                    .OrderByDescending(p => p.SizeBytes)
                    .FirstOrDefault();
                if (candidate is null || string.IsNullOrEmpty(candidate.Path))
                {
                    continue;
                }

                var dir = Path.GetDirectoryName(candidate.Path) ?? string.Empty;
                var ext = Path.GetExtension(candidate.Path);
                var defaultPath = Path.Combine(dir, ActorDatabase.SafeFileName(name) + ext);
                if (!string.Equals(candidate.Path, defaultPath, StringComparison.OrdinalIgnoreCase) && !File.Exists(defaultPath))
                {
                    File.Move(candidate.Path, defaultPath, overwrite: false);
                    _logger.LogInformation("LocalMediaAssets：默认照片缺失自愈 {Name} → {Default}", name, defaultPath);
                    fixedCount++;
                }
            }
            catch (Exception ex)
            {
                result.Errors++;
                _logger.LogDebug(ex, "LocalMediaAssets：默认照片自愈失败 {Name}", name);
            }
        }

        return fixedCount;
    }

    /// <summary>
    /// 按存储模式分发默认照片+信息到各视频目录 actors/（跟随视频走）。
    /// </summary>
    private void DistributeToVideos(PluginConfiguration config, SyncResult result)
    {
        var mode = config.StorageMode ?? "Video+Library";
        if (mode == "LibraryOnly")
        {
            return; // 只存演员库，不分发视频目录
        }

        // 收集所有演员的默认照片 + 信息，按演员名分发到其参演视频的 actors/
        var actors = CollectActors(config);
        foreach (var (actorName, defaultPhotoPath, infoJson) in actors)
        {
            try
            {
                var videoDirs = GetVideoDirsForActor(actorName);
                foreach (var videoDir in videoDirs)
                {
                    var actorsDir = Path.Combine(videoDir, _index.GetActorFolderNameFor(config) ?? "actors");
                    Directory.CreateDirectory(actorsDir);

                    // 默认照片：严格同名复制（已存在且内容相同则跳过）
                    if (!string.IsNullOrEmpty(defaultPhotoPath) && File.Exists(defaultPhotoPath))
                    {
                        var safeName = ActorDatabase.SafeFileName(actorName);
                        var ext = Path.GetExtension(defaultPhotoPath);
                        var target = Path.Combine(actorsDir, safeName + ext);
                        if (!File.Exists(target))
                        {
                            File.Copy(defaultPhotoPath, target, overwrite: false);
                        }
                        else if (!HashesEqual(defaultPhotoPath, target))
                        {
                            // 视频目录有不同默认图：以演员库为权威，覆盖
                            File.Copy(defaultPhotoPath, target, overwrite: true);
                        }
                    }

                    // 信息文件
                    if (!string.IsNullOrEmpty(infoJson))
                    {
                        var infoTarget = Path.Combine(actorsDir, ActorDatabase.SafeFileName(actorName) + ".json");
                        var existing = File.Exists(infoTarget) ? File.ReadAllText(infoTarget) : string.Empty;
                        if (!string.Equals(existing, infoJson, StringComparison.Ordinal))
                        {
                            File.WriteAllText(infoTarget, infoJson);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors++;
                _logger.LogDebug(ex, "LocalMediaAssets：分发演员素材失败 {Name}", actorName);
            }
        }
    }

    private List<(string Name, string? DefaultPhoto, string? InfoJson)> CollectActors(PluginConfiguration config)
    {
        var list = new List<(string, string?, string?)>();
        foreach (var name in _index.ListActorNames(config))
        {
            var def = _index.FindDefaultPhoto(name, config);
            var infoFile = _index.FindInfoFile(name, config);
            string? infoJson = null;
            if (!string.IsNullOrEmpty(infoFile) && File.Exists(infoFile))
            {
                try
                {
                    infoJson = File.ReadAllText(infoFile);
                }
                catch
                {
                    // 忽略
                }
            }

            list.Add((name, def?.Path, infoJson));
        }

        return list;
    }

    /// <summary>
    /// 某演员参演的所有视频目录（影片/剧集/剧集季根目录）。
    /// </summary>
    private List<string> GetVideoDirsForActor(string actorName)
    {
        var dirs = new List<string>();
        try
        {
            var person = _libraryManager.GetPerson(actorName);
            if (person is null)
            {
                return dirs;
            }

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                PersonIds = [person.Id],
                IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Series, Jellyfin.Data.Enums.BaseItemKind.Episode]
            });

            foreach (var item in items)
            {
                var dir = item is MediaBrowser.Controller.Entities.Folder ? item.Path : Path.GetDirectoryName(item.Path);
                if (!string.IsNullOrEmpty(dir) && !dirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                {
                    dirs.Add(dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LocalMediaAssets：查询演员参演视频失败 {Name}", actorName);
        }

        return dirs;
    }

    private static IEnumerable<string> EnumerateStagingFiles(string staging, PluginConfiguration config)
    {
        var list = new List<string>();
        try
        {
            list.AddRange(Directory.EnumerateFiles(staging)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)));
        }
        catch
        {
            // 忽略
        }

        var peopleDir = Path.Combine(staging, PluginConfiguration.IsValidFolderName(config.PeopleFolderName) ? config.PeopleFolderName.Trim() : "People");
        if (Directory.Exists(peopleDir))
        {
            try
            {
                list.AddRange(Directory.EnumerateFiles(peopleDir, "*", SearchOption.AllDirectories)
                    .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)));
            }
            catch
            {
                // 忽略
            }
        }

        return list;
    }

    private static bool HashesEqual(string a, string b)
    {
        try
        {
            return string.Equals(DeletedPhotoStore.ComputeHash(a), DeletedPhotoStore.ComputeHash(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetBaseName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
        var idx = name.LastIndexOf('_');
        if (idx > 0 && int.TryParse(name[(idx + 1)..], out _))
        {
            return name[..idx];
        }

        return name;
    }
}
