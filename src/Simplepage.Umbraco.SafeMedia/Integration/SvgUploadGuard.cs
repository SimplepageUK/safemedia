using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Simplepage.Umbraco.SafeMedia.Svg;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

namespace Simplepage.Umbraco.SafeMedia.Integration;

/// <summary>
/// Refuses to save a media item whose SVG fails <see cref="SvgSafetyCheck"/>, and tells the
/// editor why.
///
/// Hooks MediaSavingNotification, which is cancellable, so a bad file never reaches the
/// database. Cancelling here surfaces the reason in the back office rather than failing
/// silently.
///
/// THIS IS THE SECOND LINE, NOT THE FIRST
///
/// v14+ uploads in two steps: the browser POSTs to the temporary file endpoint and gets a
/// GUID, then saves the media item carrying that GUID. The upload property's value editor
/// copies the temporary file into media storage while mapping the save - which happens BEFORE
/// this notification. So for an ordinary back office upload, the value here is already a
/// stored src path and the file is already on disk. Cancelling alone would leave it there,
/// publicly served. <see cref="SvgFileStreamSecurityAnalyzer"/> refuses at the upload, before
/// anything is written; this guard catches what the analyzer's sniff cannot (a .svg file
/// whose root is not &lt;svg&gt;), files stored before the package was installed, and
/// deletes any file it has just refused.
///
/// WHERE THE FILE ACTUALLY IS AT THIS POINT
///
/// The value is EITHER a temporary file id (a value editor that defers the copy, or a save
/// through code) OR an already-stored src path (a fresh upload as above, a rename, a move, a
/// focal point change). Both shapes are handled: temporary file first, stored file second. A
/// save that touches neither - no SVG involved - costs one string check and returns.
/// </summary>
public sealed class SvgUploadGuard : INotificationHandler<MediaSavingNotification>
{
    private readonly ITemporaryFileService _temporaryFileService;
    private readonly MediaFileManager _mediaFileManager;
    private readonly IMediaService _mediaService;
    private readonly IOptionsMonitor<SvgSafetyOptions> _options;
    private readonly ILogger<SvgUploadGuard> _logger;

    public SvgUploadGuard(
        ITemporaryFileService temporaryFileService,
        MediaFileManager mediaFileManager,
        IMediaService mediaService,
        IOptionsMonitor<SvgSafetyOptions> options,
        ILogger<SvgUploadGuard> logger)
    {
        _temporaryFileService = temporaryFileService;
        _mediaFileManager = mediaFileManager;
        _mediaService = mediaService;
        _options = options;
        _logger = logger;
    }

