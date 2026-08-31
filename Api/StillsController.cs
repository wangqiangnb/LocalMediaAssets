using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using Jellyfin.Plugin.LocalMediaAssets.Configuration;
using Jellyfin.Plugin.LocalMediaAssets.Models;
using Jellyfin.Plugin.LocalMediaAssets.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Api;

/// <summary>
/// 剧照数据与设置页 API。
/// </summary>
[ApiController]
[Authorize]
[Route("LocalMediaAssets")]
public sealed class StillsController : ControllerBase
{
    private const string ClientScriptResource = "Jellyfin.Plugin.LocalMediaAssets.web.localmediaassets-stills.js";
    private const string ConfigPageResource = "Jellyfin.Plugin.LocalMediaAssets.web.configPage.html";
    private const string VerifyPageResource = "Jellyfin.Plugin.LocalMediaAssets.web.verifyPage.html";

    private static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg", ".tbn"];

    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly MediaBrowser.Controller.IServerApplicationHost _applicationHost;
    private readonly UserSettingsManager _userSettings;
    private readonly ILogger<StillsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StillsController"/> class.
    /// </summary>
    public StillsController(
        ILibraryManager libraryManager,
        IServerConfigurationManager serverConfigurationManager,
        MediaBrowser.Controller.IServerApplicationHost applicationHost,
        UserSettingsManager userSettings,
        ILogger<StillsController> logger)
    {
        _libraryManager = libraryManager;
        _serverConfigurationManager = serverConfigurationManager;
        _applicationHost = applicationHost;
        _userSettings = userSettings;
        _logger = logger;
    }

