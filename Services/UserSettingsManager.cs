using System;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.LocalMediaAssets.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 每用户显示偏好的读写（存储于插件数据目录 usersettings/&lt;userId&gt;.json）。
/// </summary>
public sealed class UserSettingsManager
{
    private readonly ILogger<UserSettingsManager> _logger;
    private readonly object _sync = new();
    private string _dir = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserSettingsManager"/> class.
    /// </summary>
    public UserSettingsManager(ILogger<UserSettingsManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 读取指定用户的设置；没有则返回 null。
    /// </summary>
    public UserSettings? Get(Guid userId)
    {
        try
        {
            var file = Path.Combine(EnsureDir(), userId.ToString("N") + ".json");
            if (!File.Exists(file))
            {
                return null;
            }

            lock (_sync)
            {
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(file));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：读取用户设置失败 {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// 保存指定用户的设置。
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
