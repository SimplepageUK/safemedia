using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace Simplepage.Umbraco.SafeMedia.Integration;

/// <summary>
/// Checks every SVG and PDF already in the media library against the same rules as an upload.
///
/// The upload filter, analyzer and save guard only see files as they arrive. Anything stored
/// before the package was installed - or while it was configured more leniently - is still on
/// disk and still served from the site's own origin. The save guard re-checks such a file the
/// next time someone saves it, but nobody may ever do that. This finds them now.
///
/// Report only: it never deletes or alters a file. Kept separate from the health check so a
/// scheduled job or command can reuse it.
/// </summary>
public sealed class MediaScanner
{
    private const int PageSize = 500;

    private readonly IMediaService _mediaService;
    private readonly MediaFileManager _mediaFileManager;
    private readonly MediaFileInspector _inspector;
    private readonly ILogger<MediaScanner> _logger;

    public MediaScanner(
        IMediaService mediaService,
        MediaFileManager mediaFileManager,
        MediaFileInspector inspector,
        ILogger<MediaScanner> logger)
    {
        _mediaService = mediaService;
        _mediaFileManager = mediaFileManager;
        _inspector = inspector;
        _logger = logger;
    }

    public MediaScanResult Scan()
    {
        var checkedCount = 0;
        var refused = new List<RefusedMedia>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long pageIndex = 0;
        long total;

        do
        {
            // Descendants of the root include the recycle bin: a trashed file is still on disk.
            var page = _mediaService.GetPagedDescendants(Constants.System.Root, pageIndex++, PageSize, out total);

            foreach (var media in page)
            {
                foreach (var property in media.Properties)
                {
                    foreach (var value in property.Values)
                    {
                        var raw = value.EditedValue as string ?? value.PublishedValue as string;

                        if (string.IsNullOrWhiteSpace(raw) || !MediaFileInspector.MightReferToCheckedFile(raw))
                        {
                            continue;
                        }

                        var inspection = Check(media, property, value.Culture, value.Segment, out var path);

                        if (inspection is null)
                        {
                            continue;
                        }

                        checkedCount++;

                        if (path is not null)
                        {
                            claimed.Add(Normalise(path));
                        }

                        if (!inspection.IsSafe)
                        {
                            refused.Add(new RefusedMedia(media.Key, media.Name ?? string.Empty, path ?? raw, inspection.Kind, media.Trashed, inspection.Problems));
                        }
                    }
                }
            }
        }
        while (pageIndex * PageSize < total);

        // Files on disk that no media item points at. A cancelled save in v14+ leaves exactly
        // this behind (the upload is copied into storage before MediaSaving can refuse it), so
        // a site that ran a save-time-only guard can have hostile files no media item shows.
        foreach (var path in CheckedFilesOnDisk(string.Empty))
        {
            if (claimed.Contains(Normalise(path)))
            {
                continue;
            }

            checkedCount++;
            var inspection = CheckFile(path);

            if (!inspection.IsSafe)
            {
                refused.Add(new RefusedMedia(null, string.Empty, path, inspection.Kind, false, inspection.Problems));
            }
        }

        foreach (var item in refused)
        {
            _logger.LogWarning(
                "SafeMedia scan: stored {Kind} {Path} (media '{Name}', {Key}) would be refused. Reasons: {Reasons}",
                item.Kind, item.Path, item.Name, item.Key, string.Join("; ", item.Problems));
        }

        _logger.LogInformation(
            "SafeMedia scan checked {Checked} files; {Refused} would be refused.", checkedCount, refused.Count);

        return new MediaScanResult(checkedCount, refused);
    }

    private IEnumerable<string> CheckedFilesOnDisk(string directory)
    {
        var fileSystem = _mediaFileManager.FileSystem;

        // Filtered here rather than with a GetFiles pattern, which not every storage provider
        // honours the same way.
        foreach (var file in fileSystem.GetFiles(directory))
        {
            if (MediaFileInspector.Handles(file))
            {
                yield return file;
            }
        }

        foreach (var child in fileSystem.GetDirectories(directory))
        {
            foreach (var file in CheckedFilesOnDisk(child))
            {
                yield return file;
            }
        }
    }

    private MediaInspection CheckFile(string path)
    {
        try
        {
            using var stream = _mediaFileManager.FileSystem.OpenFile(path);
            return _inspector.Inspect(path, stream) ?? MediaFileInspector.Unreadable(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SafeMedia could not read {Path} while scanning.", path);
            return MediaFileInspector.Unreadable(path);
        }
    }

    private string Normalise(string path) =>
        _mediaFileManager.FileSystem.GetRelativePath(path).Replace('\\', '/').Trim('/');

    /// <summary>Null when the value does not resolve to a file SafeMedia checks - not the same as safe.</summary>
    private MediaInspection? Check(IMedia media, IProperty property, string? culture, string? segment, out string? path)
    {
        path = null;

        try
        {
            using var stored = _mediaFileManager.GetFile(media, out path, property.Alias, culture, segment);

            if (string.IsNullOrWhiteSpace(path) || !MediaFileInspector.Handles(path))
            {
                return null;
            }

            return _inspector.Inspect(path, stored);
        }
        catch (Exception ex)
        {
            // Fail CLOSED, as at upload: a file that cannot be read is also one nobody has checked.
            _logger.LogError(ex, "SafeMedia could not read the file on media '{Name}' ({Key}) while scanning.", media.Name, media.Key);
            return path is null ? null : MediaFileInspector.Unreadable(path);
        }
    }
}

public sealed record MediaScanResult(int CheckedCount, IReadOnlyList<RefusedMedia> Refused);

/// <summary>Key is null for a file on disk that no media item references.</summary>
public sealed record RefusedMedia(Guid? Key, string Name, string Path, string Kind, bool InRecycleBin, IReadOnlyList<string> Problems);
