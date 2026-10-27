using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Simplepage.Umbraco.SafeMedia.Svg;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace Simplepage.Umbraco.SafeMedia.Integration;

/// <summary>
/// Checks every SVG already in the media library against the same rules as an upload.
///
/// The upload filter, analyzer and save guard only see files as they arrive. Anything stored
/// before the package was installed - or while it was configured more leniently - is still on
/// disk and still served from the site's own origin. The save guard re-checks such a file the
/// next time someone saves it, but nobody may ever do that. This finds them now.
///
/// Report only: it never deletes or alters a file. Kept separate from the health check so a
/// scheduled job or command can reuse it.
/// </summary>
public sealed class SvgMediaScanner
{
    private const int PageSize = 500;

    private readonly IMediaService _mediaService;
    private readonly MediaFileManager _mediaFileManager;
    private readonly IOptionsMonitor<SvgSafetyOptions> _options;
    private readonly ILogger<SvgMediaScanner> _logger;

    public SvgMediaScanner(
        IMediaService mediaService,
        MediaFileManager mediaFileManager,
        IOptionsMonitor<SvgSafetyOptions> options,
        ILogger<SvgMediaScanner> logger)
    {
        _mediaService = mediaService;
        _mediaFileManager = mediaFileManager;
        _options = options;
        _logger = logger;
    }

    public SvgMediaScanResult Scan()
    {
        var options = _options.CurrentValue;
        var checkedCount = 0;
        var refused = new List<RefusedSvgMedia>();
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

                        if (string.IsNullOrWhiteSpace(raw)
                            || !raw.Contains(".svg", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var result = Check(media, property, value.Culture, value.Segment, options, out var path);

                        if (result is null)
                        {
                            continue;
                        }

                        checkedCount++;

                        if (path is not null)
                        {
                            claimed.Add(Normalise(path));
                        }

                        if (!result.IsSafe)
                        {
                            refused.Add(new RefusedSvgMedia(media.Key, media.Name ?? string.Empty, path ?? raw, media.Trashed, result.Problems));
                        }
                    }
                }
            }
        }
        while (pageIndex * PageSize < total);

        // Files on disk that no media item points at. A cancelled save in v14+ leaves exactly
        // this behind (the upload is copied into storage before MediaSaving can refuse it), so
        // a site that ran a save-time-only guard can have hostile files no media item shows.
        foreach (var path in SvgFilesOnDisk(string.Empty))
        {
            if (claimed.Contains(Normalise(path)))
            {
                continue;
            }

            checkedCount++;
            var result = CheckFile(path, options);

            if (!result.IsSafe)
            {
                refused.Add(new RefusedSvgMedia(null, string.Empty, path, false, result.Problems));
            }
        }

        foreach (var item in refused)
        {
            _logger.LogWarning(
                "SafeMedia scan: stored SVG {Path} (media '{Name}', {Key}) would be refused. Reasons: {Reasons}",
                item.Name, item.Key, item.Path, string.Join("; ", item.Problems));
        }

        _logger.LogInformation(
            "SafeMedia scan checked {Checked} SVG files; {Refused} would be refused.", checkedCount, refused.Count);

        return new SvgMediaScanResult(checkedCount, refused);
    }

    private IEnumerable<string> SvgFilesOnDisk(string directory)
    {
        var fileSystem = _mediaFileManager.FileSystem;

        // Filtered here rather than with a GetFiles pattern, which not every storage provider
        // honours the same way.
        foreach (var file in fileSystem.GetFiles(directory))
        {
            if (file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }

        foreach (var child in fileSystem.GetDirectories(directory))
        {
            foreach (var file in SvgFilesOnDisk(child))
            {
                yield return file;
            }
        }
    }

    private SvgSafetyResult CheckFile(string path, SvgSafetyOptions options)
    {
        if (path.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase))
        {
            return SvgSafetyResult.Unsafe(new[] { "is a compressed SVG (.svgz), which cannot be checked" });
        }

        try
        {
            using var stream = _mediaFileManager.FileSystem.OpenFile(path);
            return SvgSafetyCheck.Check(stream, options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SafeMedia could not read {Path} while scanning.", path);
            return SvgSafetyResult.Unsafe(new[] { "could not be read for checking" });
        }
    }

    private string Normalise(string path) =>
        _mediaFileManager.FileSystem.GetRelativePath(path).Replace('\\', '/').Trim('/');

    /// <summary>Null when the value does not resolve to an SVG file - not the same as safe.</summary>
    private SvgSafetyResult? Check(
        IMedia media, IProperty property, string? culture, string? segment, SvgSafetyOptions options, out string? path)
    {
        path = null;

        try
        {
            using var stored = _mediaFileManager.GetFile(media, out path, property.Alias, culture, segment);

            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (path.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase))
            {
                return SvgSafetyResult.Unsafe(new[] { "is a compressed SVG (.svgz), which cannot be checked" });
            }

            if (!path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return SvgSafetyCheck.Check(stored, options);
        }
        catch (Exception ex)
        {
            // Fail CLOSED, as at upload: a file that cannot be read is also one nobody has checked.
            _logger.LogError(ex, "SafeMedia could not read SVG on media '{Name}' ({Key}) while scanning.", media.Name, media.Key);
            return SvgSafetyResult.Unsafe(new[] { "could not be read for checking" });
        }
    }
}

public sealed record SvgMediaScanResult(int CheckedCount, IReadOnlyList<RefusedSvgMedia> Refused);

/// <summary>Key is null for a file on disk that no media item references.</summary>
public sealed record RefusedSvgMedia(Guid? Key, string Name, string Path, bool InRecycleBin, IReadOnlyList<string> Problems);
