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

    /// <summary>视频目录中存放演员素材的文件夹名。</summary>
    public string ActorFolderName { get; set; } = "actors";

    /// <summary>演员库中的人物子文件夹名。</summary>
    public string PeopleFolderName { get; set; } = "People";

    /// <summary>视频目录中存放剧照的文件夹名。</summary>
    public string StillsFolderName { get; set; } = "extrafanart";

    /// <summary>演员库目录（插件自动保存演员照片与信息的根目录）。</summary>
    public string ActorLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// 备用演员库目录（只读补充照片源）：用户把第三方整理好的演员照片放入此目录
    /// （&lt;演员名&gt;.jpg / &lt;演员名&gt;_2.jpg 等），插件索引时读取补充进本地演员数据库。
    /// 本插件不会修改此目录（只读）；请勿直接手动修改演员库，新增照片请放入备用库后手动刷新。
    /// </summary>
    public string ActorStagingPath { get; set; } = string.Empty;

    /// <summary>是否为没有 NFO 的电影/剧集补写 NFO（含演员列表），换机器后无需重新刮削 cast。已存在的 NFO 不会被覆盖。</summary>
    public bool ExportNfoNextToVideo { get; set; } = true;

    /// <summary>
    /// 演员素材同步模式：Auto（库扫描后自动同步）/ Scheduled（定时同步+手动）/ Manual（仅手动触发）。
    /// </summary>
    public string SyncMode { get; set; } = "Auto";

    /// <summary>定时同步的小时（0-23），SyncMode=Scheduled 时生效。</summary>
    public int SyncHour { get; set; } = 4;

    /// <summary>默认照片选取规则：First（严格同名优先，无则取最大文件）/ Largest（总是取文件最大者）。</summary>
    public string DefaultPhotoRule { get; set; } = "First";

    /// <summary>
    /// 演员数据存储模式：VideoOnly（仅视频文件夹，默认）/ Video+Library（视频+演员库）/
    /// LibraryOnly（仅演员库）/ Disabled（关闭数据库）。
    /// 读取始终聚合所有来源；修改存储方式不删除文件，仅改变参与范围。
    /// </summary>
    public string StorageMode { get; set; } = "VideoOnly";

    /// <summary>是否启用 Jellyfin 刮削源（只读）：读取 Jellyfin 中该 Person 的照片/简介作为补充，插件自身不联网刮削。</summary>
    public bool JellyfinSourceEnabled { get; set; } = true;

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
