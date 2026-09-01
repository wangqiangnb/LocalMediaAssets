using Jellyfin.Plugin.LocalMediaAssets.Configuration;

namespace Jellyfin.Plugin.LocalMediaAssets.Services;

/// <summary>
/// 语言解析：auto 跟随 Jellyfin 服务器界面语言（UICulture），否则使用自定义语言。
/// </summary>
public static class LanguageResolver
{
    /// <summary>
    /// 返回 "zh" 或 "en"。
    /// </summary>
    public static string Resolve(PluginConfiguration? config, string? uiCulture)
    {
        return config?.Language switch
        {
            "zh" => "zh",
            "en" => "en",
            _ => string.IsNullOrEmpty(uiCulture) || uiCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en"
        };
    }
}
