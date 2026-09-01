using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.LocalMediaAssets.Configuration;
using Jellyfin.Plugin.LocalMediaAssets.Models;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 演员数据库实现（IActorDatabase）：基于 <see cref="ActorIndex"/> 统一索引，
/// 提供照片/信息的查询与修改；同步走 <see cref="ActorSyncEngine"/>。
/// </summary>
public sealed class ActorDatabase : IActorDatabase
{
    private readonly ActorIndex _index;
    private readonly DeletedPhotoStore _deletedPhotos;
    private readonly ActorSyncEngine _syncEngine;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ActorDatabase> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActorDatabase"/> class.
    /// </summary>
    public ActorDatabase(
        ActorIndex index,
        DeletedPhotoStore deletedPhotos,
        ActorSyncEngine syncEngine,
        ILibraryManager libraryManager,
        ILogger<ActorDatabase> logger)
    {
        _index = index;
        _deletedPhotos = deletedPhotos;
        _syncEngine = syncEngine;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler<ActorChangedEventArgs>? ActorChanged;

    /// <inheritdoc />
    public ActorRecord? GetActor(string name, PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(name) || config is null)
        {
            return null;
        }

        var photos = _index.FindPhotos(name, config);
        var infoFile = _index.FindInfoFile(name, config);
        var record = new ActorRecord
        {
            Name = name,
            Photos = photos
                .Where(p => !string.IsNullOrEmpty(p.Path))
                .Select(p => p.Path!)
                .ToList()
        };

        if (!string.IsNullOrEmpty(infoFile) && File.Exists(infoFile))
        {
            try
            {
                var data = JsonSerializer.Deserialize<ActorInfoFile>(File.ReadAllText(infoFile));
                if (data is not null)
                {
                    record.Name = string.IsNullOrWhiteSpace(data.Name) ? name : data.Name;
                    record.Overview = data.Overview ?? string.Empty;
                    record.DefaultPhoto = data.DefaultPhoto;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LocalMediaAssets：读取演员信息失败 {File}", infoFile);
            }
        }

        // 默认照片：优先 JSON 标记；否则取索引默认图
        if (string.IsNullOrEmpty(record.DefaultPhoto))
        {
            var def = _index.FindDefaultPhoto(name, config);
            record.DefaultPhoto = def?.Path;
        }
        else if (!string.IsNullOrEmpty(record.DefaultPhoto) && !Path.IsPathRooted(record.DefaultPhoto))
        {
            // JSON 里存的是文件名，转换为完整路径
            var def = _index.FindDefaultPhoto(name, config);
            record.DefaultPhoto = def?.Path ?? record.DefaultPhoto;
        }

        return record;
    }

    /// <inheritdoc />
    public ActorPhotoEntry? GetDefaultPhoto(string name, PluginConfiguration config)
        => _index.FindDefaultPhoto(name, config);

    /// <inheritdoc />
    public IReadOnlyList<ActorPhotoEntry> GetPhotos(string name, PluginConfiguration config)
        => _index.FindPhotos(name, config);

    /// <inheritdoc />
    public IReadOnlyList<string> ListActorNames(PluginConfiguration config)
        => _index.ListActorNames(config);

    /// <inheritdoc />
    public IReadOnlyList<string> Search(string keyword, PluginConfiguration config, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return [];
        }

        var kw = keyword.Trim().ToLowerInvariant();
        return ListActorNames(config)
            .Where(n => n.ToLowerInvariant().Contains(kw, StringComparison.Ordinal))
            .Take(limit)
            .ToList();
    }

    /// <inheritdoc />
    public void SaveInfo(string name, string? overview, string? defaultPhotoFile, PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(name) || config is null)
        {
            return;
        }

        var safeName = SafeFileName(name);
        var infoFile = _index.FindInfoFile(name, config);

        // 无现有文件时，按存储模式决定目标位置
        if (string.IsNullOrEmpty(infoFile))
        {
            infoFile = GetPrimaryInfoTarget(safeName, config);
        }

        // 空简介且无默认照片标记更新：不写（用户手写内容优先 / 无内容可写）
        var needPhotoUpdate = !string.IsNullOrWhiteSpace(defaultPhotoFile);
        if (string.IsNullOrWhiteSpace(overview) && !needPhotoUpdate && (File.Exists(infoFile) || string.IsNullOrEmpty(infoFile)))
        {
            return;
        }

