using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalMediaAssets.Models;
using Jellyfin.Plugin.LocalMediaAssets.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalMediaAssets.Providers;

/// <summary>
/// 演员信息本地提供器：按「视频目录 → 演员库」顺序读取 actors/&lt;演员名&gt;.json 中的简介。
/// </summary>
public sealed class LocalPersonMetadataProvider : ILocalMetadataProvider<Person>, IHasOrder
{
    private readonly PersonImageIndexer _indexer;
    private readonly ILogger<LocalPersonMetadataProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalPersonMetadataProvider"/> class.
    /// </summary>
    public LocalPersonMetadataProvider(PersonImageIndexer indexer, ILogger<LocalPersonMetadataProvider> logger)
    {
        _indexer = indexer;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "LocalMediaAssets Person Metadata";

    /// <inheritdoc />
    public int Order => -10;

    /// <inheritdoc />
    public Task<MetadataResult<Person>> GetMetadata(ItemInfo info, IDirectoryService directoryService, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Person>();

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Task.FromResult(result);
        }

        // Person 的 Path 指向服务器数据目录 metadata/People/<前缀>/<演员名>，演员名即最后一级目录名。
        var personName = string.IsNullOrEmpty(info.Path) ? null : Path.GetFileName(info.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(personName))
        {
            return Task.FromResult(result);
        }

        var file = _indexer.FindPersonInfoFile(personName, config);
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            return Task.FromResult(result);
        }

        ActorInfoFile? data = null;
        try
        {
            data = JsonSerializer.Deserialize<ActorInfoFile>(File.ReadAllText(file));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalMediaAssets：解析演员信息文件失败 {File}", file);
        }

        if (data is null)
        {
            return Task.FromResult(result);
        }

        var person = new Person();
        if (!string.IsNullOrWhiteSpace(data.Name))
        {
            person.Name = data.Name;
        }

        if (!string.IsNullOrWhiteSpace(data.Overview))
        {
            person.Overview = data.Overview;
        }

        result.Item = person;
        result.HasMetadata = !string.IsNullOrWhiteSpace(data.Name) || !string.IsNullOrWhiteSpace(data.Overview);

        _logger.LogDebug("LocalMediaAssets：为演员 {Name} 使用本地信息文件 {File}", personName, file);
        return Task.FromResult(result);
    }
}
