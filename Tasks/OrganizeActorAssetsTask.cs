using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalMediaAssets.Services;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Tasks;

/// <summary>
/// 计划任务「整理本地演员信息和照片」：
/// 本插件不负责刮削；演员信息/照片由其他插件刮削完成后，运行本任务把
/// 演员照片与简介同步到各视频目录的 actors/ 与配置的演员库。
/// </summary>
public sealed class OrganizeActorAssetsTask : IScheduledTask
{
    private readonly ActorAssetExporter _exporter;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ILogger<OrganizeActorAssetsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrganizeActorAssetsTask"/> class.
    /// </summary>
    public OrganizeActorAssetsTask(
        ActorAssetExporter exporter,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<OrganizeActorAssetsTask> logger)
    {
        _exporter = exporter;
        _serverConfigurationManager = serverConfigurationManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name
    {
        get
        {
            var lang = LanguageResolver.Resolve(Plugin.Instance?.Configuration, _serverConfigurationManager.Configuration.UICulture);
            return lang == "zh" ? "整理本地演员信息和照片" : "Organize Local Actor Assets";
        }
    }

    /// <inheritdoc />
    public string Key => "OrganizeActorAssets";

    /// <inheritdoc />
    public string Description
    {
        get
        {
            var lang = LanguageResolver.Resolve(Plugin.Instance?.Configuration, _serverConfigurationManager.Configuration.UICulture);
            return lang == "zh"
                ? "把演员照片与简介同步到各视频目录的 actors/ 文件夹与配置的演员库。刮削由其他插件完成，本任务只做本地整理。"
                : "Sync actor photos and bios to each video folder's actors/ directory and the configured actor library. Scraping is done by other plugins; this task only organizes local assets.";
        }
    }

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // 默认每天凌晨 4 点执行一次；也可在「计划任务」中手动运行
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = new TimeSpan(4, 0, 0).Ticks
            }
        ];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return;
        }

        _logger.LogInformation("LocalMediaAssets：开始「整理本地演员信息和照片」");
        _exporter.RebuildIndex(config);
        progress.Report(3);

        if (config.ExportToActorLibrary && !string.IsNullOrWhiteSpace(config.ActorLibraryPath))
        {
            var libraryProgress = new Progress<double>(p => progress.Report(3 + p * 0.45));
            await _exporter.ExportToActorLibraryAsync(config, libraryProgress, cancellationToken).ConfigureAwait(false);
        }

        var videoProgress = new Progress<double>(p => progress.Report(50 + p * 0.5));
        await _exporter.ExportToVideosAsync(
            config,
            videoProgress,
            cancellationToken,
            includePhotos: config.ExportPhotosNextToVideo,
            includeInfo: config.ExportInfoNextToVideo).ConfigureAwait(false);

        progress.Report(100);
        _logger.LogInformation("LocalMediaAssets：「整理本地演员信息和照片」完成");
    }
}
