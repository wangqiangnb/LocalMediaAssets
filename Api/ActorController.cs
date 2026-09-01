using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LocalMediaAssets.Configuration;
using Jellyfin.Plugin.LocalMediaAssets.Models;
using Jellyfin.Plugin.LocalMediaAssets.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Api;

/// <summary>
/// 演员卡片 API：状态查询、触发刷新、本地信息与头像读取、前端脚本与自定义详情页。
/// 全部复用统一索引（ActorIndex）与统一数据库接口（IActorDatabase），不新建任何存储。
/// </summary>
[ApiController]
[Authorize]
[Route("LocalMediaAssets")]
public sealed class ActorController : ControllerBase
{
    private const string ActorPageResource = "Jellyfin.Plugin.LocalMediaAssets.web.actor-detail.html";

    private readonly ILibraryManager _libraryManager;
    private readonly PersonImageIndexer _indexer;
    private readonly IActorDatabase _db;
    private readonly ActorRefreshService _refreshService;
    private readonly UserSettingsManager _userSettings;
    private readonly DeletedPhotoStore _deletedPhotos;
    private readonly ILogger<ActorController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActorController"/> class.
    /// </summary>
    public ActorController(
        ILibraryManager libraryManager,
        PersonImageIndexer indexer,
        IActorDatabase db,
        ActorRefreshService refreshService,
        UserSettingsManager userSettings,
        DeletedPhotoStore deletedPhotos,
        ILogger<ActorController> logger)
    {
        _libraryManager = libraryManager;
        _indexer = indexer;
        _db = db;
        _refreshService = refreshService;
        _userSettings = userSettings;
        _deletedPhotos = deletedPhotos;
        _logger = logger;
    }

