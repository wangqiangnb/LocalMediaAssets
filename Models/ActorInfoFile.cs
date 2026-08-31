using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.LocalMediaAssets.Models;

/// <summary>
/// 演员信息文件（actors/&lt;演员名&gt;.json 或 演员库/People/&lt;演员名&gt;.json）。
/// </summary>
public sealed class ActorInfoFile
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }
}
