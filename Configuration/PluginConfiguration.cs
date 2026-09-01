using System.IO;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LocalMediaAssets.Configuration;

/// <summary>
/// 插件配置。
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>界面语言：auto（跟随 Jellyfin 界面语言）/ zh / en。</summary>
    public string Language { get; set; } = "auto";

    /// <summary>是否启用本地演员照片读取（视频目录 actors/ 或演员库）。</summary>
    public bool EnableActorImages { get; set; } = true;

    /// <summary>是否启用「本地演员信息」读取（actors/xxx.json，用于演员简介）。</summary>
    public bool EnableActorMetadata { get; set; } = true;

    /// <summary>是否启用剧照功能（详情页演员区上方的剧照网格）。</summary>
    public bool EnableStills { get; set; } = true;

    /// <summary>是否同时把剧照注册为背景图（默认关闭，只通过详情页网格展示）。</summary>
    public bool EnableStillsAsBackdrops { get; set; } = false;

    /// <summary>视频目录中存放演员素材的文件夹名。</summary>
    public string ActorFolderName { get; set; } = "actors";

    /// <summary>演员库中的人物子文件夹名。</summary>
    public string PeopleFolderName { get; set; } = "People";

    /// <summary>视频目录中存放剧照的文件夹名。</summary>
    public string StillsFolderName { get; set; } = "extrafanart";

    /// <summary>单个视频最多展示的剧照数量。</summary>
    public int MaxStills { get; set; } = 20;

    /// <summary>剧照区块在详情页的位置：Top / AboveOverview / AboveCast（默认）/ Bottom。</summary>
    public string StillsPosition { get; set; } = "AboveCast";

    /// <summary>是否自动把视频目录顶层的所有照片也作为剧照展示（默认关，只读 extrafanart/）。</summary>
    public bool StillsIncludeVideoDirPhotos { get; set; } = false;

    /// <summary>自动读取视频目录照片时，是否包含 poster/folder/landscape 等海报类图片（默认关，仅显示普通照片）。</summary>
    public bool StillsIncludeArtworkImages { get; set; } = false;

    /// <summary>是否在剧照区展示预告片（本地 trailers/ + 远程在线预告片，点击弹窗播放）。</summary>
    public bool ShowTrailers { get; set; } = true;

    /// <summary>演员库目录（插件自动保存演员照片与信息的根目录）。</summary>
    public string ActorLibraryPath { get; set; } = string.Empty;

    /// <summary>演员库优先：同一演员在演员库与视频目录照片/信息不一致时，以演员库为默认（默认开启）。</summary>
    public bool ActorLibraryPriority { get; set; } = true;

    /// <summary>是否在库扫描结束后自动把演员照片/信息导出到演员库。</summary>
    public bool ExportToActorLibrary { get; set; } = true;

    /// <summary>是否在库扫描结束后把演员信息(JSON)写入每个视频旁的 actors/ 文件夹。</summary>
    public bool ExportInfoNextToVideo { get; set; } = true;

    /// <summary>是否在整理/扫描时把演员照片也同步到每个视频旁的 actors/ 文件夹。</summary>
    public bool ExportPhotosNextToVideo { get; set; } = true;

    /// <summary>是否为没有 NFO 的电影/剧集补写 NFO（含演员列表），换机器后无需重新刮削 cast。已存在的 NFO 不会被覆盖。</summary>
    public bool ExportNfoNextToVideo { get; set; } = true;

    /// <summary>
    /// 校验目录名是否为安全的纯名称：不含路径分隔符、冒号、点目录，长度受限。
    /// </summary>
    public static bool IsValidFolderName(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return false;
        }

        var trimmed = folderName.Trim();
        if (trimmed.Length > 64 || trimmed is "." or "..")
        {
            return false;
        }

        return trimmed.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':' }) < 0;
    }
}
