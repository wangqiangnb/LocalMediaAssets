using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.LocalMediaAssets.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 演员数据来源。
/// </summary>
public enum ActorSource
{
    /// <summary>主演员库（权威，可写）。</summary>
    Library = 0,

    /// <summary>视频目录 actors/（主存储，跟随视频，可写）。</summary>
    Video = 1,

    /// <summary>备用演员库（只读补充源）。</summary>
    Staging = 2,

    /// <summary>Jellyfin Person 数据（只读引用，其他插件刮削结果）。</summary>
    Jellyfin = 3
}

/// <summary>
/// 索引中的单个照片条目。
/// </summary>
public sealed class ActorPhotoEntry
{
    /// <summary>文件路径（Jellyfin 源可能为图片 URL 或空，配合 <see cref="ImageUrl"/>）。</summary>
    public string? Path { get; set; }

    /// <summary>来源。</summary>
    public ActorSource Source { get; set; }

    /// <summary>文件大小（字节）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>内容哈希（SHA256，缓存，去重用）。</summary>
    public string? Sha256 { get; set; }

    /// <summary>Jellyfin 图片 URL（Source=Jellyfin 时）。</summary>
    public string? ImageUrl { get; set; }
}

/// <summary>
/// 演员统一索引（单索引，四来源聚合）：
/// - Library（主演员库）/ Video（视频目录 actors/）/ Staging（备用库）/ Jellyfin（刮削引用）
/// - 内存索引秒查；磁盘缓存 sourceindex.json（含各来源 mtime 快照，支持增量更新）
/// - 服务「取用」（详情页美化/头像/介绍）与「更新」（同步引擎增量扫描）
/// </summary>
public sealed class ActorIndex
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".tbn", ".gif", ".svg"];
    private static readonly string[] InfoExtensions = [".json"];

    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN",
        "System Volume Information",
        ".git",
        ".svn",
        ".tmp",
        "node_modules",
        "metadata",
        "cache",
        "backdrops",
        "extrafanart",
        "trailers",
        ".actors"
    };

    private static readonly HashSet<string> ArtworkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "poster", "folder", "fanart", "landscape", "backdrop", "backdrop1", "backdrop2", "backdrop3",
        "thumb", "logo", "clearart", "clearlogo", "banner", "box", "boxrear", "disc", "menu", "keyart",
        "primary", "icon", "default"
    };

    private static readonly string[] ArtworkSuffixes =
    [
        "poster", "fanart", "landscape", "backdrop", "thumb", "logo", "clearart", "clearlogo",
        "banner", "box", "boxrear", "disc", "menu", "keyart"
    ];

    // 磁盘缓存新鲜度窗口：进程重启后缓存在此窗口内且来源 mtime 未变则直接加载
    private static readonly TimeSpan DiskCacheWindow = TimeSpan.FromMinutes(5);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ActorIndex> _logger;
    private readonly object _sync = new();

    // Jellyfin 自身存储的 Person 数据目录（InternalMetadataPath/People/<首字母>/<演员名>/）
    private readonly string _peoplePath;

    private Dictionary<string, List<ActorPhotoEntry>> _index = new(StringComparer.Ordinal);
    private Dictionary<string, string> _infoFiles = new(StringComparer.Ordinal); // actorName -> json 路径
    private Dictionary<string, DateTime> _sourceMtimes = new(StringComparer.Ordinal); // 来源根 -> mtime
    private string _actorLibraryPath = string.Empty;
    private string _actorFolderName = "actors";
    private string _peopleFolderName = "People";
    private DateTime _builtAtUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActorIndex"/> class.
    /// </summary>
    public ActorIndex(ILibraryManager libraryManager, IApplicationPaths applicationPaths, ILogger<ActorIndex> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        try
        {
            // Jellyfin 的 metadata 目录 = ProgramDataPath/metadata（含 People 子目录）
            _peoplePath = Path.Combine(applicationPaths.ProgramDataPath, "metadata", "People");
        }
        catch
        {
            _peoplePath = string.Empty;
        }
    }

    // ---------------- 查询（取用） ----------------

    /// <summary>
    /// 读取可用的来源集合（读取只从目标位置：演员库 和/或 视频目录 actors/；
    /// 备用库、Jellyfin 刮削目录仅作为同步的输入源，不直接读取）。
    /// </summary>
    private static HashSet<ActorSource> GetReadSources(PluginConfiguration config)
    {
        var mode = config.StorageMode ?? "VideoOnly";
        var set = new HashSet<ActorSource>();
        if (mode == "Disabled")
        {
            return set;
        }

        if (mode is "VideoOnly" or "Video+Library")
        {
            set.Add(ActorSource.Video);
        }

        if (mode is "Video+Library" or "LibraryOnly")
        {
            set.Add(ActorSource.Library);
        }

        return set;
    }

    /// <summary>
    /// 返回演员的全部照片（默认图排最前；只从目标位置读取；文件已删的过滤掉）。
    /// 命名规范：&lt;演员名&gt;.jpg|png|webp 为默认；&lt;演员名&gt;_N 为附加。
    /// </summary>
    public IReadOnlyList<ActorPhotoEntry> FindPhotos(string name, PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        EnsureFresh(config);
        var key = Normalize(name);
        var readSources = GetReadSources(config);
        lock (_sync)
        {
            if (!_index.TryGetValue(key, out var list) || list.Count == 0)
            {
                return [];
            }

            // 只保留目标位置（可读来源）的照片；过滤已删文件
            var valid = list
                .Where(p => readSources.Contains(p.Source) && !string.IsNullOrEmpty(p.Path) && File.Exists(p.Path))
                .ToList();
            if (valid.Count == 0)
            {
                return [];
            }

            // 排序：默认图（严格同名）优先 → 按来源优先级 → 路径
            return valid
                .OrderBy(p => IsExactDefault(p, name) ? 0 : 1)
                .ThenBy(p => (int)p.Source)
                .ThenBy(p => p.Path ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// 返回某演员的全部照片（所有来源，含备用库/Jellyfin 刮削目录）。
    /// 仅供同步引擎发现输入源使用；日常读取请用 <see cref="FindPhotos"/>（只读目标位置）。
    /// </summary>
    public IReadOnlyList<ActorPhotoEntry> FindSourcePhotos(string name, PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        EnsureFresh(config);
        var key = Normalize(name);
        lock (_sync)
        {
            if (!_index.TryGetValue(key, out var list) || list.Count == 0)
            {
                return [];
            }

            return list
                .Where(p => !string.IsNullOrEmpty(p.Path) && File.Exists(p.Path))
                .OrderBy(p => (int)p.Source)
                .ThenBy(p => p.Path ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// 返回默认照片（严格同名优先；无则按文件大小最大者；只从目标位置读取）。
    /// 多张严格同名（不同扩展名，如 .jpg + .webp）时按扩展名优先级取一张为默认。
    /// </summary>
    public ActorPhotoEntry? FindDefaultPhoto(string name, PluginConfiguration config)
    {
        var photos = FindPhotos(name, config);
        if (photos.Count == 0)
        {
            return null;
        }

        var exact = photos.Where(p => IsExactDefault(p, name)).ToList();
        if (exact.Count > 0)
        {
            return exact
                .OrderBy(p => ExtensionPriority(p.Path))
                .ThenByDescending(p => p.SizeBytes)
                .First();
        }

        // 无严格同名：取文件最大者
        return photos
            .OrderByDescending(p => p.SizeBytes)
            .First();
    }

    // 扩展名优先级：jpg 最常用作默认，其次 jpeg/png，webp 等靠后
    private static int ExtensionPriority(string? path)
    {
        var ext = Path.GetExtension(path);
        return ext?.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => 0,
            ".png" => 1,
            ".webp" => 2,
            _ => 3
        };
    }

    /// <summary>
    /// 返回演员信息文件路径（.json）。
    /// </summary>
    public string? FindInfoFile(string name, PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        EnsureFresh(config);
        var key = Normalize(name);
        lock (_sync)
        {
            if (_infoFiles.TryGetValue(key, out var file) && !string.IsNullOrEmpty(file) && File.Exists(file))
            {
                return file;
            }

            return null;
        }
    }

    /// <summary>
    /// 全部演员名。
    /// </summary>
    public IReadOnlyList<string> ListActorNames(PluginConfiguration config)
    {
        EnsureFresh(config);
        lock (_sync)
        {
            return _index.Keys.ToList();
        }
    }

    /// <summary>
    /// 返回当前视频演员文件夹名（未重建索引时按配置计算）。
    /// </summary>
    public string GetActorFolderNameFor(PluginConfiguration config)
    {
        var name = PluginConfiguration.IsValidFolderName(config.ActorFolderName) ? config.ActorFolderName.Trim() : "actors";
        lock (_sync)
        {
            if (!string.IsNullOrEmpty(_actorFolderName) && _actorFolderName != "actors")
            {
                return _actorFolderName;
            }

            _actorFolderName = name;
            return name;
        }
    }

    // ---------------- 索引构建（更新） ----------------

    /// <summary>
    /// 使索引缓存失效（下次访问全量重建）。文件变化（增删/设默认）后调用。
    /// </summary>
    public void InvalidateCache()
    {
        lock (_sync)
        {
            _index = new Dictionary<string, List<ActorPhotoEntry>>(StringComparer.Ordinal);
            _infoFiles = new Dictionary<string, string>(StringComparer.Ordinal);
            _sourceMtimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            _builtAtUtc = default;

            try
            {
                var cacheFile = CacheFilePath();
                if (File.Exists(cacheFile))
                {
                    File.Delete(cacheFile);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LocalMediaAssets：索引缓存删除失败");
            }

            _logger.LogInformation("LocalMediaAssets：索引缓存已失效，下次访问将全量重建");
        }
    }

    /// <summary>
    /// 立即全量重建索引（四来源聚合）。
    /// </summary>
    public int Rebuild(PluginConfiguration config)
    {
        lock (_sync)
        {
            _actorLibraryPath = config.ActorLibraryPath?.Trim() ?? string.Empty;
            _actorFolderName = PluginConfiguration.IsValidFolderName(config.ActorFolderName) ? config.ActorFolderName.Trim() : "actors";
            _peopleFolderName = PluginConfiguration.IsValidFolderName(config.PeopleFolderName) ? config.PeopleFolderName.Trim() : "People";

            var index = new Dictionary<string, List<ActorPhotoEntry>>(StringComparer.Ordinal);
            var infoFiles = new Dictionary<string, string>(StringComparer.Ordinal);
            var mtimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);

            // 存储模式决定参与扫描的来源（Disabled 时索引为空）
            var mode = config.StorageMode ?? "VideoOnly";
            if (mode != "Disabled")
            {
                // Video：媒体库根目录树中的 actors/ 目录（VideoOnly / Video+Library）
                if (mode is "VideoOnly" or "Video+Library")
                {
                    foreach (var root in GetLibraryRoots())
                    {
                        ScanVideoTree(root, index, infoFiles, mtimes);
                    }
                }

                // Library：主演员库 People/（含首字分层）（Video+Library / LibraryOnly）
                if (mode is "Video+Library" or "LibraryOnly")
                {
                    ScanLibraryFolder(index, infoFiles, mtimes);
                }

                // Staging：备用库（平铺 + People 分层）—— 图片来源，任何非禁用模式都参与
                ScanStagingFolder(config, index, infoFiles, mtimes);

                // Jellyfin：Jellyfin 自身存储的 Person 刮削结果（只读引用，照片来源之一）
                if (config.JellyfinSourceEnabled)
                {
                    ScanJellyfinPeople(index, mtimes);
                }
            }

            _index = index;
            _infoFiles = infoFiles;
            _sourceMtimes = mtimes;
            _builtAtUtc = DateTime.UtcNow;

            SaveToDisk(config);

            _logger.LogInformation(
                "LocalMediaAssets 索引重建完成：共 {Count} 个演员名，演员库 {Library}，备用库 {Staging}",
                index.Count,
                string.IsNullOrEmpty(_actorLibraryPath) ? "(未配置)" : _actorLibraryPath,
                string.IsNullOrEmpty(config.ActorStagingPath) ? "(未配置)" : config.ActorStagingPath);

            return index.Count;
        }
    }

    /// <summary>
    /// 确保索引新鲜：进程内首次加载磁盘缓存（mtime 校验）；之后 TTL 内直接复用，
    /// TTL 外对比来源 mtime，仅重扫变化的来源。
    /// </summary>
    private void EnsureFresh(PluginConfiguration config)
    {
        lock (_sync)
        {
            var newLibrary = config.ActorLibraryPath?.Trim() ?? string.Empty;
            var newActorFolder = PluginConfiguration.IsValidFolderName(config.ActorFolderName) ? config.ActorFolderName.Trim() : "actors";
            var newPeopleFolder = PluginConfiguration.IsValidFolderName(config.PeopleFolderName) ? config.PeopleFolderName.Trim() : "People";
            var configChanged = !string.Equals(_actorLibraryPath, newLibrary, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_actorFolderName, newActorFolder, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_peopleFolderName, newPeopleFolder, StringComparison.OrdinalIgnoreCase);

            if (_builtAtUtc == default)
            {
                if (!configChanged && TryLoadFromDisk(config))
                {
                    return;
                }

                Rebuild(config);
                return;
            }

            if (configChanged)
            {
                Rebuild(config);
                return;
            }

            // TTL 内直接复用；TTL 外先做来源目录 mtime 快速检查：全部未变则续期复用，
            // 避免每次详情页访问都触发整库树遍历（大媒体库下开销大）
            if (DateTime.UtcNow - _builtAtUtc <= DiskCacheWindow)
            {
                return;
            }

            if (SourcesUnchanged())
            {
                _builtAtUtc = DateTime.UtcNow;
                SaveToDisk(config);
                return;
            }

            Rebuild(config);
        }
    }

    /// <summary>
    /// 快速检查所有已索引来源目录的最后写入时间是否与快照一致（不做遍历）。
    /// 任意目录被改动（增删文件/子目录）都会使该目录 mtime 变化，从而触发重建。
    /// </summary>
    private bool SourcesUnchanged()
    {
        lock (_sync)
        {
            if (_sourceMtimes.Count == 0)
            {
                return false;
            }

            foreach (var kv in _sourceMtimes)
            {
                try
                {
                    if (!Directory.Exists(kv.Key) || Directory.GetLastWriteTimeUtc(kv.Key) != kv.Value)
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }
    }

    // ---------------- 来源扫描 ----------------

    private IEnumerable<string> GetLibraryRoots()
    {
        try
        {
            var folders = _libraryManager.GetVirtualFolders();
            return folders
                .Where(f => f.Locations is not null)
                .SelectMany(f => f.Locations)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：获取媒体库根目录失败");
            return [];
        }
    }

    /// <summary>
    /// 遍历媒体库根目录树，索引 actors/ 目录中的照片与信息文件。
    /// </summary>
    private void ScanVideoTree(
        string root,
        Dictionary<string, List<ActorPhotoEntry>> index,
        Dictionary<string, string> infoFiles,
        Dictionary<string, DateTime> mtimes)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        var stack = new Stack<string>();
        stack.Push(root);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string resolved;
            try
            {
                var linkTarget = new DirectoryInfo(dir).ResolveLinkTarget(returnFinalTarget: true);
                resolved = linkTarget?.FullName ?? Path.GetFullPath(dir);
            }
            catch
            {
                resolved = Path.GetFullPath(dir);
            }

            if (!visited.Add(resolved))
            {
                continue;
            }

            var name = Path.GetFileName(dir);

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "无法枚举目录 {Dir}", dir);
                subDirs = [];
            }

            if (name.Equals(_actorFolderName, StringComparison.OrdinalIgnoreCase))
            {
                // actors/ 目录：索引其文件与一层子目录（People 分层）
                IndexFolderFiles(dir, ActorSource.Video, index, infoFiles, includeSubLevel: true);
                TryRecordMtime(mtimes, dir);
            }

            foreach (var sub in subDirs)
            {
                var subName = Path.GetFileName(sub);
                if (SkippedDirectoryNames.Contains(subName))
                {
                    continue;
                }

                // 不进入主演员库目录（避免重复索引）
                if (!string.IsNullOrEmpty(_actorLibraryPath))
                {
                    try
                    {
                        if (string.Equals(Path.GetFullPath(sub), Path.GetFullPath(_actorLibraryPath), StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        // 忽略路径解析异常
                    }
                }

                stack.Push(sub);
            }
        }
    }

    /// <summary>
    /// 索引主演员库：&lt;演员库&gt;/People/（含 People/&lt;首字&gt;/ 分层）与平铺文件。
    /// </summary>
    private void ScanLibraryFolder(
        Dictionary<string, List<ActorPhotoEntry>> index,
        Dictionary<string, string> infoFiles,
        Dictionary<string, DateTime> mtimes)
    {
        if (string.IsNullOrEmpty(_actorLibraryPath) || !Directory.Exists(_actorLibraryPath))
        {
            return;
        }

        var peopleDir = Path.Combine(_actorLibraryPath, _peopleFolderName);
        if (Directory.Exists(peopleDir))
        {
            IndexFolderFiles(peopleDir, ActorSource.Library, index, infoFiles, includeSubLevel: true);
            TryRecordMtime(mtimes, peopleDir);
        }

        // 演员库根目录直接平铺的照片（演员库/<演员名>.jpg）
        IndexFolderFiles(_actorLibraryPath, ActorSource.Library, index, infoFiles, includeSubLevel: false);
        TryRecordMtime(mtimes, _actorLibraryPath);
    }

    /// <summary>
    /// 索引备用演员库（只读补充源）：平铺 + People 分层。
    /// </summary>
    private void ScanStagingFolder(
        PluginConfiguration config,
        Dictionary<string, List<ActorPhotoEntry>> index,
        Dictionary<string, string> infoFiles,
        Dictionary<string, DateTime> mtimes)
    {
        var staging = config.ActorStagingPath?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(staging) || !Directory.Exists(staging))
        {
            return;
        }

        IndexFolderFiles(staging, ActorSource.Staging, index, infoFiles, includeSubLevel: false, includeInfo: false);
        TryRecordMtime(mtimes, staging);

        var peopleDir = Path.Combine(staging, _peopleFolderName);
        if (Directory.Exists(peopleDir))
        {
            IndexFolderFiles(peopleDir, ActorSource.Staging, index, infoFiles, includeSubLevel: true, includeInfo: false);
            TryRecordMtime(mtimes, peopleDir);
        }
    }

    /// <summary>
    /// 把某目录（及可选一层子目录）内的演员文件写入索引，键为演员名（&lt;名&gt;_N 归并）。
    /// </summary>
    private void IndexFolderFiles(
        string folder,
        ActorSource source,
        Dictionary<string, List<ActorPhotoEntry>> index,
        Dictionary<string, string> infoFiles,
        bool includeSubLevel,
        bool includeInfo = true)
    {
        AddFilesFrom(folder);

        if (includeSubLevel)
        {
            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(folder);
            }
            catch
            {
                return;
            }

            foreach (var sub in subDirs)
            {
                AddFilesFrom(sub);
            }
        }

        void AddFilesFrom(string dir)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch
            {
                return;
            }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                var isImage = ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
                var isInfo = includeInfo && InfoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
                if (!isImage && !isInfo)
                {
                    continue;
                }

                if (isImage && IsArtworkFile(file))
                {
                    continue;
                }

                var key = Normalize(GetBaseName(file));
                if (key.Length == 0)
                {
                    continue;
                }

                if (isInfo)
                {
                    infoFiles[key] = file;
                    continue;
                }

                if (!index.TryGetValue(key, out var list))
                {
                    list = [];
                    index[key] = list;
                }

                long size = 0;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch
                {
                    // 忽略
                }

                list.Add(new ActorPhotoEntry
                {
                    Path = file,
                    Source = source,
                    SizeBytes = size
                });
            }
        }
    }

    /// <summary>
    /// 索引 Jellyfin 自身存储的 Person 刮削结果：
    /// InternalMetadataPath/People/&lt;首字母&gt;/&lt;演员名&gt;/ 下的图片（poster.jpg 等）。
    /// 作为只读照片来源（本地无照片时同步到主存储）。注意：这里的 poster/folder 等命名是
    /// Jellyfin 的标准头像文件名，不能按 IsArtworkFile 过滤。
    /// </summary>
    private void ScanJellyfinPeople(
        Dictionary<string, List<ActorPhotoEntry>> index,
        Dictionary<string, DateTime> mtimes)
    {
        if (string.IsNullOrEmpty(_peoplePath) || !Directory.Exists(_peoplePath))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_peoplePath, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (!ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 演员名 = 图片所在目录名（People/<首字母>/<演员名>/ 或 People/<演员名>/）
                var dir = Path.GetDirectoryName(file);
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                var personName = Path.GetFileName(dir);
                var key = Normalize(personName);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!index.TryGetValue(key, out var list))
                {
                    list = [];
                    index[key] = list;
                }

                long size = 0;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch
                {
                    // 忽略
                }

                list.Add(new ActorPhotoEntry
                {
                    Path = file,
                    Source = ActorSource.Jellyfin,
                    SizeBytes = size
                });
            }

            TryRecordMtime(mtimes, _peoplePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LocalMediaAssets：扫描 Jellyfin People 目录失败");
        }
    }

    private void TryRecordMtime(Dictionary<string, DateTime> mtimes, string dir)
    {
        try
        {
            mtimes[dir] = Directory.GetLastWriteTimeUtc(dir);
        }
        catch
        {
            // 忽略
        }
    }

    // ---------------- 工具 ----------------

    /// <summary>
    /// 文件名（去 _N 后缀）是否严格等于演员名（默认照片）。
    /// </summary>
    private static bool IsExactDefault(ActorPhotoEntry p, string name)
    {
        if (string.IsNullOrEmpty(p.Path))
        {
            return false;
        }

        return string.Equals(Path.GetFileNameWithoutExtension(p.Path), name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 演员名（去掉附加图编号后缀）："张三_2" → "张三"。
    /// </summary>
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

    private static bool IsArtworkFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (ArtworkNames.Contains(name))
        {
            return true;
        }

        foreach (var suffix in ArtworkSuffixes)
        {
            if (name.EndsWith("-" + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string name)
        => name.Trim().ToLowerInvariant();

    /// <summary>
    /// 磁盘索引缓存路径（插件程序集目录 sourceindex.json；不用 DataFolderPath 避免影子插件目录）。
    /// </summary>
    private static string CacheFilePath()
    {
        var pluginDir = Path.GetDirectoryName(typeof(ActorIndex).Assembly.Location);
        return string.IsNullOrEmpty(pluginDir)
            ? Path.Combine(Path.GetTempPath(), "lma-sourceindex.json")
            : Path.Combine(pluginDir, "sourceindex.json");
    }

    // ---------------- 磁盘缓存 ----------------

    private void SaveToDisk(PluginConfiguration config)
    {
        try
        {
            var cache = new SourceIndexFile
            {
                SavedAtUtc = _builtAtUtc,
                ActorLibraryPath = _actorLibraryPath,
                ActorFolderName = _actorFolderName,
                PeopleFolderName = _peopleFolderName,
                SourceMtimes = _sourceMtimes,
                InfoFiles = _infoFiles,
                Items = _index
            };
            var cacheFile = CacheFilePath();
            var dir = Path.GetDirectoryName(cacheFile) ?? string.Empty;
            Directory.CreateDirectory(dir);
            var tmpFile = cacheFile + ".tmp";
            File.WriteAllText(tmpFile, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tmpFile, cacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LocalMediaAssets：索引缓存写入失败");
        }
    }

    private bool TryLoadFromDisk(PluginConfiguration config)
    {
        try
        {
            var file = CacheFilePath();
            if (!File.Exists(file))
            {
                return false;
            }

            var cache = JsonSerializer.Deserialize<SourceIndexFile>(File.ReadAllText(file));
            if (cache is null || cache.Items is null || cache.SavedAtUtc == default)
            {
                return false;
            }

            if (DateTime.UtcNow - cache.SavedAtUtc > DiskCacheWindow)
            {
                return false;
            }

            // 配置校验
            var expectedLibrary = config.ActorLibraryPath?.Trim() ?? string.Empty;
            var expectedActorFolder = PluginConfiguration.IsValidFolderName(config.ActorFolderName) ? config.ActorFolderName.Trim() : "actors";
            var expectedPeopleFolder = PluginConfiguration.IsValidFolderName(config.PeopleFolderName) ? config.PeopleFolderName.Trim() : "People";
            if (!string.Equals(cache.ActorLibraryPath, expectedLibrary, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(cache.ActorFolderName, expectedActorFolder, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(cache.PeopleFolderName, expectedPeopleFolder, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 来源根存在性校验（换机器/盘符变化时缓存作废）
            if (cache.SourceMtimes is not null)
            {
                foreach (var kv in cache.SourceMtimes)
                {
                    if (!Directory.Exists(kv.Key))
                    {
                        return false;
                    }
                }
            }

            _actorLibraryPath = expectedLibrary;
            _actorFolderName = expectedActorFolder;
            _peopleFolderName = expectedPeopleFolder;
            _index = new Dictionary<string, List<ActorPhotoEntry>>(cache.Items, StringComparer.Ordinal);
            _infoFiles = cache.InfoFiles is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(cache.InfoFiles, StringComparer.Ordinal);
            _sourceMtimes = cache.SourceMtimes is null
                ? new Dictionary<string, DateTime>(StringComparer.Ordinal)
                : new Dictionary<string, DateTime>(cache.SourceMtimes, StringComparer.Ordinal);
            _builtAtUtc = cache.SavedAtUtc;

            _logger.LogInformation("LocalMediaAssets：已从磁盘加载演员索引（{Count} 个演员名）", _index.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LocalMediaAssets：索引缓存加载失败，将重新遍历");
            return false;
        }
    }

    /// <summary>
    /// 磁盘索引缓存文件结构。
    /// </summary>
    private sealed class SourceIndexFile
    {
        public DateTime SavedAtUtc { get; set; }

        public string ActorLibraryPath { get; set; } = string.Empty;

        public string ActorFolderName { get; set; } = string.Empty;

        public string PeopleFolderName { get; set; } = string.Empty;

        public Dictionary<string, DateTime> SourceMtimes { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> InfoFiles { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<ActorPhotoEntry>> Items { get; set; } = new(StringComparer.Ordinal);
    }
}
