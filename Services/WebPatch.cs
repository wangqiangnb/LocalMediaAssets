using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 自愈式 Web 补丁：在 jellyfin-web/index.html 中注入详情页优化脚本标签（幂等、可精确还原）。
/// 设计原则（不改动 Jellyfin 其他内容、不与其他插件冲突）：
///   - 只注入/删除自己的一行脚本标签（带唯一注释标记），绝不整文件备份恢复——卸载或手动还原时
///     仅删除这一行，其他插件对 index.html 的修改原样保留；
///   - 注入点优先在主脚本（main.jellyfin.bundle.js）之后，找不到时回退到 &lt;/head&gt; 前（兼容皮肤类插件）；
///   - 自愈：脚本标签被其他插件移除或移动后，下次启动自动重新注入/归位。
/// </summary>
public static class WebPatch
{
    private const string ScriptTag = "<script defer=\"defer\" src=\"/LocalMediaAssets/webJs\"></script>";
    private const string MarkerStart = "<!-- LocalMediaAssets:start -->";
    private const string MarkerEnd = "<!-- LocalMediaAssets:end -->";
    private const string Marker = MarkerStart + ScriptTag + MarkerEnd;
    private const string Needle = "src=\"main.jellyfin.bundle.js";
    private const string HeadNeedle = "</head>";

    private static readonly object Sync = new();
    private static bool _attempted;

    /// <summary>
    /// 确保 index.html 已包含插件脚本标签（幂等，进程内只尝试一次）。
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

                // 计算注入位置：主脚本闭合标签后；找不到则 </head> 前
                int insertPos;
                var closeTag = "</script>";
                var idx = html.IndexOf(Needle, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var closeIdx = html.IndexOf(closeTag, idx, StringComparison.Ordinal);
                    if (closeIdx < 0)
                    {
                        logger.LogWarning("LocalMediaAssets：index.html 中未找到主脚本闭合标签，跳过注入");
                        return;
                    }

                    insertPos = closeIdx + closeTag.Length;
                }
                else
                {
                    var headIdx = html.IndexOf(HeadNeedle, StringComparison.OrdinalIgnoreCase);
                    if (headIdx < 0)
                    {
                        logger.LogWarning("LocalMediaAssets：index.html 中未找到注入点（main bundle 或 </head>），跳过注入");
                        return;
                    }

                    insertPos = headIdx;
                    closeTag = HeadNeedle;
                }

                // 若标记已存在且位于注入点（含）之后，说明已注入且位置正确
                var markerIdx = html.IndexOf(Marker, StringComparison.Ordinal);
                if (markerIdx >= 0 && markerIdx >= insertPos)
                {
                    logger.LogDebug("LocalMediaAssets：Web 补丁已存在且位置正确，跳过");
                    return;
                }

                // 移除旧标记（含无注释的旧版单标签，兼容此前部署版本），重新注入
                var changed = RemoveInjectedScript(html, out html);
                if (!changed && markerIdx >= 0)
                {
                    // 标记存在但位置错误：也移除重插
                    html = html.Replace(Marker, string.Empty);
                    changed = true;
                }

                if (markerIdx < 0)
                {
                    // 重新计算注入位置（移除旧标记后偏移可能变化）
                    if (NeedleFound(html, out idx))
                    {
                        var closeIdx = html.IndexOf(closeTag, idx, StringComparison.Ordinal);
                        if (closeIdx < 0)
                        {
                            logger.LogWarning("LocalMediaAssets：index.html 重算注入点失败");
                            return;
                        }

                        insertPos = closeIdx + closeTag.Length;
                    }
                    else
                    {
                        var headIdx = html.IndexOf(HeadNeedle, StringComparison.OrdinalIgnoreCase);
                        if (headIdx < 0)
                        {
                            return;
                        }

                        insertPos = headIdx;
                    }
                }

                html = html.Insert(insertPos, Marker);
                File.WriteAllText(index, html);

                logger.LogInformation("LocalMediaAssets：已注入 jellyfin-web/index.html 详情页优化脚本（仅新增一行带标记的 script，可随时精确移除）");
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(
                    ex,
                    "LocalMediaAssets：jellyfin-web/index.html 没有写权限，详情页优化不可用。请在 Docker/Linux 上给该文件写权限（如 chmod 666 /jellyfin/jellyfin-web/index.html），或设置 JELLYFIN_WEB_DIR 指向可写目录后重启。");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "LocalMediaAssets：Web 补丁注入失败");
            }
        }
    }

    /// <summary>
    /// 精确移除插件注入的脚本标签（只删自己的一行，不影响其他插件对 index.html 的修改）。
    /// 兼容带注释标记与旧版无注释单标签两种形式。
    /// </summary>
    /// <returns>是否发生了删除。</returns>
    public static bool Restore(string indexHtmlPath, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(indexHtmlPath))
            {
                return false;
            }

            var html = File.ReadAllText(indexHtmlPath);
            var changed = RemoveInjectedScript(html, out var cleaned);
            if (!changed)
            {
                return false;
            }

            File.WriteAllText(indexHtmlPath, cleaned);
            logger?.LogInformation("LocalMediaAssets：已从 jellyfin-web/index.html 精确移除插件脚本");
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger?.LogWarning(ex, "LocalMediaAssets：jellyfin-web/index.html 没有写权限，无法移除插件脚本");
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "LocalMediaAssets：移除插件脚本失败");
            return false;
        }
    }

    private static bool RemoveInjectedScript(string html, out string cleaned)
    {
        cleaned = html;
        var changed = false;
        if (cleaned.Contains(Marker, StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace(Marker, string.Empty, StringComparison.Ordinal);
            changed = true;
        }

        // 兼容旧版：无注释的单标签
        if (cleaned.Contains(ScriptTag, StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace(ScriptTag, string.Empty, StringComparison.Ordinal);
            changed = true;
        }

        return changed;
    }

    private static bool NeedleFound(string html, out int idx)
    {
        idx = html.IndexOf(Needle, StringComparison.Ordinal);
        return idx >= 0;
    }
}
