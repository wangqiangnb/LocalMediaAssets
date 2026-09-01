using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.LocalMediaAssets.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 为演员建立「照片/信息文件」索引，并按照「视频目录 → 演员库」的优先级查找。
/// 视频目录指每个媒体文件夹下的 actors/（或 People/）子文件夹，演员库指配置的自定义目录。
/// </summary>
public sealed class PersonImageIndexer
{
    private const int TierActorLibrary = 0;
    private const int TierVideoFolder = 1;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".tbn", ".gif", ".svg"];
    // 演员信息文件仅支持 JSON（NFO 由 Jellyfin 原生处理，这里不索引以免误报）
    private static readonly string[] InfoExtensions = [".json"];

    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN",
        "System Volume Information",
        ".git",
        ".svn",
        ".tmp",
        "node_modules",
        // Jellyfin 本地元数据目录
        "metadata",
        // 常见非演员素材目录（避免无谓遍历，也防止误读）
        "cache",
        "backdrops",
        "extrafanart",
        "trailers"
    };

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    // 磁盘索引缓存的新鲜度窗口：进程重启后若缓存不早于该时间且根目录仍存在，则直接加载跳过遍历
    private static readonly TimeSpan DiskCacheWindow = TimeSpan.FromHours(1);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PersonImageIndexer> _logger;
    private readonly object _sync = new();

    private Dictionary<string, List<Candidate>> _index = new(StringComparer.Ordinal);
    private string _actorLibraryPath = string.Empty;
    private string _actorFolderName = "actors";
    private string _peopleFolderName = "People";
    private DateTime _builtAtUtc;

    private sealed record Candidate(string Path, int Tier);

    /// <summary>
    /// Initializes a new instance of the <see cref="PersonImageIndexer"/> class.
    /// </summary>
    public PersonImageIndexer(ILibraryManager libraryManager, ILogger<PersonImageIndexer> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// 立即重建索引。库扫描结束后应调用一次。
    /// </summary>
    public void Rebuild(PluginConfiguration config)
    {
        lock (_sync)
        {
            _actorLibraryPath = config.ActorLibraryPath?.Trim() ?? string.Empty;
            _actorFolderName = PluginConfiguration.IsValidFolderName(config.ActorFolderName) ? config.ActorFolderName.Trim() : "actors";
            _peopleFolderName = PluginConfiguration.IsValidFolderName(config.PeopleFolderName) ? config.PeopleFolderName.Trim() : "People";

            var index = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
            AddLibraryFolders(index, config);
            AddActorLibraryFolder(index);
            _index = index;
            _builtAtUtc = DateTime.UtcNow;

            SaveToDisk(config);

            _logger.LogInformation(
                "LocalMediaAssets 索引重建完成：共 {Count} 个演员名，演员库目录 {Library}",
                index.Count,
                string.IsNullOrEmpty(_actorLibraryPath) ? "(未配置)" : _actorLibraryPath);
        }
    }

    /// <summary>
    /// 按优先级查找演员照片：演员库优先（默认源），其次视频目录；同层按路径排序保证确定性。
    /// </summary>
    public string? FindPersonImage(string personName, PluginConfiguration config)
    {
        if (!config.EnableActorImages || string.IsNullOrWhiteSpace(personName))
        {
            return null;
        }

        EnsureFresh(config);
        var candidates = Lookup(personName, config);
        return candidates.FirstOrDefault(c => IsImageFile(c.Path))?.Path;
    }

    /// <summary>
    /// 按优先级查找演员信息文件（.json 优先于 .nfo）。
    /// </summary>
    public string? FindPersonInfoFile(string personName, PluginConfiguration config)
    {
        if (!config.EnableActorMetadata || string.IsNullOrWhiteSpace(personName))
        {
            return null;
        }

        EnsureFresh(config);
        var candidates = Lookup(personName, config);
        return candidates
            .Where(c => IsInfoFile(c.Path))
            .OrderByDescending(c => Path.GetExtension(c.Path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Path)
            .FirstOrDefault();
    }

    private void EnsureFresh(PluginConfiguration config)
    {
        lock (_sync)
        {
            // 配置变更（演员库路径/文件夹名）后立即重建，避免使用旧索引
            var newActorLibraryPath = config.ActorLibraryPath?.Trim() ?? string.Empty;
            var newActorFolderName = PluginConfiguration.IsValidFolderName(config.ActorFolderName) ? config.ActorFolderName.Trim() : "actors";
            var newPeopleFolderName = PluginConfiguration.IsValidFolderName(config.PeopleFolderName) ? config.PeopleFolderName.Trim() : "People";
            var configChanged = !string.Equals(_actorLibraryPath, newActorLibraryPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_actorFolderName, newActorFolderName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_peopleFolderName, newPeopleFolderName, StringComparison.OrdinalIgnoreCase);

            if (_builtAtUtc == default)
            {
                // 进程内首次使用：优先加载磁盘索引缓存，跳过全库遍历
                if (!TryLoadFromDisk(config))
                {
                    Rebuild(config);
                }

                return;
            }

            if (configChanged || DateTime.UtcNow - _builtAtUtc > CacheTtl)
            {
                Rebuild(config);
            }
        }
    }

    /// <summary>
    /// 磁盘索引缓存路径（插件数据目录）。
    /// </summary>
    private static string CacheFilePath()
    {
        var baseDir = Plugin.Instance?.DataFolderPath;
        return string.IsNullOrEmpty(baseDir)
            ? Path.Combine(Path.GetTempPath(), "lma-actorindex.json")
            : Path.Combine(baseDir, "actorindex.json");
    }

    /// <summary>
    /// 把当前索引写入磁盘（尽力而为；临时文件 + 原子替换）。
    /// </summary>
    private void SaveToDisk(PluginConfiguration config)
    {
        try
        {
            var cache = new IndexCacheFile
            {
                SavedAtUtc = _builtAtUtc,
                Roots = GetLibraryRoots().ToList(),
                ActorLibraryPath = _actorLibraryPath,
                ActorFolderName = _actorFolderName,
                PeopleFolderName = _peopleFolderName,
                Items = _index
            };
            var cacheFile = CacheFilePath();
            var dir = Path.GetDirectoryName(cacheFile) ?? string.Empty;
            Directory.CreateDirectory(dir);
            var tmpFile = cacheFile + ".tmp";
            File.WriteAllText(tmpFile, JsonSerializer.Serialize(cache, CacheJsonOptions));
            File.Move(tmpFile, cacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LocalMediaAssets：索引缓存写入失败");
        }
    }

    /// <summary>
    /// 尝试从磁盘加载索引缓存。校验：文件不早于新鲜度窗口、记录的媒体库根目录仍存在。
    /// </summary>
    private bool TryLoadFromDisk(PluginConfiguration config)
    {
        try
        {
            var file = CacheFilePath();
            if (!File.Exists(file))
            {
                return false;
            }

            var cache = JsonSerializer.Deserialize<IndexCacheFile>(File.ReadAllText(file), CacheJsonOptions);
            if (cache is null || cache.Items is null || cache.SavedAtUtc == default)
            {
                return false;
            }

            // 新鲜度校验
            if (DateTime.UtcNow - cache.SavedAtUtc > DiskCacheWindow)
            {
                return false;
            }

            // 媒体库根目录存在性校验（换机器/盘符变化时缓存作废）
            if (cache.Roots is null || cache.Roots.Count == 0)
            {
                return false;
            }

            var currentRoots = GetLibraryRoots().ToList();
            if (!cache.Roots.OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(currentRoots.OrderBy(r => r, StringComparer.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (cache.Roots.Any(r => !Directory.Exists(r)))
            {
                return false;
            }

            // 配置相关字段校验：演员库路径/文件夹名变更后缓存作废
            var expectedLibraryPath = config.ActorLibraryPath?.Trim() ?? string.Empty;
            var expectedActorFolder = PluginConfiguration.IsValidFolderName(config.ActorFolderName) ? config.ActorFolderName.Trim() : "actors";
            var expectedPeopleFolder = PluginConfiguration.IsValidFolderName(config.PeopleFolderName) ? config.PeopleFolderName.Trim() : "People";
            if (!string.Equals(cache.ActorLibraryPath, expectedLibraryPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(cache.ActorFolderName, expectedActorFolder, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(cache.PeopleFolderName, expectedPeopleFolder, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 配置相关字段对齐
            _actorLibraryPath = expectedLibraryPath;
            _actorFolderName = expectedActorFolder;
            _peopleFolderName = expectedPeopleFolder;

            _index = new Dictionary<string, List<Candidate>>(cache.Items, StringComparer.Ordinal);
            _builtAtUtc = cache.SavedAtUtc;

            _logger.LogInformation("LocalMediaAssets：已从磁盘缓存加载索引（{Count} 个演员名，跳过全库遍历）", _index.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LocalMediaAssets：索引缓存加载失败，将重新遍历");
            return false;
        }
    }

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// 磁盘索引缓存文件结构。
    /// </summary>
    private sealed class IndexCacheFile
    {
        public DateTime SavedAtUtc { get; set; }

        public List<string> Roots { get; set; } = [];

        public string ActorLibraryPath { get; set; } = string.Empty;

        public string ActorFolderName { get; set; } = string.Empty;

        public string PeopleFolderName { get; set; } = string.Empty;

        public Dictionary<string, List<Candidate>> Items { get; set; } = new(StringComparer.Ordinal);
    }

    private List<Candidate> Lookup(string personName, PluginConfiguration config)
    {
        var key = Normalize(personName);
        lock (_sync)
        {
            if (!_index.TryGetValue(key, out var list) || list.Count == 0)
            {
                return [];
            }

            // 优先级：ActorLibraryPriority=true → 演员库在前；false → 视频目录在前
            var preferredTier = config.ActorLibraryPriority ? TierActorLibrary : TierVideoFolder;
            return list
                .OrderBy(c => Math.Abs(c.Tier - preferredTier))
                .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private void AddLibraryFolders(Dictionary<string, List<Candidate>> index, PluginConfiguration config)
    {
        var libraryRoots = GetLibraryRoots();
        foreach (var root in libraryRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            WalkAndIndex(root, index);
        }
    }

    private void AddActorLibraryFolder(Dictionary<string, List<Candidate>> index)
    {
        if (string.IsNullOrWhiteSpace(_actorLibraryPath) || !Directory.Exists(_actorLibraryPath))
        {
            return;
        }

        // 演员库根目录下的直接文件（演员库/<演员名>.jpg）
        IndexFolderFiles(index, _actorLibraryPath, TierActorLibrary, includeSubLevel: false);

        // 演员库/People/<演员名>.jpg（含 People/<首字母>/<演员名>.jpg 分层结构）
        var peopleDir = Path.Combine(_actorLibraryPath, _peopleFolderName);
        if (Directory.Exists(peopleDir))
        {
            IndexFolderFiles(index, peopleDir, TierActorLibrary, includeSubLevel: true);
        }
    }

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
            _logger.LogWarning(ex, "获取媒体库根目录失败");
            return [];
        }
    }

    /// <summary>
    /// 遍历目录树并建立索引：
    /// - 名为 actors/People（可配置）的文件夹：索引其文件及一层子目录；
    /// - 其余每个目录（跳过清单除外）的顶层图片也索引——这样无论演员照片放在
    ///   影片根目录、剧集根目录还是库根目录，只要文件名等于演员名就能被识别，
    ///   无需限定在 actors/ 或 People/ 文件夹内。
    /// </summary>
    private void WalkAndIndex(string root, Dictionary<string, List<Candidate>> index)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        // 若演员库位于媒体库内部，跳过其子树，避免同一批文件被双重索引
        var libraryFullPath = string.IsNullOrEmpty(_actorLibraryPath)
            ? null
            : Path.GetFullPath(_actorLibraryPath);

        // 防止符号链接/交接点成环导致死循环：按解析后的真实路径去重
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

            // actors/People 文件夹：索引其文件及一层子目录
            if (name.Equals(_actorFolderName, StringComparison.OrdinalIgnoreCase)
                || name.Equals(_peopleFolderName, StringComparison.OrdinalIgnoreCase))
            {
                IndexFolderFiles(index, dir, TierVideoFolder, includeSubLevel: true);
            }
            else
            {
                // 其余目录：顶层图片也作为候选（文件名=演员名时生效）
                AddImageFiles(index, dir, TierVideoFolder);
            }

            foreach (var sub in subDirs)
            {
                var subName = Path.GetFileName(sub);
                if (SkippedDirectoryNames.Contains(subName))
                {
                    continue;
                }

                if (libraryFullPath is not null)
                {
                    try
                    {
                        if (string.Equals(Path.GetFullPath(sub), libraryFullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // 不进入演员库目录
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
    /// 把目录顶层的图片文件写入索引（键为文件名不含扩展名，小写）。
    /// </summary>
    private static void AddImageFiles(Dictionary<string, List<Candidate>> index, string dir, int tier)
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
            if (!ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = Normalize(Path.GetFileNameWithoutExtension(file));
            if (key.Length == 0)
            {
                continue;
            }

            if (!index.TryGetValue(key, out var list))
            {
                list = [];
                index[key] = list;
            }

            list.Add(new Candidate(file, tier));
        }
    }

    /// <summary>
    /// 把某个文件夹（及可选一层子目录）内的演员文件写入索引，键为文件名（不含扩展名，小写）。
    /// </summary>
    private static void IndexFolderFiles(
        Dictionary<string, List<Candidate>> index,
        string folder,
        int tier,
        bool includeSubLevel)
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
                var key = Normalize(Path.GetFileNameWithoutExtension(file));
                if (key.Length == 0)
                {
                    continue;
                }

                if (!index.TryGetValue(key, out var list))
                {
                    list = [];
                    index[key] = list;
                }

                list.Add(new Candidate(file, tier));
            }
        }
    }

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsInfoFile(string path)
    {
        var ext = Path.GetExtension(path);
        return InfoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string name)
        => name.Trim().ToLowerInvariant();
}
