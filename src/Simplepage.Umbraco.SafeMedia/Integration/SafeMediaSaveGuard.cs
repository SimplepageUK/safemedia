using System.Text.Json;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

namespace Simplepage.Umbraco.SafeMedia.Integration;

/// <summary>
/// Refuses to save a media item whose SVG or PDF fails its check, and tells the editor why. For
/// a file that passes with a warning, lets the save go ahead and shows the warning.
///
/// Hooks MediaSavingNotification, which is cancellable, so a bad file never reaches the
/// database. Cancelling here surfaces the reason in the back office rather than failing
/// silently.
///
/// THIS IS THE LAST LINE, NOT THE FIRST
///
/// v14+ uploads in two steps: the browser POSTs to the temporary file endpoint and gets a
/// GUID, then saves the media item carrying that GUID. The upload property's value editor
/// copies the temporary file into media storage while mapping the save - which happens BEFORE
/// this notification. So for an ordinary back office upload, the value here is already a
/// stored src path and the file is already on disk. Cancelling alone would leave it there,
/// publicly served. <see cref="SafeMediaUploadFilter"/> and <see cref="SafeMediaFileStreamAnalyzer"/>
/// refuse at the upload, before anything is written; this guard catches what they cannot (a
/// file whose content and name disagree), files stored before the package was installed, and
/// deletes any file it has just refused.
///
/// WHERE THE FILE ACTUALLY IS AT THIS POINT
///
/// The value is EITHER a temporary file id (a value editor that defers the copy, or a save
/// through code) OR an already-stored src path (a fresh upload as above, a rename, a move, a
/// focal point change). Both shapes are handled: temporary file first, stored file second. A
/// save that touches neither costs one string check and returns.
/// </summary>
public sealed class SafeMediaSaveGuard : INotificationHandler<MediaSavingNotification>
{
    private readonly ITemporaryFileService _temporaryFileService;
    private readonly MediaFileManager _mediaFileManager;
    private readonly IMediaService _mediaService;
    private readonly MediaFileInspector _inspector;
    private readonly ILogger<SafeMediaSaveGuard> _logger;

    public SafeMediaSaveGuard(
        ITemporaryFileService temporaryFileService,
        MediaFileManager mediaFileManager,
        IMediaService mediaService,
        MediaFileInspector inspector,
        ILogger<SafeMediaSaveGuard> logger)
    {
        _temporaryFileService = temporaryFileService;
        _mediaFileManager = mediaFileManager;
        _mediaService = mediaService;
        _inspector = inspector;
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

                    if (string.IsNullOrWhiteSpace(raw) || !MediaFileInspector.MightReferToCheckedFile(raw))
                    {
                        continue;
                    }

                    var (inspection, storedPath) = Inspect(raw, media, property, value.Culture, value.Segment);

                    if (inspection is null)
                    {
                        continue;
                    }

                    if (inspection.IsSafe)
                    {
                        // Only for a file this save has just written: a warning that repeats on
                        // every later rename or move of the same file would just be noise.
                        if (inspection.WarningMessage is not null
                            && (storedPath is null || IsWrittenBySave(storedPath, raw, media, property, value.Culture, value.Segment)))
                        {
                            notification.Messages.Add(new EventMessage(
                                inspection.WarningTitle, inspection.WarningMessage, EventMessageType.Warning));
                        }

                        continue;
                    }

                    _logger.LogWarning(
                        "SafeMedia refused {Kind} on media '{Name}' ({Key}). Reasons: {Reasons}",
                        inspection.Kind, media.Name, media.Key, string.Join("; ", inspection.Problems));

                    if (storedPath is not null && IsWrittenBySave(storedPath, raw, media, property, value.Culture, value.Segment))
                    {
                        Delete(storedPath);
                    }

                    notification.CancelOperation(
                        new EventMessage(inspection.RefusedTitle, inspection.Message, EventMessageType.Error));

                    return;
                }
            }
        }
    }

    /// <summary>
    /// Null when there is nothing to check - which is NOT the same as "safe", and is why the
    /// caller tests for null separately. A value we cannot resolve to a stream must not be
    /// treated as having passed.
    /// </summary>
    private (MediaInspection? Inspection, string? StoredPath) Inspect(
        string rawValue, IMedia media, IProperty property, string? culture, string? segment)
    {
        var name = rawValue;

        try
        {
            var temporaryFileKey = ReadTemporaryFileKey(rawValue);

            if (temporaryFileKey is not null)
            {
                var temporaryFile = _temporaryFileService
                    .GetAsync(temporaryFileKey.Value)
                    .GetAwaiter()
                    .GetResult();

                if (temporaryFile is null || !MediaFileInspector.Handles(temporaryFile.FileName))
                {
                    return (null, null);
                }

                name = temporaryFile.FileName;
                using var temporaryStream = temporaryFile.OpenReadStream();
                return (_inspector.Inspect(temporaryFile.FileName, temporaryStream), null);
            }

            // Already stored. Re-checking on every save is cheap and means a file that
            // predates this guard gets caught the next time an editor touches the item.
            using var stored = _mediaFileManager.GetFile(media, out var path, property.Alias, culture, segment);

            if (string.IsNullOrWhiteSpace(path) || !MediaFileInspector.Handles(path))
            {
                return (null, null);
            }

            name = path;
            return (_inspector.Inspect(path, stored), path);
        }
        catch (Exception ex)
        {
            // Fail CLOSED. If the file cannot be read or parsed, refusing the save is the safe
            // outcome - the alternative is that an unreadable file is also an unchecked one.
            _logger.LogError(ex, "SafeMedia could not inspect the file on media '{Name}'.", media.Name);
            return (MediaFileInspector.Unreadable(name), null);
        }
    }

    /// <summary>
    /// True when the stored file was written by this save, rather than being what the item
    /// already had. Only a file at the path Umbraco generates for this item and property, and
    /// that differs from the persisted value, counts.
    /// </summary>
    private bool IsWrittenBySave(
        string storedPath, string rawValue, IMedia media, IProperty property, string? culture, string? segment)
    {
        try
        {
            var relativePath = _mediaFileManager.FileSystem.GetRelativePath(storedPath);
            var expectedPath = _mediaFileManager.GetMediaPath(Path.GetFileName(relativePath), media.Key, property.PropertyType.Key);

            if (!SamePath(relativePath, expectedPath))
            {
                return false;
            }

            if (!media.HasIdentity)
            {
                return true;
            }

            var persisted = _mediaService.GetById(media.Key)?.GetValue(property.Alias, culture, segment) as string;
            return !string.Equals(persisted, rawValue, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SafeMedia could not tell whether {Path} was written by this save.", storedPath);
            return false;
        }
    }

    /// <summary>
    /// Deletes the file a refused save has just written, so a cancelled upload does not leave a
    /// hostile file on a public URL. A file that predates the package, re-checked on a rename,
    /// is refused but left alone - the health check reports those.
    /// </summary>
    private void Delete(string storedPath)
    {
        try
        {
            var relativePath = _mediaFileManager.FileSystem.GetRelativePath(storedPath);
            _mediaFileManager.FileSystem.DeleteFile(relativePath);
            _logger.LogInformation("SafeMedia deleted refused file {Path}.", relativePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SafeMedia refused a file but could not delete it from {Path}. Delete it manually.", storedPath);
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
