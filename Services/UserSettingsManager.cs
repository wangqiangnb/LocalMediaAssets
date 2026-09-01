using System;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.LocalMediaAssets.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 每用户显示偏好的读写（存储于插件数据目录 usersettings/&lt;userId&gt;.json）。
/// 带短 TTL 内存缓存，避免详情页轮询等高频请求反复读盘。
/// </summary>
public sealed class UserSettingsManager
{
    private readonly ILogger<UserSettingsManager> _logger;
    private readonly object _sync = new();
    private string _dir = string.Empty;

    // 内存缓存：userId -> (settings, 加载时间)；TTL 内直接复用，避免每次请求读文件
    private readonly Dictionary<Guid, CacheEntry> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private sealed class CacheEntry
    {
        public UserSettings? Settings { get; set; }

        public DateTime LoadedAtUtc { get; set; }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UserSettingsManager"/> class.
    /// </summary>
    public UserSettingsManager(ILogger<UserSettingsManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 读取指定用户的设置；没有则返回 null（带短 TTL 缓存）。
    /// </summary>
    public UserSettings? Get(Guid userId)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(userId, out var entry)
                && DateTime.UtcNow - entry.LoadedAtUtc <= CacheTtl)
            {
                return entry.Settings;
            }
        }

        try
        {
            var file = Path.Combine(EnsureDir(), userId.ToString("N") + ".json");
            UserSettings? settings = null;
            if (File.Exists(file))
            {
                lock (_sync)
                {
                    settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(file));
                }
            }

            lock (_sync)
            {
                _cache[userId] = new CacheEntry { Settings = settings, LoadedAtUtc = DateTime.UtcNow };
            }

            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：读取用户设置失败 {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// 保存指定用户的设置（写盘并刷新缓存）。
    /// </summary>
    public void Save(Guid userId, UserSettings settings)
    {
        try
        {
            var dir = EnsureDir();
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, userId.ToString("N") + ".json");

            lock (_sync)
            {
                File.WriteAllText(file, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
                _cache[userId] = new CacheEntry { Settings = settings, LoadedAtUtc = DateTime.UtcNow };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：保存用户设置失败 {UserId}", userId);
        }
    }

    private string EnsureDir()
    {
        if (_dir.Length == 0)
        {
            // 与 PersonImageIndexer 同理：不能使用 DataFolderPath，否则会在 plugins 下
            // 创建与插件目录名不同的数据目录，被 Jellyfin 误认为第二个插件（影子插件），
            // 导致设置页启用/禁用按钮错乱。改放插件程序集所在目录的 usersettings/ 子目录。
            var pluginDir = Path.GetDirectoryName(typeof(UserSettingsManager).Assembly.Location);
            _dir = string.IsNullOrEmpty(pluginDir)
                ? Path.Combine(Path.GetTempPath(), "lma-usersettings")
                : Path.Combine(pluginDir, "usersettings");
        }

        return _dir;
    }
}
