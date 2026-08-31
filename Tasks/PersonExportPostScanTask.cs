using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalMediaAssets.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Tasks;

/// <summary>
/// 库扫描结束后自动执行演员素材整理（重建索引 + 同步演员库 + 视频旁演员素材）。
/// </summary>
public sealed class PersonExportPostScanTask : ILibraryPostScanTask
{
    private readonly ActorAssetExporter _exporter;
    private readonly ILogger<PersonExportPostScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersonExportPostScanTask"/> class.
    /// </summary>
    public PersonExportPostScanTask(ActorAssetExporter exporter, ILogger<PersonExportPostScanTask> logger)
    {
        _exporter = exporter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return;
        }

        if (!config.ExportToActorLibrary && !config.ExportInfoNextToVideo && !config.ExportPhotosNextToVideo && !config.ExportNfoNextToVideo)
        {
            return;
        }

        _logger.LogInformation("LocalMediaAssets：开始扫描后整理任务（重建索引 + 同步演员素材）");
        _exporter.RebuildIndex(config);
        progress.Report(5);

        if (config.ExportToActorLibrary && !string.IsNullOrWhiteSpace(config.ActorLibraryPath))
        {
            var libraryProgress = new Progress<double>(p => progress.Report(5 + p * 0.45));
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
        _logger.LogInformation("LocalMediaAssets：扫描后整理任务完成");
    }
}
