using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 自愈式 Web 补丁：在 jellyfin-web/index.html 中自动注入剧照脚本标签（幂等）。
/// 由插件进程自己写入（与 Jellyfin 同一用户权限），Jellyfin 升级后重启一次即自动恢复，无需手工改文件。
/// </summary>
public static class WebPatch
{
    private const string ScriptTag = "<script defer=\"defer\" src=\"/LocalMediaAssets/stillsJs\"></script>";
    private const string Needle = "src=\"main.jellyfin.bundle.js";

    private static readonly object Sync = new();
    private static bool _attempted;

    /// <summary>
    /// 确保 index.html 已包含剧照脚本标签；已包含或已尝试过则直接返回。
    /// </summary>
    public static void EnsureApplied(IApplicationPaths applicationPaths, ILogger logger)
    {
        if (_attempted)
        {
            return;
        }

        lock (Sync)
        {
            if (_attempted)
            {
                return;
            }

            _attempted = true;

            try
            {
                var index = Path.Combine(applicationPaths.WebPath, "index.html");
                if (!File.Exists(index))
                {
                    logger.LogDebug("LocalMediaAssets：未找到 index.html，跳过 Web 补丁");
                    return;
                }

                var html = File.ReadAllText(index);

                var idx = html.IndexOf(Needle, StringComparison.Ordinal);
                if (idx < 0)
                {
                    logger.LogWarning("LocalMediaAssets：index.html 中未找到注入点（main.jellyfin.bundle.js）");
                    return;
                }

                var closeTag = "</script>";
                var closeIdx = html.IndexOf(closeTag, idx, StringComparison.Ordinal);
                if (closeIdx < 0)
                {
                    logger.LogWarning("LocalMediaAssets：index.html 中未找到主脚本闭合标签");
                    return;
                }

                var insertPos = closeIdx + closeTag.Length;

                // 自愈：若标记已存在但位置错误，先移除再重新插入
                var markerIdx = html.IndexOf(ScriptTag, StringComparison.Ordinal);
                if (markerIdx >= 0)
                {
                    if (markerIdx >= insertPos)
                    {
                        logger.LogDebug("LocalMediaAssets：Web 补丁已存在且位置正确，跳过");
                        return;
                    }

                    html = html.Remove(markerIdx, ScriptTag.Length);
                    closeIdx = html.IndexOf(closeTag, idx, StringComparison.Ordinal);
                    insertPos = closeIdx + closeTag.Length;
                }

                html = html.Insert(insertPos, ScriptTag);
                File.WriteAllText(index, html);

                logger.LogInformation("LocalMediaAssets：已自动修复/注入 jellyfin-web/index.html 剧照脚本（Jellyfin 升级后重启即自动恢复）");
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(
                    ex,
                    "LocalMediaAssets：jellyfin-web/index.html 没有写权限，剧照详情页展示不可用。请在 Docker/Linux 上给该文件写权限（如 chmod 666 /jellyfin/jellyfin-web/index.html），或设置 JELLYFIN_WEB_DIR 指向可写目录后重启。");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "LocalMediaAssets：Web 补丁注入失败");
            }
        }
    }
}