    /// <summary>
    /// 获取指定条目的剧照列表（匿名可访问：条目 ID 为 GUID，与官方图片接口一致）。
    /// 可选 userId 参数用于应用该用户的显示偏好。
    /// </summary>
    [HttpGet("Stills")]
    [AllowAnonymous]
    public ActionResult<StillsResult> GetStills([FromQuery] string itemId, [FromQuery] string? userId)
    {
        // 首次访问时确保 Web 注入已生效（幂等、自动恢复）
        WebPatch.EnsureApplied(_serverConfigurationManager.ApplicationPaths, _logger);

        // 禁止缓存，保证开关设置即时生效
        Response.Headers.CacheControl = "no-store";

        var item = ResolveItem(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var config = Plugin.Instance?.Configuration;
        var result = new StillsResult
        {
            ItemId = item.Id.ToString(),
            Position = string.IsNullOrWhiteSpace(config?.StillsPosition) ? "AboveCast" : config.StillsPosition,
            Lang = LanguageResolver.Resolve(config, _serverConfigurationManager.Configuration.UICulture)
        };

        if (config is null)
        {
            return result;
        }

        // 全局默认
        var showTrailers = config.ShowTrailers;
        var showStills = config.EnableStills;
        var position = result.Position;
        var lang = config.Language;
        var maxStills = config.MaxStills > 0 ? config.MaxStills : 20;

        // 应用当前用户的显示偏好（如有）：只允许应用调用者自己的偏好
        var us = ResolveUserSettings(OwnOrEmptyUserId(userId));
        if (us is not null)
        {
            if (us.EnableStills.HasValue) showStills = us.EnableStills.Value;
            if (us.ShowTrailers.HasValue) showTrailers = us.ShowTrailers.Value;
            if (!string.IsNullOrWhiteSpace(us.StillsPosition)) position = us.StillsPosition;
            if (!string.IsNullOrWhiteSpace(us.Language)) lang = us.Language;
            if (us.MaxStills.HasValue && us.MaxStills.Value > 0) maxStills = us.MaxStills.Value;
        }

        result.Position = position;
        result.Lang = LanguageResolver.Resolve(new PluginConfiguration { Language = lang }, _serverConfigurationManager.Configuration.UICulture);

        // 预览视频（预告片）：本地 trailers/ + 在线预告片
        if (showTrailers && item is IHasTrailers hasTrailers)
        {
            try
            {
                foreach (var t in hasTrailers.LocalTrailers.Where(t => t is not null && !string.IsNullOrEmpty(t.Name)))
                {
                    result.Trailers.Add(new TrailerItem
                    {
                        ItemId = t.Id.ToString(),
                        Name = t.Name,
                        IsRemote = false,
                        StreamUrl = BuildLocalStreamUrl(t)
                    });
                }

                foreach (var r in hasTrailers.RemoteTrailers.Where(r => r is not null && !string.IsNullOrEmpty(r.Url)))
                {
                    // Name 可能为空，交给前端按语言显示默认文案
                    result.Trailers.Add(new TrailerItem { Url = r.Url, Name = r.Name, IsRemote = true });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LocalMediaAssets：读取预告片失败 {Item}", item.Name);
            }
        }

        // 预览图（剧照）
        if (showStills)
        {
            var baseDir = GetBaseDir(item);
            var files = new List<string>();

            // 1) extrafanart 目录（首选剧照源）
            if (baseDir is not null)
            {
                var stillsFolderName = PluginConfiguration.IsValidFolderName(config.StillsFolderName) ? config.StillsFolderName.Trim() : "extrafanart";
                var stillsDir = Path.Combine(baseDir, stillsFolderName);
                if (Directory.Exists(stillsDir))
                {
                    files.AddRange(EnumerateImageFiles(stillsDir));
                }
            }

            // 2) 视频目录顶层的所有照片（可选）
            if (config.StillsIncludeVideoDirPhotos && baseDir is not null && Directory.Exists(baseDir))
            {
                var topLevel = EnumerateImageFiles(baseDir);
                if (!config.StillsIncludeArtworkImages)
                {
                    topLevel = topLevel.Where(f => !IsArtworkFile(f)).ToList();
                }

                files.AddRange(topLevel);
            }

            try
            {
                result.Stills = files
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    // 按文件名去重：避免 extrafanart 与视频目录顶层同名文件重复展示
                    .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .Take(maxStills)
                    .Select(f => new StillItem { Name = Path.GetFileName(f) })
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LocalMediaAssets：收集剧照失败 {Item}", item.Name);
            }
        }

        return result;
    }

    /// <summary>
    /// 获取当前登录用户的显示偏好。
    /// </summary>
    [HttpGet("usersettings")]
    [Authorize]
    public ActionResult<UserSettings> GetUserSettings()
    {
        var userId = CurrentUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        return _userSettings.Get(userId.Value) ?? new UserSettings();
    }

    /// <summary>
    /// 保存当前登录用户的显示偏好。
    /// </summary>
    [HttpPost("usersettings")]
    [Authorize]
    public IActionResult SaveUserSettings([FromBody] UserSettings? settings)
    {
        var userId = CurrentUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        _userSettings.Save(userId.Value, settings ?? new UserSettings());
        return NoContent();
    }

    private UserSettings? ResolveUserSettings(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || !Guid.TryParse(userId, out var uid))
        {
            return null;
        }

        return _userSettings.Get(uid);
    }

    private Guid? CurrentUserId()
    {
        var claim = User.FindFirst("Jellyfin-UserId");
        return claim is not null && Guid.TryParse(claim.Value, out var id) ? id : null;
    }

    /// <summary>
    /// 仅当请求的 userId 为空（匿名）或与当前登录用户一致时才返回；否则返回 null，
    /// 防止他人通过 userId 参数读取任意用户的显示偏好。
    /// </summary>
    private string? OwnOrEmptyUserId(string? requestedUserId)
    {
        if (string.IsNullOrWhiteSpace(requestedUserId))
        {
            return null;
        }

        var claim = User.FindFirst("Jellyfin-UserId");
        if (claim is null || !string.Equals(claim.Value, requestedUserId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return requestedUserId;
    }

    /// <summary>
    /// 返回单张剧照图片内容（匿名可访问，与 Jellyfin 自带图片接口一致）。
    /// </summary>
    [HttpGet("Stills/Image")]
    [AllowAnonymous]
    public IActionResult GetStillImage([FromQuery] string itemId, [FromQuery] string name)
    {
        var item = ResolveItem(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var baseDir = GetBaseDir(item);
        if (baseDir is null)
        {
            return NotFound();
        }

        // 防路径穿越：文件名必须是纯文件名
        var safeName = Path.GetFileName(name ?? string.Empty);
        if (string.IsNullOrEmpty(safeName) || !string.Equals(safeName, name, StringComparison.Ordinal))
        {
            return BadRequest();
        }

        // 只允许提供图片文件，防止通过本接口下载视频等任意文件
        if (!SupportedExtensions.Contains(Path.GetExtension(safeName), StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest();
        }

        // 依次在 extrafanart 目录与视频目录顶层查找
        var config = Plugin.Instance?.Configuration;
        var folderName = PluginConfiguration.IsValidFolderName(config?.StillsFolderName) ? config!.StillsFolderName.Trim() : "extrafanart";
        var candidates = new[] { Path.Combine(baseDir, folderName), baseDir };

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var file = Path.Combine(dir, safeName);
            if (System.IO.File.Exists(file))
            {
                // nosniff：防止 SVG 等文本型图片被当作可执行内容
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                return PhysicalFile(file, GetMimeType(safeName));
            }
        }

        return NotFound();
    }

    /// <summary>
    /// 返回剧照展示客户端脚本（匿名，供 index.html 注入）。
    /// </summary>
    [HttpGet("stillsJs")]
    [AllowAnonymous]
    public ActionResult<string> GetClientScript()
    {
        Response.Headers.CacheControl = "no-store";
        return Content(ReadResource(ClientScriptResource, out _), "application/javascript");
    }

    /// <summary>
    /// 返回插件运行统计（设置页展示用）。
    /// </summary>
    [HttpGet("stats")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<StatsResult> GetStats()
    {
        var config = Plugin.Instance?.Configuration;
        var result = new StatsResult
        {
            LibraryConfigured = config is not null && !string.IsNullOrWhiteSpace(config.ActorLibraryPath),
            ActorLibraryPath = config?.ActorLibraryPath ?? string.Empty
        };

        if (result.LibraryConfigured && config is not null)
        {
            // 根目录防呆：避免把整个磁盘当作演员库递归枚举
            string? fullLibraryPath = null;
            try
            {
                fullLibraryPath = Path.GetFullPath(config.ActorLibraryPath.Trim());
            }
            catch
            {
                // 无效路径，按未配置处理
            }

            var libRoot = fullLibraryPath is null ? null : Path.GetPathRoot(fullLibraryPath);
            if (libRoot is null
                || string.Equals(libRoot, fullLibraryPath, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(fullLibraryPath))
            {
                result.LibraryConfigured = false;
                return result;
            }

            var peopleFolderName = PluginConfiguration.IsValidFolderName(config.PeopleFolderName) ? config.PeopleFolderName.Trim() : "People";
            var peopleDir = Path.Combine(fullLibraryPath, peopleFolderName);
            if (Directory.Exists(peopleDir))
            {
                try
                {
                    var files = Directory.EnumerateFiles(peopleDir, "*", SearchOption.AllDirectories).ToList();
                    result.ActorPhotos = files.Count(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
                    result.ActorInfos = files.Count(f => Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "LocalMediaAssets：统计演员库失败 {Dir}", peopleDir);
                }
            }
        }

        try
        {
            result.PersonsInLibrary = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Person]
            }).Count;
        }
        catch
        {
            // 忽略
        }

        return result;
    }

    /// <summary>
    /// 生成本地预告片的播放 URL：浏览器兼容格式（mp4/m4v/webm/mov）直出，
    /// 其他格式（mkv/avi/wmv 等）强制转码为 h264/mp4，保证浏览器可播放。
    /// </summary>
    private static string BuildLocalStreamUrl(BaseItem trailer)
    {
        var ext = Path.GetExtension(trailer.Path).ToLowerInvariant();
        var compatible = ext is ".mp4" or ".m4v" or ".webm" or ".mov";
        var url = "/Videos/" + trailer.Id + "/stream";
        return compatible
            ? url + "?static=true"
            : url + "?container=mp4&videoCodec=h264";
    }

    /// <summary>
    /// 剧照管线验证页：模拟详情页环境，直接验证「脚本 → API → 渲染」是否正常（无需登录）。
    /// </summary>
    [HttpGet("verify")]
    [AllowAnonymous]
    public ActionResult<string> GetVerifyPage()
    {
        Response.Headers.CacheControl = "no-store";
        return Content(ReadResource(VerifyPageResource, out _), "text/html; charset=utf-8");
    }

    /// <summary>
    /// 返回插件设置页（独立自包含页面，不依赖 Jellyfin 视图框架）。
    /// </summary>
    [HttpGet("config")]
    [AllowAnonymous]
    public ActionResult<string> GetConfigPage()
    {
        Response.Headers.CacheControl = "no-store";
        return Content(ReadResource(ConfigPageResource, out _), "text/html; charset=utf-8");
    }

    /// <summary>
    /// 返回插件存储库清单：让 Jellyfin 的「存储库」能找到本插件，
    /// 从而使插件详情页正常显示「设置」按钮（原生配置入口）。
    /// </summary>
    [HttpGet("repository.json")]
    [AllowAnonymous]
    public ActionResult<IEnumerable<object>> GetRepository()
    {
        Response.Headers.CacheControl = "no-store";

        // 版本号动态生成，避免与程序集/服务器版本脱节：
        // Version = 插件程序集版本；TargetAbi = 当前 Jellyfin 服务器版本
        var pluginVersion = typeof(StillsController).Assembly.GetName().Version?.ToString() ?? "1.0.0.0";
        var targetAbi = _applicationHost.ApplicationVersion.ToString();

        var manifest = new[]
        {
            new
            {
                Name = "LocalMediaAssets",
                Id = "8f2a9d0e-3c4b-4a5f-9e6d-1b2c3d4e5f60",
                Description = "本地化媒体素材：演员照片/简介跟随媒体文件，详情页展示预览图与预告片",
                Overview = "演员照片与简介优先从视频目录与演员库读取，详情页展示预览图与预告片；提供「整理本地演员信息和照片」计划任务与 NFO 补写，换机器无需重新刮削。",
                Owner = "local",
                Category = "Metadata",
                Versions = new[]
                {
                    new
                    {
                        Version = pluginVersion,
                        TargetAbi = targetAbi,
                        Changelog = "自动同步版本号"
                    }
                }
            }
        };
        return Ok(manifest);
    }

    private string ReadResource(string resourceName, out bool found)
    {
        var assembly = typeof(StillsController).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            _logger.LogError("LocalMediaAssets：未找到内嵌资源 {Resource}", resourceName);
            found = false;
            return string.Empty;
        }

        found = true;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private BaseItem? ResolveItem(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId, out var id))
        {
            return null;
        }

        try
        {
            return _libraryManager.GetItemById(id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：解析条目失败 {ItemId}", itemId);
            return null;
        }
    }

    private string? GetBaseDir(BaseItem item)
    {
        // 文件夹类（剧集、季、合集）：Path 就是目录本身；文件类（电影、单集）：取父目录
        return item is Folder ? item.Path : Path.GetDirectoryName(item.Path);
    }

    private static IEnumerable<string> EnumerateImageFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 判断是否为 Jellyfin 海报类图片（poster/folder/landscape/backdrop/thumb/logo 等），
    /// 这些是媒体封面而非剧照，默认在自动读取视频目录照片时排除。
    /// </summary>
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

        // 支持 "-poster"、"-thumb" 等后缀命名（如 movie-poster.jpg）
        foreach (var suffix in ArtworkSuffixes)
        {
            if (name.EndsWith("-" + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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

    private static string GetMimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }
}

/// <summary>
/// 剧照列表响应。
/// </summary>
public sealed class StillsResult
{
    /// <summary>条目 ID。</summary>
    public string? ItemId { get; set; }

    /// <summary>剧照区块位置（Top / AboveOverview / AboveCast / Bottom）。</summary>
    public string? Position { get; set; }

    /// <summary>界面语言（zh / en）。</summary>
    public string? Lang { get; set; }

    /// <summary>预告片列表（本地 + 远程）。</summary>
    public List<TrailerItem> Trailers { get; set; } = [];

    /// <summary>剧照列表。</summary>
    public List<StillItem> Stills { get; set; } = [];
}

/// <summary>
/// 预告片（本地文件或在线链接）。
/// </summary>
public sealed class TrailerItem
{
    /// <summary>本地预告片条目 ID（用于播放）；远程预告片为空。</summary>
    public string? ItemId { get; set; }

    /// <summary>本地预告片的播放 URL（已按格式选择直出或转码）；远程预告片为空。</summary>
    public string? StreamUrl { get; set; }

    /// <summary>在线预告片 URL（YouTube 等）。</summary>
    public string? Url { get; set; }

    /// <summary>是否为远程（在线）预告片。</summary>
    public bool IsRemote { get; set; }

    /// <summary>预告片名称。</summary>
    public string? Name { get; set; }
}

/// <summary>
/// 单张剧照。
/// </summary>
public sealed class StillItem
{
    /// <summary>文件名。</summary>
    public string? Name { get; set; }
}

/// <summary>
/// 插件运行统计。
/// </summary>
public sealed class StatsResult
{
    /// <summary>是否已配置演员库。</summary>
    public bool LibraryConfigured { get; set; }

    /// <summary>演员库目录。</summary>
    public string? ActorLibraryPath { get; set; }

    /// <summary>演员库中的照片数。</summary>
    public int ActorPhotos { get; set; }

    /// <summary>演员库中的简介数。</summary>
    public int ActorInfos { get; set; }

    /// <summary>库中演员总数。</summary>
    public int PersonsInLibrary { get; set; }
}
