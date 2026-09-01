using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LocalMediaAssets.Configuration;
using Jellyfin.Plugin.LocalMediaAssets.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using System.Xml.Linq;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 演员素材整理器：把库中演员的照片与简介同步到各视频目录 actors/ 与配置的演员库。
/// 刮削由其他插件完成，本类只做本地文件整理。
/// </summary>
public sealed class ActorAssetExporter
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    /// <summary>整理互斥锁：防止「计划任务」与「扫描后任务」同时运行导致文件写冲突。</summary>
    private static readonly SemaphoreSlim ExportLock = new(1, 1);

    static ActorAssetExporter()
    {
        // 部分图片服务会拒绝无 User-Agent 的请求
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.LocalMediaAssets/0.9.0.0");
    }

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".tbn", ".gif", ".svg"];

    private readonly ILibraryManager _libraryManager;
    private readonly PersonImageIndexer _indexer;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActorAssetExporter"/> class.
    /// </summary>
    public ActorAssetExporter(ILibraryManager libraryManager, PersonImageIndexer indexer, ILogger<ActorAssetExporter> logger)
    {
        _libraryManager = libraryManager;
        _indexer = indexer;
        _logger = logger;
    }

    /// <summary>
    /// 重建演员图片索引。
    /// </summary>
    public void RebuildIndex(PluginConfiguration config) => _indexer.Rebuild(config);

    /// <summary>
    /// 把所有演员的照片与简介同步到「演员库」目录（&lt;演员库&gt;/People/&lt;演员名&gt;.jpg + .json）。
    /// </summary>
    public async Task ExportToActorLibraryAsync(PluginConfiguration config, IProgress<double> progress, CancellationToken cancellationToken)
    {
        await ExportLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExportToActorLibraryCoreAsync(config, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExportLock.Release();
        }
    }

    private async Task ExportToActorLibraryCoreAsync(PluginConfiguration config, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var libraryPath = (config.ActorLibraryPath ?? string.Empty).Trim();

        // 防呆：演员库路径为磁盘根目录（如 C:\、/）时跳过，避免在根上创建 People 目录
        string fullLibraryPath;
        try
        {
            fullLibraryPath = Path.GetFullPath(libraryPath);
        }
        catch
        {
            _logger.LogWarning("LocalMediaAssets：演员库路径无效，已跳过导出 {Path}", libraryPath);
            return;
        }

        var root = Path.GetPathRoot(fullLibraryPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(root, fullLibraryPath, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("LocalMediaAssets：演员库路径为磁盘根目录，已跳过导出，请设置子目录（如 D:\\演员库）{Path}", fullLibraryPath);
            return;
        }

        var peopleFolderName = PluginConfiguration.IsValidFolderName(config.PeopleFolderName) ? config.PeopleFolderName.Trim() : "People";
        var targetDir = Path.Combine(fullLibraryPath, peopleFolderName);
        try
        {
            Directory.CreateDirectory(targetDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LocalMediaAssets：无法创建演员库目录 {Dir}", targetDir);
            return;
        }

        var persons = GetItems(BaseItemKind.Person);
        var total = persons.Count;
        var done = 0;

        foreach (var item in persons)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item is not Person person)
            {
                continue;
            }

            var name = SafeFileName(person.Name);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            // 按首字符分组存储：People/<首字母>/<演员名>.jpg + .json
            var subDir = GetLibrarySubDir(name);
            var actorDir = Path.Combine(targetDir, subDir);

            // 迁移旧平铺文件：People/<演员名>.* → People/<首字母>/<演员名>.*
            MigrateFlatFile(targetDir, actorDir, name);

            await EnsurePersonPhotoAsync(person, actorDir, name, cancellationToken).ConfigureAwait(false);
            WritePersonInfo(person, Path.Combine(actorDir, name + ".json"));

            done++;
            progress.Report(100.0 * done / Math.Max(1, total));
        }

        _logger.LogInformation("LocalMediaAssets：演员库同步完成，共处理 {Count} 位演员 → {Dir}", done, targetDir);
    }

    /// <summary>
    /// 演员库分组名：拉丁字母取大写首字母，中文取首字，其余归 Other。
    /// </summary>
    private static string GetLibrarySubDir(string name)
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
    /// 把旧版平铺的演员文件迁移到分组目录（目标不存在时才迁移，支持任意图片/信息扩展名）。
    /// </summary>
    private void MigrateFlatFile(string peopleDir, string actorDir, string name)
    {
        if (!Directory.Exists(peopleDir))
        {
            return;
        }

        try
        {
            foreach (var flat in Directory.EnumerateFiles(peopleDir))
            {
                var flatName = Path.GetFileNameWithoutExtension(flat);
                if (!string.Equals(flatName, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var ext = Path.GetExtension(flat);
                if (!ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
                    && !ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = Path.Combine(actorDir, flatName + ext);
                if (File.Exists(target))
                {
                    // 分组版已存在：旧平铺文件是残留副本，直接清理
                    File.Delete(flat);
                    continue;
                }

                Directory.CreateDirectory(actorDir);
                File.Move(flat, target);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：迁移旧版平铺演员文件失败 {Name}", name);
        }
    }

    /// <summary>
    /// 把演员照片/简介同步到各视频目录下的 actors/ 文件夹（文件夹类型取自身路径，文件类型取父目录）。
    /// 照片/信息以演员库为默认源：演员库有则覆盖同步，没有才回退到库中演员数据。
    /// </summary>
    public async Task ExportToVideosAsync(
        PluginConfiguration config,
        IProgress<double> progress,
        CancellationToken cancellationToken,
        bool includePhotos,
        bool includeInfo)
    {
        await ExportLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExportToVideosCoreAsync(config, progress, cancellationToken, includePhotos, includeInfo).ConfigureAwait(false);
        }
        finally
        {
            ExportLock.Release();
        }
    }

    private async Task ExportToVideosCoreAsync(
        PluginConfiguration config,
        IProgress<double> progress,
        CancellationToken cancellationToken,
        bool includePhotos,
        bool includeInfo)
    {
        var items = GetItems(BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode);
        var total = items.Count;
        var done = 0;

        // 一次性加载全部演员，避免循环内 N+1 查询
        var allPersons = LoadAllPersons();

        // 预扫描演员库默认文件：<演员库>/<PeopleFolderName> 及其首字母子目录
        string? libraryDir = null;
        if (!string.IsNullOrWhiteSpace(config.ActorLibraryPath))
        {
            try
            {
                var fullLib = Path.GetFullPath(config.ActorLibraryPath);
                var libRoot = Path.GetPathRoot(fullLib);
                // 根目录防呆：演员库路径不能是磁盘根目录
                if (!string.IsNullOrEmpty(libRoot) && !string.Equals(libRoot, fullLib, StringComparison.OrdinalIgnoreCase))
                {
                    var peopleFolder = PluginConfiguration.IsValidFolderName(config.PeopleFolderName) ? config.PeopleFolderName.Trim() : "People";
                    libraryDir = Directory.Exists(fullLib) ? Path.Combine(fullLib, peopleFolder) : null;
                }
            }
            catch
            {
                // 无效路径：视为未配置
            }
        }
        var libraryPhotos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var libraryInfos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (libraryDir is not null && Directory.Exists(libraryDir))
        {
            var scanDirs = new List<string> { libraryDir };
            try
            {
                scanDirs.AddRange(Directory.EnumerateDirectories(libraryDir));
            }
            catch
            {
                // 忽略
            }

            foreach (var d in scanDirs)
            {
                foreach (var f in Directory.EnumerateFiles(d))
                {
                    var key = Path.GetFileNameWithoutExtension(f);
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }

                    if (ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                        && !libraryPhotos.ContainsKey(key))
                    {
                        libraryPhotos[key] = f;
                    }
                    else if (Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase)
                             && !libraryInfos.ContainsKey(key))
                    {
                        libraryInfos[key] = f;
                    }
                }
            }
        }

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(item.Path))
            {
                done++;
                continue;
            }

            var baseDir = item is Folder ? item.Path : Path.GetDirectoryName(item.Path);
            if (string.IsNullOrEmpty(baseDir))
            {
                done++;
                continue;
            }

            var actorsFolderName = PluginConfiguration.IsValidFolderName(config.ActorFolderName) ? config.ActorFolderName.Trim() : "actors";
            var actorsDir = Path.Combine(baseDir, actorsFolderName);
            IReadOnlyList<PersonInfo> people;
            try
            {
                people = _libraryManager.GetPeople(item);
            }
            catch
            {
                people = [];
            }

            // 为没有 NFO 的电影/剧集补写 NFO（含演员列表），换机器后无需重新刮削 cast
            if (config.ExportNfoNextToVideo && (item is Movie || item is Series) && people.Count > 0)
            {
                WriteNfoIfMissing(item, people);
            }

            foreach (var personInfo in people)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = SafeFileName(personInfo.Name);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (!includePhotos && !includeInfo)
                {
                    continue;
                }

                // 默认源优先：演员库有 → 用它覆盖视频目录副本；没有 → 回退到库中演员数据
                if (includePhotos)
                {
                    if (libraryPhotos.TryGetValue(name, out var libraryPhoto))
                    {
                        await CopyDefaultAsync(libraryPhoto, actorsDir, name, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        allPersons.TryGetValue(personInfo.Name, out var person);
                        if (person is not null)
                        {
                            await EnsurePersonPhotoAsync(person, actorsDir, name, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                if (includeInfo)
                {
                    if (libraryInfos.TryGetValue(name, out var libraryInfo))
                    {
                        await CopyDefaultAsync(libraryInfo, actorsDir, name, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        allPersons.TryGetValue(personInfo.Name, out var person);
                        WritePersonInfo(
                            new Person { Name = personInfo.Name, Overview = person?.Overview },
                            Path.Combine(actorsDir, name + ".json"));
                    }
                }
            }

            done++;
            progress.Report(100.0 * done / Math.Max(1, total));
        }

        _logger.LogInformation("LocalMediaAssets：视频旁演员素材同步完成，共处理 {Count} 个条目", done);
    }

    /// <summary>
    /// 把默认文件（演员库版本）复制到目标目录，覆盖不一致的旧副本。
    /// </summary>
    private async Task CopyDefaultAsync(string sourceFile, string targetDir, string name, CancellationToken cancellationToken)
    {
        try
        {
            var ext = Path.GetExtension(sourceFile);
            var target = Path.Combine(targetDir, name + ext);
            var isImage = ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);

            // 一致性判断：图片按文件大小；文本按内容（避免对二进制做文本比较）
            if (File.Exists(target)
                && new FileInfo(target).Length == new FileInfo(sourceFile).Length
                && (isImage || string.Equals(File.ReadAllText(target), File.ReadAllText(sourceFile), StringComparison.Ordinal)))
            {
                return; // 已一致
            }

            Directory.CreateDirectory(targetDir);
            if (isImage)
            {
                File.Copy(sourceFile, target, overwrite: true);
                _logger.LogInformation("LocalMediaAssets：已用演员库默认照片覆盖视频目录副本 {Target}", target);
            }
            else
            {
                await File.WriteAllTextAsync(target, await File.ReadAllTextAsync(sourceFile, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("LocalMediaAssets：已用演员库默认信息覆盖视频目录副本 {Target}", target);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：同步默认文件失败 {File}", sourceFile);
        }
    }

    private IReadOnlyList<BaseItem> GetItems(params BaseItemKind[] kinds)
    {
        try
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = kinds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LocalMediaAssets：枚举条目失败");
            return [];
        }
    }

    /// <summary>
    /// 一次性加载全部演员（按姓名索引），避免循环内逐条查询。
    /// </summary>
    private Dictionary<string, Person> LoadAllPersons()
    {
        var dict = new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in GetItems(BaseItemKind.Person))
        {
            if (item is Person person && !string.IsNullOrWhiteSpace(person.Name) && !dict.ContainsKey(person.Name))
            {
                dict[person.Name] = person;
            }
        }

        return dict;
    }

    /// <summary>
    /// 为没有 NFO 的电影/剧集补写 NFO（含演员列表）。已存在则跳过，不覆盖任何现有 NFO。
    /// </summary>
    private void WriteNfoIfMissing(BaseItem item, IReadOnlyList<PersonInfo> people)
    {
        string nfoPath;
        string rootElement;
        if (item is Series)
        {
            nfoPath = Path.Combine(item.Path, "tvshow.nfo");
            rootElement = "tvshow";
        }
        else
        {
            nfoPath = Path.ChangeExtension(item.Path, ".nfo");
            rootElement = "movie";
        }

        if (string.IsNullOrEmpty(nfoPath) || File.Exists(nfoPath))
        {
            return;
        }

        try
        {
            var actors = people
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new XElement(
                    "actor",
                    new XElement("name", p.Name),
                    new XElement("role", p.Role ?? string.Empty),
                    new XElement("type", "Actor")));

            var root = new XElement(
                rootElement,
                new XElement("title", item.Name ?? string.Empty));
            if (item.ProductionYear.HasValue)
            {
                root.Add(new XElement("year", item.ProductionYear.Value));
            }

            root.Add(actors);

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
            Directory.CreateDirectory(Path.GetDirectoryName(nfoPath) ?? string.Empty);
            doc.Save(nfoPath);
            _logger.LogInformation("LocalMediaAssets：已补写 NFO（含 {Count} 位演员）{Path}", people.Count, nfoPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：补写 NFO 失败 {Path}", nfoPath);
        }
    }

    /// <summary>
    /// 确保演员照片已同步到目标目录（缺失时下载/复制，本地文件变化时更新）。
    /// </summary>
    private async Task EnsurePersonPhotoAsync(Person person, string dir, string name, CancellationToken cancellationToken)
    {
        ItemImageInfo? image;
        try
        {
            image = person.GetImageInfo(ImageType.Primary, 0);
        }
        catch
        {
            return;
        }

        if (image is null || string.IsNullOrEmpty(image.Path))
        {
            return;
        }

        var ext = GetImageExtension(image.Path);
        var target = Path.Combine(dir, name + ext);

        try
        {
            if (image.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || image.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // 远程 URL 桩：仅当本地缺失时才下载
                if (File.Exists(target))
                {
                    return;
                }

                Directory.CreateDirectory(dir);
                var bytes = await DownloadImageBytesAsync(image.Path, cancellationToken).ConfigureAwait(false);
                if (bytes is null)
                {
                    _logger.LogWarning("LocalMediaAssets：下载图片失败或 URL 不安全，已跳过 {Url}", image.Path);
                    return;
                }

                // 校验下载内容确实是图片，避免把 404/错误页存成照片
                if (!IsImageBytes(bytes))
                {
                    _logger.LogWarning("LocalMediaAssets：下载内容不是有效图片，跳过 {Url}", image.Path);
                    return;
                }

                await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("LocalMediaAssets：已下载演员照片 {Name} → {Target}", person.Name, target);
            }
            else if (File.Exists(image.Path))
            {
                var srcLength = new FileInfo(image.Path).Length;
                if (File.Exists(target) && new FileInfo(target).Length == srcLength)
                {
                    return; // 已同步
                }

                Directory.CreateDirectory(dir);
                File.Copy(image.Path, target, overwrite: true);
                _logger.LogInformation("LocalMediaAssets：已更新演员照片 {Name} → {Target}", person.Name, target);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：同步演员照片失败 {Name}", person.Name);
        }
    }

    /// <summary>
    /// 写入演员简介 JSON（内容有变化才写）。
    /// </summary>
    private void WritePersonInfo(Person person, string infoFile)
    {
        if (string.IsNullOrWhiteSpace(person.Overview) && string.IsNullOrWhiteSpace(person.Name))
        {
            return;
        }

        try
        {
            var data = new ActorInfoFile
            {
                Name = person.Name,
                Overview = person.Overview
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

            if (File.Exists(infoFile) && string.Equals(File.ReadAllText(infoFile), json, StringComparison.Ordinal))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(infoFile) ?? string.Empty);
            File.WriteAllText(infoFile, json);
            _logger.LogInformation("LocalMediaAssets：已更新演员信息 {File}", infoFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：写入演员信息失败 {File}", infoFile);
        }
    }

    private static string GetImageExtension(string path)
    {
        string ext;
        try
        {
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(path);
                ext = Path.GetExtension(uri.AbsolutePath);
            }
            else
            {
                ext = Path.GetExtension(path);
            }
        }
        catch
        {
            ext = string.Empty;
        }

        if (string.IsNullOrEmpty(ext) || !ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return ".jpg";
        }

        return ext.ToLowerInvariant();
    }

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static string SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        // 结尾的点/空格在 NTFS 上会被去除，可能导致文件名意外指向其他文件
        cleaned = cleaned.TrimEnd('.', ' ');
        // Windows 保留设备名（CON/NUL/COM1…）即使带扩展名也会被当作设备文件
        var baseName = Path.GetFileNameWithoutExtension(cleaned);
        if (!string.IsNullOrEmpty(baseName) && WindowsReservedNames.Contains(baseName))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }

    /// <summary>
    /// 安全检查：仅允许 https，且解析后的主机不能是内网/环回/链路本地/保留地址（防 SSRF）。
    /// </summary>
    private static async Task<bool> IsSafeDownloadUrlAsync(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var host = new Uri(url).Host;
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            foreach (var addr in addresses)
            {
                if (IsPrivateAddress(addr))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPrivateAddress(System.Net.IPAddress address)
    {
        if (System.Net.IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            var b0 = bytes[0];
            if (b0 == 10 || b0 == 127) return true;                                  // 10/8、127/8
            if (b0 == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;          // 172.16/12
            if (b0 == 192 && bytes[1] == 168) return true;                           // 192.168/16
            if (b0 == 169 && bytes[1] == 254) return true;                           // 169.254/16 链路本地
            if (b0 == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;         // 100.64/10 CGNAT
            if (b0 == 0 || b0 >= 224) return true;                                   // 保留/组播
        }
        else if (bytes.Length == 16)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;     // fe80::/10、fec0::/10
            if ((bytes[0] & 0xFE) == 0xFC) return true;                              // fc00::/7 ULA
            var allZero = true;
            for (var i = 0; i < 15; i++)
            {
                if (bytes[i] != 0)
                {
                    allZero = false;
                    break;
                }
            }

            if (allZero && bytes[15] == 1) return true;                              // ::1
        }

        return false;
    }

    /// <summary>
    /// 下载图片字节：手动跟随最多 3 次重定向（每次重新做安全检查），限制大小 5MB。
    /// </summary>
    private static async Task<byte[]?> DownloadImageBytesAsync(string url, CancellationToken cancellationToken)
    {
        const int MaxBytes = 5 * 1024 * 1024;
        const int MaxRedirects = 3;
        var current = url;

        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            if (!await IsSafeDownloadUrlAsync(current).ConfigureAwait(false))
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is System.Net.HttpStatusCode.MovedPermanently
                or System.Net.HttpStatusCode.Found
                or System.Net.HttpStatusCode.SeeOther
                or System.Net.HttpStatusCode.TemporaryRedirect
                or System.Net.HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                if (location is null)
                {
                    return null;
                }

                current = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(current), location).ToString();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > MaxBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    return null;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return buffer.ToArray();
        }

        return null;
    }

    /// <summary>
    /// 校验字节是否为常见图片格式（JPEG/PNG/WebP/GIF/BMP 魔数）。
    /// </summary>
    private static bool IsImageBytes(byte[] bytes)
    {
        if (bytes.Length < 8)
        {
            return false;
        }

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return true;
        }

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return true;
        }

        // GIF: 47 49 46 38
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
        {
            return true;
        }

        // WebP: 52 49 46 46 .. 57 45 42 50
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return true;
        }

        // BMP: 42 4D
        if (bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            return true;
        }

        // SVG：文本格式，要求开头含 <svg 且不含可执行脚本
        var head = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        if (head.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return head.IndexOf("<script", StringComparison.OrdinalIgnoreCase) < 0;
        }

        return false;
    }
}