    /// <summary>
    /// 获取影片全部演员的本地素材状态（匿名：条目 ID 为 GUID，与官方接口一致）。
    /// </summary>
    [HttpGet("Actor/Status")]
    [AllowAnonymous]
    public ActionResult<ActorStatusResult> GetActorStatus([FromQuery] string itemId, [FromQuery] string? userId)
    {
        // 禁止缓存：刷新/刮削后前端轮询需立即拿到最新状态
        Response.Headers.CacheControl = "no-store";

        if (PluginState.IsDisabled())
        {
            return StatusCode(503);
        }

        var item = ResolveItem(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var config = Plugin.Instance?.Configuration;
        // 数据库是否开启（全局管理员配置）；影视详情页优化为每用户设置，未设置用内置默认
        var dbDisabled = config is null || string.Equals(config.StorageMode, "Disabled", StringComparison.OrdinalIgnoreCase);

        // 内置推荐默认（真正的默认）
        var enableActors = true;
        var actorCardsPerRow = 0;
        var stillsPerRow = 0;
        var trailersPerRow = 0;
        var sectionOrder = "Overview,Stills,Trailers,Actors";

        // 应用当前用户的影视详情页优化设置（仅允许应用调用者自己的偏好；Guid 语义比较，不区分 D/N 格式）
        if (!string.IsNullOrWhiteSpace(userId) && Guid.TryParse(userId, out var uid))
        {
            var claim = User.FindFirst("Jellyfin-UserId");
            if (claim is not null && Guid.TryParse(claim.Value, out var claimGuid) && claimGuid == uid)
            {
                var us = _userSettings.Get(uid);
                if (us is not null)
                {
                    if (us.EnableActorReplacement.HasValue) enableActors = us.EnableActorReplacement.Value;
                    if (us.ActorCardsPerRow.HasValue) actorCardsPerRow = us.ActorCardsPerRow.Value;
                    if (us.StillsPerRow.HasValue) stillsPerRow = us.StillsPerRow.Value;
                    if (us.TrailersPerRow.HasValue) trailersPerRow = us.TrailersPerRow.Value;
                    if (!string.IsNullOrWhiteSpace(us.SectionOrder)) sectionOrder = us.SectionOrder;
                }
            }
        }

        var enabled = !dbDisabled && enableActors;

        var result = new ActorStatusResult
        {
            ItemId = item.Id.ToString(),
            Enabled = enabled,
            ActorCardsPerRow = actorCardsPerRow,
            StillsPerRow = stillsPerRow,
            TrailersPerRow = trailersPerRow,
            SectionOrder = sectionOrder
        };

        IReadOnlyList<PersonInfo> people;
        try
        {
            people = _libraryManager.GetPeople(item);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：读取条目演员列表失败 {Item}", item.Name);
            return result;
        }

        foreach (var p in people.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
        {
            string? image = null;
            string? infoFile = null;
            if (config is not null)
            {
                // 全部走统一索引查询，不从磁盘实时扫描
                image = _indexer.FindPersonImage(p.Name, config);
                infoFile = _indexer.FindPersonInfoFile(p.Name, config);
            }

            var hasAvatar = !string.IsNullOrEmpty(image) && System.IO.File.Exists(image);
            var hasInfo = !string.IsNullOrEmpty(infoFile) && System.IO.File.Exists(infoFile);

            // 本地照片数（详情页照片墙可展示的内容）
            var photoCount = 0;
            try
            {
                if (config is not null)
                {
                    photoCount = _indexer.FindPersonImages(p.Name, config)
                        .Count(ph => !string.IsNullOrEmpty(ph) && System.IO.File.Exists(ph));
                }
            }
            catch
            {
                // 忽略
            }

            // 支持跳转详情页 = 存在演员信息 或 库中存在该演员参演的其他作品 或 有多张照片可展示
            var canNavigate = hasInfo || photoCount > 1 || HasOtherWorks(p.Name, item.Id);

            // Jellyfin 源兜底：本地无图且启用 Jellyfin 源时，用 Jellyfin Person 图作为头像
            string? avatarUrl = null;
            if (hasAvatar && !string.IsNullOrEmpty(image))
            {
                // 带默认图修改时间参数：设默认/替换后 URL 变化 → 浏览器立即刷新头像
                var m = 0L;
                try
                {
                    m = new FileInfo(image).LastWriteTimeUtc.Ticks;
                }
                catch
                {
                    // 忽略
                }

                avatarUrl = "/LocalMediaAssets/Actor/Image?name=" + Uri.EscapeDataString(p.Name) +
                    "&file=" + Uri.EscapeDataString(Path.GetFileName(image)) +
                    "&m=" + m;
            }

            if (avatarUrl is null && (config?.JellyfinSourceEnabled ?? false))
            {
                try
                {
                    var person = _libraryManager.GetPerson(p.Name);
                    if (person is not null && person.HasImage(ImageType.Primary))
                    {
                        avatarUrl = "/Items/" + person.Id + "/Images/Primary";
                    }
                }
                catch
                {
                    // 忽略
                }
            }

            result.Actors.Add(new ActorStatusItem
            {
                Name = p.Name,
                Role = p.Role,
                HasLocalInfo = canNavigate,
                HasAvatar = hasAvatar,
                IsRefreshing = _refreshService.IsRefreshing(p.Name),
                AvatarUrl = avatarUrl
            });
        }

        return result;
    }

    /// <summary>
    /// 库中是否存在该演员参演的其他作品（排除当前条目；当前条目未命中时按全部作品数 &gt; 1 判断）。
    /// </summary>
    private bool HasOtherWorks(string actorName, Guid currentItemId)
    {
        try
        {
            var person = _libraryManager.GetPerson(actorName);
            if (person is null)
            {
                return false;
            }

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                PersonIds = [person.Id],
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode],
                Limit = 100
            });

            if (items.Count > 1)
            {
                return true;
            }

            return items.Count == 1 && items[0].Id != currentItemId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 触发指定演员的 Jellyfin 原生元数据刷新（立即返回 202，后台执行）。
    /// 不指定 Provider、不禁用任何刮削源；刷新完成后自动落盘本地素材并重建索引。
    /// </summary>
    [HttpPost("Actor/Refresh")]
    public IActionResult RefreshActor([FromBody] RefreshActorRequest? request)
    {
        if (PluginState.IsDisabled())
        {
            return StatusCode(503);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ActorName))
        {
            return BadRequest();
        }

        _refreshService.StartRefresh(request.ActorName.Trim());
        return Accepted();
    }

