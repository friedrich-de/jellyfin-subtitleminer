using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.SubtitleMiner.Library;

/// <summary>
/// Reads videos and their streams from Jellyfin.
/// </summary>
internal sealed class JellyfinMediaLibrary : IMediaLibrary
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;

    public JellyfinMediaLibrary(ILibraryManager libraryManager, IUserManager userManager, IMediaSourceManager mediaSourceManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
    }

    /// <inheritdoc />
    public MediaInfo? Find(Guid userId, Guid itemId, string? mediaSourceId)
    {
        var user = _userManager.GetUserById(userId);

        // IsVisible looks at the age rating only. IsVisibleStandalone also asks whether the user has
        // the library the video is in, the way Jellyfin's own item endpoints do.
        if (user is null || _libraryManager.GetItemById(itemId) is not Video video || !video.IsVisibleStandalone(user))
        {
            return null;
        }

        var sources = _mediaSourceManager.GetStaticMediaSources(video, false, user);
        var source = sources.FirstOrDefault(s => string.Equals(s.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            // A version that is asked for by name and is not there is not swapped for another one.
            if (!string.IsNullOrEmpty(mediaSourceId) || sources.Count == 0)
            {
                return null;
            }

            source = sources[0];
        }

        var path = source.Protocol == MediaProtocol.File && File.Exists(source.Path) ? source.Path : null;
        return new MediaInfo(
            video.Id,
            source.Id,
            path,
            StampOf(path),
            TitleOf(video),
            source.RunTimeTicks is null ? null : source.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond,
            Streams(source));
    }

    /// <summary>
    /// Names the streams of one version of a video.
    /// </summary>
    internal static List<MediaStreamInfo> Streams(MediaSourceInfo source)
    {
        var streams = new List<MediaStreamInfo>();
        var ordinals = new Dictionary<StreamKind, int>();
        foreach (var stream in source.MediaStreams.OrderBy(stream => stream.Index))
        {
            StreamKind kind;
            switch (stream.Type)
            {
                case MediaStreamType.Video:
                    kind = StreamKind.Video;
                    break;
                case MediaStreamType.Audio:
                    kind = StreamKind.Audio;
                    break;
                case MediaStreamType.Subtitle:
                    kind = StreamKind.Subtitle;
                    break;
                default:
                    continue;
            }

            var ordinal = -1;
            string key;
            if (stream.IsExternal)
            {
                key = "x" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(stream.Path ?? string.Empty))), 0, 6);
            }
            else
            {
                ordinal = ordinals.GetValueOrDefault(kind);
                ordinals[kind] = ordinal + 1;
                key = string.Create(CultureInfo.InvariantCulture, $"{(kind == StreamKind.Audio ? 'a' : kind == StreamKind.Subtitle ? 's' : 'v')}{ordinal}");
            }

            streams.Add(new MediaStreamInfo(
                key,
                kind,
                stream.Index,
                ordinal,
                (stream.Codec ?? string.Empty).ToLowerInvariant(),
                string.IsNullOrWhiteSpace(stream.Language) ? null : stream.Language,
                string.IsNullOrWhiteSpace(stream.Title) ? null : stream.Title,
                stream.IsExternal,
                stream.IsDefault,
                stream.IsForced,
                stream.IsExternal ? stream.Path : null));
        }

        return streams;
    }

    private static MediaTitle TitleOf(Video video) => video is Episode episode
        ? new MediaTitle(episode.Name, episode.SeriesName, episode.ParentIndexNumber, episode.IndexNumber, null)
        : new MediaTitle(video.Name, null, null, null, video.ProductionYear);

    private static string StampOf(string? path)
    {
        if (path is null)
        {
            return string.Empty;
        }

        var file = new FileInfo(path);
        return string.Create(CultureInfo.InvariantCulture, $"{file.Length}-{file.LastWriteTimeUtc.Ticks}");
    }
}
