using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalMediaAssets.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Tasks;

/// <summary>
/// 库扫描结束后自动执行演员数据同步（统一走 IActorDatabase.Sync）。
/// </summary>
public sealed class PersonExportPostScanTask : ILibraryPostScanTask
{
    private readonly IActorDatabase _db;
    private readonly ILogger<PersonExportPostScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersonExportPostScanTask"/> class.
    /// </summary>
    public PersonExportPostScanTask(IActorDatabase db, ILogger<PersonExportPostScanTask> logger)
    {
        _db = db;
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

        // 同步方式：Auto（库扫描后自动）/ Scheduled（定时+手动，扫描后不自动）/ Manual（仅手动）
        var mode = config.SyncMode ?? "Auto";
        if (mode != "Auto")
        {
            return;
        }

        _logger.LogInformation("LocalMediaAssets：开始扫描后演员数据同步");
        progress.Report(10);

        await Task.Run(() => _db.Sync(config, SyncTrigger.Auto), cancellationToken).ConfigureAwait(false);

        progress.Report(100);
        _logger.LogInformation("LocalMediaAssets：扫描后演员数据同步完成");
    }
}