    /// <summary>
    /// 手动刷新备用演员库：重建备用库索引 → 把备用库新照片补充进主演员库 → 重建主索引。
    /// 备用库只读（不修改）；设置页提示用户新增照片放备用库后点此按钮。
    /// </summary>
    [HttpPost("Actor/Staging/Sync")]
    public ActionResult<StagingSyncResult> SyncStaging()
    {
        if (PluginState.IsDisabled())
        {
            return StatusCode(503);
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return NotFound();
        }

        var stagingPath = config.ActorStagingPath?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(stagingPath))
        {
            return BadRequest();
        }

        var result = _db.Sync(config, SyncTrigger.Manual);
        return Ok(new StagingSyncResult { Copied = result.AddedPhotos });
    }

    /// <summary>
    /// 清空删除墓碑记录（被删照片将可重新从来源同步回来）。
    /// </summary>
    [HttpPost("Actor/Tombstones/Clear")]
    public IActionResult ClearTombstones()
    {
        if (PluginState.IsDisabled())
        {
            return StatusCode(503);
        }

        _deletedPhotos.Clear();
        return NoContent();
    }

    /// <summary>
    /// 设置演员默认照片（写操作：重命名文件 + 更新 JSON 标记）。
    /// </summary>
    [HttpPost("Actor/Photos/Default")]
    public IActionResult SetDefaultPhoto([FromBody] ActorPhotoRequest? request)
    {
        if (PluginState.IsDisabled())
        {
            return StatusCode(503);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.File))
        {
            return BadRequest();
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return NotFound();
        }

        // 通过索引解析目标照片真实路径（防伪造路径）
        var photos = _indexer.FindPersonImages(request.Name, config);
        var target = photos.FirstOrDefault(p => string.Equals(Path.GetFileName(p), request.File, StringComparison.OrdinalIgnoreCase));
        if (target is null || !System.IO.File.Exists(target))
        {
            return NotFound();
        }

        var ok = _db.SetDefaultPhoto(request.Name, target, config);
        if (!ok)
        {
            return BadRequest();
        }

        // 同步：把权威源（演员库）的默认变化镜像到各视频 actors/，并重建索引
        _db.Sync(config, SyncTrigger.Manual);
        return NoContent();
    }

    /// <summary>
    /// 删除演员照片（默认照片不可直接删除；删除后重建索引）。
    /// </summary>
    [HttpPost("Actor/Photos/Delete")]
    public IActionResult DeletePhoto([FromBody] ActorPhotoRequest? request)
    {
        if (PluginState.IsDisabled())
        {
            return StatusCode(503);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.File))
        {
            return BadRequest();
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return NotFound();
        }

        var photos = _indexer.FindPersonImages(request.Name, config);
        var target = photos.FirstOrDefault(p => string.Equals(Path.GetFileName(p), request.File, StringComparison.OrdinalIgnoreCase));
        if (target is null || !System.IO.File.Exists(target))
        {
            return NotFound();
        }

        var ok = _db.DeletePhoto(request.Name, target, config);
        if (!ok)
        {
            return BadRequest();
        }

        // 同步：权威源删除后，视频 actors/ 中多余的同名照片被镜像清理，并重建索引
        _db.Sync(config, SyncTrigger.Manual);
        return NoContent();
    }

    /// <summary>
    /// 返回演员完整本地信息（读取现有 actors/&lt;名&gt;.json 或演员库 json；无数据 404）。
    /// from 为来源影片 id（可选）："参演作品"只返回库中其他作品（排除当前这部）。
    /// </summary>
    [HttpGet("Actor/Detail")]
    [AllowAnonymous]
    public ActionResult<ActorDetailResult> GetActorDetail([FromQuery] string name, [FromQuery] string? from = null)
    {
        // 禁止缓存：设默认/删除等操作后 load() 重载必须拿到最新数据（否则浏览器返回旧照片列表）
        Response.Headers.CacheControl = "no-store";

        if (PluginState.IsDisabled())
        {
            return StatusCode(503);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest();
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return NotFound();
        }

        var record = _db.GetActor(name, config);
        if (record is null)
        {
            return NotFound();
        }

        var result = new ActorDetailResult
        {
            Name = record.Name,
            Overview = record.Overview,
            HasAvatar = record.Photos.Count > 0,
            PhotoUrl = "/LocalMediaAssets/Actor/Image?name=" + Uri.EscapeDataString(name),
            Photos = []
        };

        // 全部照片（默认图排最前）→ URL 列表（按文件名去重：同名照片不同路径时取优先者）
        // 每个 URL 带文件修改时间参数：设默认/删除等操作改文件后，URL 变化 → 浏览器重新拉取，
        // 避免同名文件内容变化时被本地缓存挡住（头像/照片墙立即反映）
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var photo in record.Photos)
        {
            var fname = Path.GetFileName(photo);
            if (string.IsNullOrEmpty(fname) || !seenFiles.Add(fname))
            {
                continue;
            }

            var mtime = 0L;
            try
            {
                mtime = System.IO.File.Exists(photo) ? new FileInfo(photo).LastWriteTimeUtc.Ticks : 0;
            }
            catch
            {
                // 忽略
            }

            result.Photos.Add("/LocalMediaAssets/Actor/Image?name=" + Uri.EscapeDataString(name) +
                "&file=" + Uri.EscapeDataString(fname) +
                "&m=" + mtime);
        }

        // 记录默认照片文件名（前端标记用）
        if (!string.IsNullOrEmpty(record.DefaultPhoto))
        {
            result.DefaultPhotoFile = Path.GetFileName(record.DefaultPhoto);
            // 大头照 URL 也带默认图修改时间（内容变化时强制刷新）
            try
            {
                var m = System.IO.File.Exists(record.DefaultPhoto) ? new FileInfo(record.DefaultPhoto).LastWriteTimeUtc.Ticks : 0L;
                result.PhotoUrl = "/LocalMediaAssets/Actor/Image?name=" + Uri.EscapeDataString(name) +
                    "&file=" + Uri.EscapeDataString(Path.GetFileName(record.DefaultPhoto)) +
                    "&m=" + m;
            }
            catch
            {
                // 保持默认 URL
            }
        }

        var person = _libraryManager.GetPerson(name);
        if (person is not null)
        {
            result.PersonId = person.Id.ToString();
            result.PremiereDate = person.PremiereDate;
            result.ProductionYear = person.ProductionYear;
            result.CommunityRating = person.CommunityRating;
            result.Appearances = LoadAppearances(person, from);
        }

        return result;
    }

    /// <summary>
    /// 查询库中该演员参演的影片/剧集（供详情页"参演作品"区块展示）；
    /// 传入来源影片 id 时排除当前这部（优先显示"其他作品"）；若排除后为空
    /// （该演员只参演了当前这一部）则回退显示全部，保证作品区至少有一项可展示。
    /// 复用 Jellyfin 原生 PersonIds 查询，不新建存储逻辑。
    /// </summary>
    private List<AppearanceItem> LoadAppearances(Person person, string? excludeItemId = null)
    {
        var all = new List<AppearanceItem>();
        try
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                PersonIds = [person.Id],
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode],
                Limit = 50
            });

            foreach (var item in items)
            {
                all.Add(new AppearanceItem
                {
                    Id = item.Id.ToString(),
                    Name = item.Name,
                    Type = item is MediaBrowser.Controller.Entities.Movies.Movie ? "Movie"
                        : item is MediaBrowser.Controller.Entities.TV.Series ? "Series" : "Episode",
                    ProductionYear = item.ProductionYear,
                    PosterUrl = "/Items/" + item.Id + "/Images/Primary"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：查询演员参演作品失败 {Name}", person.Name);
        }

        // 排除当前这部（跳转来源）；排除后为空则回退显示全部
        if (!string.IsNullOrWhiteSpace(excludeItemId) && Guid.TryParse(excludeItemId, out var excludeGuid))
        {
            var others = all.Where(a => !string.Equals(a.Id, excludeGuid.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
            return others.Count > 0 ? others : all;
        }

        return all;
    }

    /// <summary>
    /// 返回演员照片：默认照片（无 file 参数）或指定文件名的一张（file 参数）。
    /// 不存在时返回内置默认头像 SVG。
    /// </summary>
    [HttpGet("Actor/Image")]
    [AllowAnonymous]
    public IActionResult GetActorImage([FromQuery] string name, [FromQuery] string? file)
    {
        if (PluginState.IsDisabled())
        {
            return StatusCode(503);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest();
        }

        var config = Plugin.Instance?.Configuration;
        string? image = null;

        if (string.IsNullOrWhiteSpace(file))
        {
            image = config is null ? null : _indexer.FindPersonImage(name, config);
        }
        else
        {
            // 指定文件名：在多照片中匹配（防路径穿越：只取纯文件名）
            var safeFile = Path.GetFileName(file);
            if (!string.Equals(safeFile, file, StringComparison.Ordinal))
            {
                return BadRequest();
            }

            var photos = config is null ? [] : _indexer.FindPersonImages(name, config);
            image = photos.FirstOrDefault(p => string.Equals(Path.GetFileName(p), safeFile, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrEmpty(image) || !System.IO.File.Exists(image))
        {
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Content(DefaultAvatarSvg, "image/svg+xml; charset=utf-8");
        }

        // nosniff：防止 SVG 等文本型图片被当作可执行内容
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        // 图片允许浏览器缓存 10 分钟（文件一般不变；换头像后 10 分钟内最多旧图）
        Response.Headers.CacheControl = "private, max-age=600";
        return PhysicalFile(image, GetMimeType(image));
    }

    /// <summary>
    /// 返回自定义演员详情页（独立自包含页面，匿名）。
    /// </summary>
    [HttpGet("ActorPage")]
    [AllowAnonymous]
    public ActionResult<string> GetActorPage()
    {
        if (PluginState.IsDisabled())
        {
            return StatusCode(503);
        }

        Response.Headers.CacheControl = "no-store";
        return Content(ReadResource(ActorPageResource, out _), "text/html; charset=utf-8");
    }

    private string ReadResource(string resourceName, out bool found)
    {
        var assembly = typeof(ActorController).Assembly;
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
            ".tbn" => "image/jpeg",
            _ => "application/octet-stream"
        };
    }

    private const string DefaultAvatarSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"160\" viewBox=\"0 0 160 160\">" +
        "<circle cx=\"80\" cy=\"80\" r=\"80\" fill=\"#3a3f47\"/>" +
        "<circle cx=\"80\" cy=\"62\" r=\"30\" fill=\"#aab2bd\"/>" +
        "<path d=\"M30 150 a50 42 0 0 1 100 0 z\" fill=\"#aab2bd\"/></svg>";
}

/// <summary>
/// 演员状态列表响应。
/// </summary>
public sealed class ActorStatusResult
{
    /// <summary>条目 ID。</summary>
    public string? ItemId { get; set; }

    /// <summary>演员卡片功能是否开启。</summary>
    public bool Enabled { get; set; }

    /// <summary>演员卡片每排数量（0=自适应）。</summary>
    public int ActorCardsPerRow { get; set; }

    /// <summary>剧照每行数量（0=自适应）。</summary>
    public int StillsPerRow { get; set; }

    /// <summary>预告片每行数量（0=自适应）。</summary>
    public int TrailersPerRow { get; set; }

    /// <summary>详情页区块顺序（Overview/Stills/Actors 排列）。</summary>
    public string? SectionOrder { get; set; }

    /// <summary>演员列表。</summary>
    public List<ActorStatusItem> Actors { get; set; } = [];
}

/// <summary>
/// 单个演员的本地素材状态。
/// </summary>
public sealed class ActorStatusItem
{
    /// <summary>演员名。</summary>
    public string? Name { get; set; }

    /// <summary>角色名。</summary>
    public string? Role { get; set; }

    /// <summary>是否已有完整本地信息（头像+简介）。</summary>
    public bool HasLocalInfo { get; set; }

    /// <summary>是否有本地头像。</summary>
    public bool HasAvatar { get; set; }

    /// <summary>是否正在刷新中。</summary>
    public bool IsRefreshing { get; set; }

    /// <summary>本地头像访问路径（无则 null，前端用默认头像）。</summary>
    public string? AvatarUrl { get; set; }
}

/// <summary>
/// 触发刷新请求体。
/// </summary>
public sealed class RefreshActorRequest
{
    /// <summary>演员名。</summary>
    public string? ActorName { get; set; }
}

/// <summary>
/// 照片操作请求体（设默认/删除）。
/// </summary>
public sealed class ActorPhotoRequest
{
    /// <summary>演员名。</summary>
    public string? Name { get; set; }

    /// <summary>照片文件名（含扩展名）。</summary>
    public string? File { get; set; }
}

/// <summary>
/// 备用库同步结果。
/// </summary>
public sealed class StagingSyncResult
{
    /// <summary>补充到主演员库的照片数。</summary>
    public int Copied { get; set; }
}

/// <summary>
/// 演员本地信息详情响应。
/// </summary>
public sealed class ActorDetailResult
{
    /// <summary>演员名。</summary>
    public string? Name { get; set; }

    /// <summary>简介（本地 json 中的 overview）。</summary>
    public string? Overview { get; set; }

    /// <summary>Jellyfin 中 Person 的 Id（有则可用于跳转官方演员页）。</summary>
    public string? PersonId { get; set; }

    /// <summary>是否有本地头像。</summary>
    public bool HasAvatar { get; set; }

    /// <summary>主照片访问路径。</summary>
    public string? PhotoUrl { get; set; }

    /// <summary>
    /// 多照片列表（预留）：当前索引按演员名只出一张主图；
    /// 索引重构支持多张照片后填充。photos 为空时前端用 PhotoUrl 兜底。
    /// </summary>
    public List<string> Photos { get; set; } = [];

    /// <summary>默认照片文件名（photos 中标记默认的那张，可能为空）。</summary>
    public string? DefaultPhotoFile { get; set; }

    /// <summary>出生日期（Jellyfin Person 的 PremiereDate，可能为空）。</summary>
    public DateTime? PremiereDate { get; set; }

    /// <summary>出道年份（Jellyfin Person 的 ProductionYear，可能为空）。</summary>
    public int? ProductionYear { get; set; }

    /// <summary>评分（Jellyfin Person 的 CommunityRating，可能为空）。</summary>
    public float? CommunityRating { get; set; }

    /// <summary>参演作品（库中按 PersonIds 查询，最多 50 条）。</summary>
    public List<AppearanceItem> Appearances { get; set; } = [];
}

/// <summary>
/// 参演作品条目。
/// </summary>
public sealed class AppearanceItem
{
    /// <summary>作品条目 ID。</summary>
    public string? Id { get; set; }

    /// <summary>作品名称。</summary>
    public string? Name { get; set; }

    /// <summary>作品类型：Movie / Series / Episode。</summary>
    public string? Type { get; set; }

    /// <summary>年份。</summary>
    public int? ProductionYear { get; set; }

    /// <summary>海报访问路径（Jellyfin 原生图片接口）。</summary>
    public string? PosterUrl { get; set; }
}
