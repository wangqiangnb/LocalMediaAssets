using System;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 插件启用状态检测。
/// Jellyfin 的禁用/启用是"重启后生效"：禁用只写盘 meta.json，内存实例与注入的
/// 前端脚本仍继续工作。为了让用户点击"关闭插件"后立即生效（不必等重启），
/// 控制器在检测到 meta.json status=Disabled 时返回 503，前端脚本收到 503 后自我卸载。
/// </summary>
public static class PluginState
{
    private static readonly object Sync = new();
    private static bool _checked;
    private static bool _disabled;
    private static DateTime _checkedAtUtc = DateTime.MinValue;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 插件是否处于"已禁用"状态（读插件目录 meta.json 的 status 字段，带 5 秒缓存）。
    /// </summary>
    public static bool IsDisabled()
    {
        lock (Sync)
        {
            if (_checked && DateTime.UtcNow - _checkedAtUtc < CacheTtl)
            {
                return _disabled;
            }

            _checkedAtUtc = DateTime.UtcNow;
            _checked = true;

            try
            {
                var pluginDir = Path.GetDirectoryName(typeof(PluginState).Assembly.Location);
                var metaFile = string.IsNullOrEmpty(pluginDir) ? null : Path.Combine(pluginDir, "meta.json");
                if (string.IsNullOrEmpty(metaFile) || !File.Exists(metaFile))
                {
                    _disabled = false;
                    return _disabled;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(metaFile));
                _disabled = doc.RootElement.TryGetProperty("status", out var statusEl)
                    && string.Equals(statusEl.GetString(), "Disabled", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 读不到 meta.json 视为未禁用（避免误伤正常状态）
                _disabled = false;
            }

            return _disabled;
        }
    }
}
