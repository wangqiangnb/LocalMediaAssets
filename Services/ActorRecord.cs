using System.Collections.Generic;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 演员记录（本地数据库统一模型）。
/// </summary>
public sealed class ActorRecord
{
    /// <summary>演员名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>简介。</summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>默认照片完整路径。</summary>
    public string? DefaultPhoto { get; set; }

    /// <summary>全部照片完整路径（默认图排最前）。</summary>
    public List<string> Photos { get; set; } = [];
}
