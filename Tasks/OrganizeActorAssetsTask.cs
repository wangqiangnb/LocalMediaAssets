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
    private readonly IActorDatabase _db;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ILogger<OrganizeActorAssetsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrganizeActorAssetsTask"/> class.
    /// </summary>
    public OrganizeActorAssetsTask(
        IActorDatabase db,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<OrganizeActorAssetsTask> logger)
    {
        _db = db;
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
        // 仅「定时同步」模式注册每日自动触发（时间取 SyncHour，默认凌晨 4 点）；
        // Auto（扫描后自动）与 Manual（仅手动）不注册定时触发，任务仍可手动运行。
        // 注意：配置变更后需重启服务器或重新加载计划任务生效。
        var syncMode = Plugin.Instance?.Configuration?.SyncMode ?? "Auto";
        if (syncMode != "Scheduled")
        {
            return [];
        }

        var hour = Plugin.Instance?.Configuration?.SyncHour ?? 4;
        if (hour < 0 || hour > 23)
        {
            hour = 4;
        }

        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = new TimeSpan(hour, 0, 0).Ticks
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
        progress.Report(10);

        // 统一同步引擎：从来源更新到主存储（去重/墓碑/默认保护/分发视频目录）
        await Task.Run(() => _db.Sync(config, SyncTrigger.Scheduled), cancellationToken).ConfigureAwait(false);

        progress.Report(100);
        _logger.LogInformation("LocalMediaAssets：「整理本地演员信息和照片」完成");
    }
}