        try
        {
            var data = new ActorInfoFile
            {
                Name = name,
                Overview = string.IsNullOrWhiteSpace(overview) ? null : overview,
                DefaultPhoto = string.IsNullOrWhiteSpace(defaultPhotoFile) ? null : defaultPhotoFile
            };

            // 空简介更新默认标记时：保留已有 json 里的用户手写简介
            if (string.IsNullOrWhiteSpace(overview) && File.Exists(infoFile))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<ActorInfoFile>(File.ReadAllText(infoFile));
                    if (existing is not null && !string.IsNullOrWhiteSpace(existing.Overview))
                    {
                        data.Overview = existing.Overview;
                    }
                }
                catch
                {
                    // 忽略解析失败
                }
            }

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

            if (!string.IsNullOrEmpty(infoFile))
            {
                // 演员库/已存在文件：直接写
                if (File.Exists(infoFile) && string.Equals(File.ReadAllText(infoFile), json, StringComparison.Ordinal))
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(infoFile) ?? string.Empty);
                File.WriteAllText(infoFile, json);
                _logger.LogInformation("LocalMediaAssets：已更新演员信息 {File}", infoFile);
                Notify(name, ActorChangeType.InfoChanged);
                return;
            }

            // 无主目标（VideoOnly 模式）：分发到该演员参演的视频 actors/ 目录，跟随视频走
            var written = WriteInfoToVideoDirs(name, safeName, json, config);
            if (written > 0)
            {
                _logger.LogInformation("LocalMediaAssets：演员信息已写入 {Count} 个视频目录 actors/", written);
                Notify(name, ActorChangeType.InfoChanged);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：写入演员信息失败 {File}", infoFile);
        }
    }

    /// <summary>
    /// VideoOnly 模式：把信息文件写到该演员参演的所有视频目录 actors/（跟随视频走）。
    /// 已存在的同名 json 不覆盖（用户手写内容优先）。
    /// </summary>
    private int WriteInfoToVideoDirs(string actorName, string safeName, string json, PluginConfiguration config)
    {
        var folderName = PluginConfiguration.IsValidFolderName(config.ActorFolderName) ? config.ActorFolderName.Trim() : "actors";
        var written = 0;
        foreach (var videoDir in GetVideoDirsForActor(actorName))
        {
            try
            {
                var target = Path.Combine(videoDir, folderName, safeName + ".json");
                if (File.Exists(target))
                {
                    continue; // 已存在：不覆盖
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? string.Empty);
                File.WriteAllText(target, json);
                written++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LocalMediaAssets：写入视频目录演员信息失败 {Dir}", videoDir);
            }
        }

        return written;
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
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode]
            });

            foreach (var item in items)
            {
                var dir = item is Folder ? item.Path : Path.GetDirectoryName(item.Path);
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

    /// <summary>
    /// 该演员的全部目标目录（按存储模式）：
    /// 含演员库 → 演员库 People/&lt;首字&gt;/；非 LibraryOnly → 各参演视频 actors/。
    /// 删除/设默认等操作需在两处同步生效。
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
                dirs.Add(Path.Combine(libDir, GetSubDir(actorName)));
            }
        }

        if (mode != "LibraryOnly")
        {
            var folderName = PluginConfiguration.IsValidFolderName(config.ActorFolderName) ? config.ActorFolderName.Trim() : "actors";
            foreach (var videoDir in GetVideoDirsForActor(actorName))
            {
                dirs.Add(Path.Combine(videoDir, folderName));
            }
        }

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <inheritdoc />
    public bool SetDefaultPhoto(string name, string photoPath, PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(photoPath) || config is null)
        {
            return false;
        }

        _index.InvalidateCache();
        var photos = _index.FindPhotos(name, config).ToList();
        var target = photos.FirstOrDefault(p => string.Equals(p.Path, photoPath, StringComparison.OrdinalIgnoreCase));
        if (target is null || string.IsNullOrEmpty(target.Path) || !File.Exists(target.Path))
        {
            return false;
        }

        // 只改权威源：含演员库模式 → 以演员库为权威（视频由同步镜像跟随）；
        // 用户点的照片可能在视频目录，则改演员库中同名照片（无则复制进演员库）。
        var mode = config.StorageMode ?? "VideoOnly";
        if (mode is "Video+Library" or "LibraryOnly")
        {
            var libPhoto = photos.FirstOrDefault(p =>
                p.Source == ActorSource.Library
                && !string.IsNullOrEmpty(p.Path)
                && string.Equals(Path.GetFileName(p.Path), Path.GetFileName(target.Path), StringComparison.OrdinalIgnoreCase));
            if (libPhoto is not null && !string.IsNullOrEmpty(libPhoto.Path))
            {
                target = libPhoto;
            }
            else
            {
                var libDir = GetPrimaryPhotoTargetDir(name, config);
                if (string.IsNullOrEmpty(libDir))
                {
                    return false;
                }

                Directory.CreateDirectory(libDir);
                var copied = Path.Combine(libDir, Path.GetFileName(target.Path));
                if (!File.Exists(copied))
                {
                    File.Copy(target.Path, copied, overwrite: false);
                }

                target = photos.FirstOrDefault(p => string.Equals(p.Path, copied, StringComparison.OrdinalIgnoreCase))
                    ?? new ActorPhotoEntry { Path = copied, Source = ActorSource.Library };
            }
        }

        var effectivePath = target.Path;
        if (string.IsNullOrEmpty(effectivePath) || !File.Exists(effectivePath))
        {
            return false;
        }

        // 已是当前默认（多张严格同名不同扩展名时，只有当前选中的那张算默认）→ 无需操作
        var currentDefault = _index.FindDefaultPhoto(name, config);
        if (currentDefault is not null
            && string.Equals(currentDefault.Path, effectivePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var dir = Path.GetDirectoryName(effectivePath) ?? string.Empty;
            var ext = Path.GetExtension(effectivePath);
            if (string.IsNullOrEmpty(ext))
            {
                ext = ".jpg";
            }

            var defaultPath = Path.Combine(dir, SafeFileName(name) + ext);

            // 挪走当前默认（只挪"当前选中的默认"那张，避免误动其他严格同名照片）
            if (currentDefault is not null
                && !string.IsNullOrEmpty(currentDefault.Path)
                && File.Exists(currentDefault.Path)
                && !string.Equals(currentDefault.Path, effectivePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(currentDefault.Path), dir, StringComparison.OrdinalIgnoreCase))
            {
                var renamed = Path.Combine(dir, SafeFileName(name) + "_2" + Path.GetExtension(currentDefault.Path));
                var counter = 3;
                while (File.Exists(renamed))
                {
                    renamed = Path.Combine(dir, SafeFileName(name) + "_" + counter + Path.GetExtension(currentDefault.Path));
                    counter++;
                }

                File.Move(currentDefault.Path, renamed, overwrite: false);
                _logger.LogInformation("LocalMediaAssets：默认照片更替，旧默认 → {Renamed}", renamed);
            }

            if (!string.Equals(effectivePath, defaultPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(effectivePath, defaultPath, overwrite: true);
            }

            _logger.LogInformation("LocalMediaAssets：已设置默认照片 {Default}", defaultPath);

            SaveInfo(name, null, Path.GetFileName(defaultPath), config);
            Notify(name, ActorChangeType.PhotosChanged);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：设置默认照片失败 {Photo}", photoPath);
            return false;
        }
    }

    /// <inheritdoc />
    public bool DeletePhoto(string name, string photoPath, PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(photoPath) || config is null)
        {
            return false;
        }

        _index.InvalidateCache();
        var photos = _index.FindPhotos(name, config).ToList();
        var target = photos.FirstOrDefault(p => string.Equals(p.Path, photoPath, StringComparison.OrdinalIgnoreCase));
        if (target is null || string.IsNullOrEmpty(target.Path) || !File.Exists(target.Path))
        {
            return false;
        }

        // 只删权威源：含演员库模式 → 优先删演员库中同名照片（视频由同步镜像清理）；
        // 用户点的照片可能在视频目录，则按文件名删除演员库中的对应照片；
        // 演员库没有对应照片（视频独有的素材）→ 删除用户点的这张并记墓碑，同步不会回加。
        var mode = config.StorageMode ?? "VideoOnly";
        var effectivePath = target.Path;
        if (mode is "Video+Library" or "LibraryOnly")
        {
            var libPhoto = photos.FirstOrDefault(p =>
                p.Source == ActorSource.Library
                && !string.IsNullOrEmpty(p.Path)
                && string.Equals(Path.GetFileName(p.Path), Path.GetFileName(target.Path), StringComparison.OrdinalIgnoreCase));
            if (libPhoto is not null && !string.IsNullOrEmpty(libPhoto.Path) && File.Exists(libPhoto.Path))
            {
                effectivePath = libPhoto.Path;
            }
            else
            {
                // 视频独有的照片：删用户点的这张（权威集没有，同步按权威集清理其余副本）
                effectivePath = target.Path;
            }
        }

        // 当前默认照片不可直接删除（必须先设其他为默认）；
        // 多张严格同名不同扩展名时，只有当前选中的默认不可删，其余可删
        var currentDefault = _index.FindDefaultPhoto(name, config);
        if (currentDefault is not null
            && string.Equals(currentDefault.Path, effectivePath, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("LocalMediaAssets：默认照片不可直接删除，请先设置其他照片为默认 {Name}", name);
            return false;
        }

        try
        {
            // 先记录墓碑（文件还在），再删除，防止同步回加
            _deletedPhotos.Record(name, effectivePath);
            File.Delete(effectivePath);
            _logger.LogInformation("LocalMediaAssets：已删除演员照片（权威源）{Photo}", effectivePath);
            Notify(name, ActorChangeType.PhotosChanged);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：删除演员照片失败 {Photo}", photoPath);
            return false;
        }
    }

    /// <inheritdoc />
    public string? AddPhoto(string name, string sourcePath, PluginConfiguration config, bool makeDefault = false)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(sourcePath) || config is null || !File.Exists(sourcePath))
        {
            return null;
        }

        var targetDir = GetPrimaryPhotoTargetDir(name, config);
        if (string.IsNullOrEmpty(targetDir))
        {
            return null;
        }

        var safeName = SafeFileName(name);
        var ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(ext))
        {
            ext = ".jpg";
        }

        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, safeName + ext);

        // 目标已存在严格同名（默认图）→ 作为附加图编号
        if (File.Exists(target))
        {
            var counter = 2;
            target = Path.Combine(targetDir, safeName + "_" + counter + ext);
            while (File.Exists(target))
            {
                counter++;
                target = Path.Combine(targetDir, safeName + "_" + counter + ext);
            }
        }

        try
        {
            File.Copy(sourcePath, target, overwrite: false);
            _logger.LogInformation("LocalMediaAssets：已导入演员照片 {Source} → {Target}", sourcePath, target);

            if (makeDefault)
            {
                _index.InvalidateCache();
                SetDefaultPhoto(name, target, config);
            }
            else
            {
                Notify(name, ActorChangeType.PhotosChanged);
            }

            return target;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：导入演员照片失败 {Source}", sourcePath);
            return null;
        }
    }

    /// <inheritdoc />
    public SyncResult Sync(PluginConfiguration config, SyncTrigger trigger)
        => _syncEngine.Run(config, trigger, this);

    /// <inheritdoc />
    public void InvalidateIndex() => _index.InvalidateCache();

    /// <summary>
    /// 触发事件。
    /// </summary>
    internal void Notify(string actorName, ActorChangeType changeType)
    {
        try
        {
            ActorChanged?.Invoke(this, new ActorChangedEventArgs
            {
                ActorName = actorName,
                ChangeType = changeType
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LocalMediaAssets：ActorChanged 事件处理异常");
        }
    }

    /// <summary>
    /// 主存储信息文件目标：按存储模式（Video+Library → 演员库优先；否则视频目录）。
    /// </summary>
    private string? GetPrimaryInfoTarget(string safeName, PluginConfiguration config)
    {
        var mode = config.StorageMode ?? "Video+Library";
        if ((mode is "Video+Library" or "LibraryOnly") && !string.IsNullOrWhiteSpace(config.ActorLibraryPath))
        {
            var libDir = Path.Combine(
                config.ActorLibraryPath.Trim(),
                PluginConfiguration.IsValidFolderName(config.PeopleFolderName) ? config.PeopleFolderName.Trim() : "People");
            return Path.Combine(libDir, GetSubDir(safeName), safeName + ".json");
        }

        // VideoOnly 或演员库未配置：视频目录由同步引擎分发，这里返回空
        return null;
    }

    /// <summary>
    /// 主存储照片目标目录（按存储模式）。
    /// </summary>
    private string? GetPrimaryPhotoTargetDir(string name, PluginConfiguration config)
    {
        var safeName = SafeFileName(name);
        var mode = config.StorageMode ?? "Video+Library";
        if ((mode is "Video+Library" or "LibraryOnly") && !string.IsNullOrWhiteSpace(config.ActorLibraryPath))
        {
            var libDir = Path.Combine(
                config.ActorLibraryPath.Trim(),
                PluginConfiguration.IsValidFolderName(config.PeopleFolderName) ? config.PeopleFolderName.Trim() : "People");
            return Path.Combine(libDir, GetSubDir(safeName));
        }

        return null;
    }

    /// <summary>
    /// 演员库分组名：拉丁字母取大写首字母，中文取首字，其余归 Other。
    /// </summary>
    internal static string GetSubDir(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Other";
        }

        var c = name[0];
        if (c < 128 && char.IsLetterOrDigit(c))
        {
            return char.ToUpperInvariant(c).ToString();
        }

        return char.IsLetterOrDigit(c) ? c.ToString() : "Other";
    }

    /// <summary>
    /// 文件名是否严格等于演员名（默认照片）。
    /// </summary>
    internal static bool IsExactDefaultName(string path, string name)
        => string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase);

    internal static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// 安全文件名。
    /// </summary>
    internal static string SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        cleaned = cleaned.TrimEnd('.', ' ');
        var baseName = Path.GetFileNameWithoutExtension(cleaned);
        if (!string.IsNullOrEmpty(baseName) && WindowsReservedNames.Contains(baseName))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }
}
