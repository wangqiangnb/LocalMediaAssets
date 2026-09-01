using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LocalMediaAssets.Configuration;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 演员数据库统一接口（供详情页美化、其他插件、未来功能调用）。
/// 查询：按演员名取信息/照片/默认头像；列表/搜索。
/// 修改：保存信息、设默认照片、删除照片、导入照片。
/// 同步：手动触发。
/// 事件：数据变化通知。
/// </summary>
public interface IActorDatabase
{
    /// <summary>
    /// 读取演员完整记录（信息 + 全部照片，默认图排最前）。无数据返回 null。
    /// </summary>
    ActorRecord? GetActor(string name, PluginConfiguration config);

    /// <summary>
    /// 读取演员默认照片（头像）。无则返回 null。
    /// </summary>
    ActorPhotoEntry? GetDefaultPhoto(string name, PluginConfiguration config);

    /// <summary>
    /// 读取演员全部照片（默认图排最前）。
    /// </summary>
    IReadOnlyList<ActorPhotoEntry> GetPhotos(string name, PluginConfiguration config);

    /// <summary>
    /// 全部演员名（索引秒查）。
    /// </summary>
    IReadOnlyList<string> ListActorNames(PluginConfiguration config);

    /// <summary>
    /// 按关键词搜索演员（名称包含匹配）。
    /// </summary>
    IReadOnlyList<string> Search(string keyword, PluginConfiguration config, int limit = 100);

    /// <summary>
    /// 保存演员信息（空简介且文件已存在时跳过，不覆盖用户手写内容）。
    /// </summary>
    void SaveInfo(string name, string? overview, string? defaultPhotoFile, PluginConfiguration config);

    /// <summary>
    /// 设置默认照片（重命名 + 更新标记）。返回是否成功。
    /// </summary>
    bool SetDefaultPhoto(string name, string photoPath, PluginConfiguration config);

    /// <summary>
    /// 删除照片（默认照片不可直接删除）。返回是否成功。
    /// </summary>
    bool DeletePhoto(string name, string photoPath, PluginConfiguration config);

    /// <summary>
    /// 导入照片到主存储（从来源路径复制，可选设为默认）。返回目标路径。
    /// </summary>
    string? AddPhoto(string name, string sourcePath, PluginConfiguration config, bool makeDefault = false);

    /// <summary>
    /// 手动触发同步（从来源更新到主存储）。返回统计。
    /// </summary>
    SyncResult Sync(PluginConfiguration config, SyncTrigger trigger);

    /// <summary>
    /// 使索引缓存失效（下次访问全量重建）。
    /// </summary>
    void InvalidateIndex();

    /// <summary>
    /// 数据变化事件（详情页实时刷新、其他插件订阅）。
    /// </summary>
    event EventHandler<ActorChangedEventArgs>? ActorChanged;
}

/// <summary>
/// 同步触发来源。
/// </summary>
public enum SyncTrigger
{
    /// <summary>库扫描后自动。</summary>
    Auto,

    /// <summary>定时同步。</summary>
    Scheduled,

    /// <summary>手动触发。</summary>
    Manual,

    /// <summary>单演员刷新后。</summary>
    Actor
}

/// <summary>
/// 同步结果统计。
/// </summary>
public sealed class SyncResult
{
    /// <summary>新增照片数。</summary>
    public int AddedPhotos { get; set; }

    /// <summary>跳过重复数。</summary>
    public int DuplicateSkipped { get; set; }

    /// <summary>命中墓碑跳过数。</summary>
    public int TombstoneSkipped { get; set; }

    /// <summary>更新信息数。</summary>
    public int UpdatedInfos { get; set; }

    /// <summary>错误数。</summary>
    public int Errors { get; set; }
}

/// <summary>
/// 数据变化事件参数。
/// </summary>
public sealed class ActorChangedEventArgs : EventArgs
{
    /// <summary>变化的演员名。</summary>
    public string ActorName { get; init; } = string.Empty;

    /// <summary>变化类型。</summary>
    public ActorChangeType ChangeType { get; init; }
}

/// <summary>
/// 变化类型。
/// </summary>
public enum ActorChangeType
{
    /// <summary>照片新增/删除/设默认。</summary>
    PhotosChanged,

    /// <summary>信息（简介等）变化。</summary>
    InfoChanged
}
