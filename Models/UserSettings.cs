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

    /// <summary>剧照区块位置（Top / AboveOverview / AboveCast / Bottom）。</summary>
    public string? StillsPosition { get; set; }

    /// <summary>界面语言（auto / zh / en）。</summary>
    public string? Language { get; set; }

    /// <summary>最多展示剧照数。</summary>
    public int? MaxStills { get; set; }
}