    public void Handle(MediaSavingNotification notification)
    {
        foreach (var media in notification.SavedEntities)
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

                    var inspection = Inspect(raw, media, property, value.Culture, value.Segment);

                    if (inspection.Result is null || inspection.Result.IsSafe)
                    {
                        continue;
                    }

                    _logger.LogWarning(
                        "SafeMedia refused SVG on media '{Name}' ({Key}). Reasons: {Reasons}",
                        media.Name, media.Key, string.Join("; ", inspection.Result.Problems));

                    RemoveFileWrittenBySave(inspection.StoredPath, raw, media, property, value.Culture, value.Segment);

                    notification.CancelOperation(
                        new EventMessage("SVG rejected", inspection.Result.ToMessage(), EventMessageType.Error));

                    return;
                }
            }
        }
    }

    /// <summary>
    /// Result is null when there is nothing to check - which is NOT the same as "safe", and is
    /// why the caller tests for null separately. A value we cannot resolve to a stream must not
    /// be treated as having passed.
    /// </summary>
    private (SvgSafetyResult? Result, string? StoredPath) Inspect(
        string rawValue, IMedia media, IProperty property, string? culture, string? segment)
    {
        try
        {
            var temporaryFileKey = ReadTemporaryFileKey(rawValue);

            if (temporaryFileKey is not null)
            {
                var temporaryFile = _temporaryFileService
                    .GetAsync(temporaryFileKey.Value)
                    .GetAwaiter()
                    .GetResult();

                if (temporaryFile is null)
                {
                    return (null, null);
                }

                if (Refuse(temporaryFile.FileName) is { } refused)
                {
                    return (refused, null);
                }

                if (!IsSvg(temporaryFile.FileName))
                {
                    return (null, null);
                }

                using var temporaryStream = temporaryFile.OpenReadStream();
                return (SvgSafetyCheck.Check(temporaryStream, _options.CurrentValue), null);
            }

            // Already stored. Re-checking on every save is cheap and means a file that
            // predates this guard gets caught the next time an editor touches the item.
            using var stored = _mediaFileManager.GetFile(media, out var path, property.Alias, culture, segment);

            if (string.IsNullOrWhiteSpace(path))
            {
                return (null, null);
            }

            if (Refuse(path) is { } refusedStored)
            {
                return (refusedStored, path);
            }

            if (!IsSvg(path))
            {
                return (null, null);
            }

            return (SvgSafetyCheck.Check(stored, _options.CurrentValue), path);
        }
        catch (Exception ex)
        {
            // Fail CLOSED. If the file cannot be read or parsed, refusing the save is the safe
            // outcome - the alternative is that an unreadable file is also an unchecked one.
            _logger.LogError(ex, "SafeMedia could not inspect SVG on media '{Name}'.", media.Name);

            return (SvgSafetyResult.Unsafe(new[]
            {
                "could not be read for checking, so it has not been saved",
            }), null);
        }
    }

    private static bool IsSvg(string fileName) =>
        fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A gzipped SVG cannot be inspected without decompressing it, and a browser will happily
    /// render one served with the right headers, so it is refused rather than waved through.
    /// </summary>
    private static SvgSafetyResult? Refuse(string fileName) =>
        fileName.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase)
            ? SvgSafetyResult.Unsafe(new[] { "is a compressed SVG (.svgz), which cannot be checked" })
            : null;

    /// <summary>
    /// Deletes the file a refused save has just written, so a cancelled upload does not leave a
    /// hostile file on a public URL. Only ever deletes a file that is (a) at the path Umbraco
    /// generates for this item and property, and (b) not what the item already had saved. A
    /// file that predates the package, re-checked on a rename, is refused but left alone.
    /// </summary>
    private void RemoveFileWrittenBySave(
        string? storedPath, string rawValue, IMedia media, IProperty property, string? culture, string? segment)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return;
        }

        try
        {
            var fileSystem = _mediaFileManager.FileSystem;
            var relativePath = fileSystem.GetRelativePath(storedPath);
            var expectedPath = _mediaFileManager.GetMediaPath(Path.GetFileName(relativePath), media.Key, property.PropertyType.Key);

            if (!SamePath(relativePath, expectedPath))
            {
                return;
            }

            if (media.HasIdentity)
            {
                var persisted = _mediaService.GetById(media.Key)?.GetValue(property.Alias, culture, segment) as string;

                if (string.Equals(persisted, rawValue, StringComparison.Ordinal))
                {
                    return;
                }
            }

            fileSystem.DeleteFile(relativePath);
            _logger.LogInformation("SafeMedia deleted refused file {Path}.", relativePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SafeMedia refused an SVG but could not delete the stored file {Path}. Delete it manually.", storedPath);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('\\', '/').Trim('/'), b.Replace('\\', '/').Trim('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pulls a temporary file GUID out of the upload or image cropper value. Both are JSON;
    /// the cropper nests the file alongside crops and a focal point, so the key is read by
    /// name rather than by assuming a shape.
    /// </summary>
    private static Guid? ReadTemporaryFileKey(string rawValue)
    {
        if (!rawValue.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawValue);

            foreach (var name in new[] { "temporaryFileId", "temporaryFileKey", "src" })
            {
                if (document.RootElement.TryGetProperty(name, out var element)
                    && element.ValueKind == JsonValueKind.String
                    && Guid.TryParse(element.GetString(), out var key))
                {
                    return key;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON we understand; fall through to the stored-file path.
        }

        return null;
    }
}
