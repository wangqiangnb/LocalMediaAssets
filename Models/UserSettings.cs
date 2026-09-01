namespace Jellyfin.Plugin.LocalMediaAssets.Models;

/// <summary>
/// 每用户的显示偏好。null/未设置 = 跟随全局（管理员配置）。
/// </summary>
public sealed class UserSettings
{
    /// <summary>显示预览图（剧照）。</summary>
    public bool? EnableStills { get; set; }

    /// <summary>展示预告片。</summary>
    public bool? ShowTrailers { get; set; }

    /// <summary>演员卡片替换（详情页圆形卡片）。</summary>
    public bool? EnableActorReplacement { get; set; }

    /// <summary>界面语言（auto / zh / en）。</summary>
    public string? Language { get; set; }

    /// <summary>详情页区块顺序（Overview/Stills/Trailers/Actors 排列，每用户）。</summary>
    public string? SectionOrder { get; set; }

    /// <summary>演员卡片每排数量（0=自适应，每用户）。</summary>
    public int? ActorCardsPerRow { get; set; }

    /// <summary>剧照每行数量（0=自适应，每用户）。</summary>
    public int? StillsPerRow { get; set; }

    /// <summary>预告片每行数量（0=自适应，每用户）。</summary>
    public int? TrailersPerRow { get; set; }

    /// <summary>最多展示剧照数。</summary>
    public int? MaxStills { get; set; }

    /// <summary>自动读取视频目录下的所有照片作为剧照（每用户）。</summary>
    public bool? StillsIncludeVideoDirPhotos { get; set; }

    /// <summary>同时显示 landscape/folder/poster 等海报类图片（每用户）。</summary>
    public bool? StillsIncludeArtworkImages { get; set; }
}
