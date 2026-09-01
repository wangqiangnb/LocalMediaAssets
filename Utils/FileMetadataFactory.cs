using System;
using System.IO;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.LocalMediaAssets.Utils;

/// <summary>
/// 把本地文件路径转换为 <see cref="FileSystemMetadata"/>。
/// </summary>
internal static class FileMetadataFactory
{
    public static FileSystemMetadata Create(string path)
    {
        var fi = new FileInfo(path);
        return new FileSystemMetadata
        {
            FullName = fi.FullName,
            Name = fi.Name,
            Extension = fi.Extension,
            IsDirectory = false,
            Exists = fi.Exists,
            Length = fi.Exists ? fi.Length : 0,
            LastWriteTimeUtc = fi.Exists ? fi.LastWriteTimeUtc : DateTime.UtcNow
        };
    }
}
