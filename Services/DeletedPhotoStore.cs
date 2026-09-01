using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 已删除照片记录（墓碑机制）：
/// 用户在演员详情页删除的照片会记录其文件哈希；之后「刷新备用库」同步时，
/// 若备用库照片的哈希命中记录则跳过，避免被删除的照片被自动加回。
/// 存储于插件程序集目录 deleted-photos.json（不入库）。
/// </summary>
public sealed class DeletedPhotoStore
{
    private readonly object _sync = new();
    private Dictionary<string, DeletedPhotoEntry> _records = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    // 记录上限：防止无限增长（超出后丢弃最旧的）
    private const int MaxRecords = 500;

    /// <summary>
    /// 记录一张被删除的照片（按演员名 + 文件哈希）。
    /// </summary>
    public void Record(string actorName, string photoPath)
    {
        if (string.IsNullOrWhiteSpace(actorName) || string.IsNullOrEmpty(photoPath) || !File.Exists(photoPath))
        {
            return;
        }

        var hash = ComputeHash(photoPath);
        if (string.IsNullOrEmpty(hash))
        {
            return;
        }

        lock (_sync)
        {
            EnsureLoaded();
            var key = BuildKey(actorName, hash);
            _records[key] = new DeletedPhotoEntry
            {
                Actor = actorName,
                Hash = hash,
                FileName = Path.GetFileName(photoPath),
                DeletedAtUtc = DateTime.UtcNow
            };

            // 上限裁剪：删除最旧记录
            if (_records.Count > MaxRecords)
            {
                var toRemove = _records.OrderBy(kv => kv.Value.DeletedAtUtc).Take(_records.Count - MaxRecords).ToList();
                foreach (var kv in toRemove)
                {
                    _records.Remove(kv.Key);
                }
            }

            Save();
        }
    }

    /// <summary>
    /// 判断某照片是否命中删除记录（演员名 + 哈希一致）。
    /// </summary>
    public bool IsDeleted(string actorName, string photoPath)
    {
        if (string.IsNullOrWhiteSpace(actorName) || string.IsNullOrEmpty(photoPath) || !File.Exists(photoPath))
        {
            return false;
        }

        var hash = ComputeHash(photoPath);
        if (string.IsNullOrEmpty(hash))
        {
            return false;
        }

        lock (_sync)
        {
            EnsureLoaded();
            return _records.ContainsKey(BuildKey(actorName, hash));
        }
    }

    /// <summary>
    /// 清空删除记录（备用库照片将可重新同步）。
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _records.Clear();
            Save();
        }
    }

    /// <summary>
    /// 当前记录条数。
    /// </summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                EnsureLoaded();
                return _records.Count;
            }
        }
    }

    private static string BuildKey(string actor, string hash)
        => actor.Trim().ToLowerInvariant() + "|" + hash;

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            var file = FilePath();
            if (!File.Exists(file))
            {
                return;
            }

            var data = JsonSerializer.Deserialize<DeletedPhotoFile>(File.ReadAllText(file));
            if (data?.Records is not null)
            {
                foreach (var rec in data.Records)
                {
                    if (rec is null || string.IsNullOrEmpty(rec.Actor) || string.IsNullOrEmpty(rec.Hash))
                    {
                        continue;
                    }

                    _records[BuildKey(rec.Actor, rec.Hash)] = rec;
                }
            }
        }
        catch (Exception)
        {
            // 记录损坏时忽略（重新开始记录）
            _records = new Dictionary<string, DeletedPhotoEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            var file = FilePath();
            var dir = Path.GetDirectoryName(file) ?? string.Empty;
            Directory.CreateDirectory(dir);
            var data = new DeletedPhotoFile
            {
                Records = _records.Values
                    .OrderByDescending(r => r.DeletedAtUtc)
                    .ToList()
            };
            File.WriteAllText(file, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // 保存失败不影响主流程
        }
    }

    /// <summary>
    /// 记录文件路径：插件程序集目录 deleted-photos.json
    /// （不用 DataFolderPath，避免在 plugins 下产生影子插件目录）。
    /// </summary>
    private static string FilePath()
    {
        var pluginDir = Path.GetDirectoryName(typeof(DeletedPhotoStore).Assembly.Location);
        return string.IsNullOrEmpty(pluginDir)
            ? Path.Combine(Path.GetTempPath(), "lma-deleted-photos.json")
            : Path.Combine(pluginDir, "deleted-photos.json");
    }

    /// <summary>
    /// 计算文件 SHA256（十六进制小写）。失败返回空串。
    /// </summary>
    public static string ComputeHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(stream);
            return Convert.ToHexStringLower(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// 删除记录文件结构。
/// </summary>
public sealed class DeletedPhotoFile
{
    /// <summary>记录列表。</summary>
    public List<DeletedPhotoEntry> Records { get; set; } = [];
}

/// <summary>
/// 单条删除记录。
/// </summary>
public sealed class DeletedPhotoEntry
{
    /// <summary>演员名。</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>文件 SHA256（十六进制小写）。</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>原文件名（展示用）。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>删除时间。</summary>
    public DateTime DeletedAtUtc { get; set; }
}
