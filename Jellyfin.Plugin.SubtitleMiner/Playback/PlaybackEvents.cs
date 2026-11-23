using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.SubtitleMiner.Playback;

/// <summary>
/// Hears from Jellyfin when a player starts, moves on or stops, and passes it to the tracker.
/// </summary>
internal sealed class PlaybackEvents : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly PlaybackTracker _tracker;

    public PlaybackEvents(ISessionManager sessionManager, PlaybackTracker tracker)
    {
        _sessionManager = sessionManager;
        _tracker = tracker;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnStart;
        _sessionManager.PlaybackProgress += OnProgress;
        _sessionManager.PlaybackStopped += OnStopped;

        // What already plays when the plugin starts.
        foreach (var session in _sessionManager.Sessions)
        {
            if (session.NowPlayingItem is { MediaType: MediaType.Video } item && session.UserId != Guid.Empty)
            {
                _tracker.Report(ReportOf(session, item.Id, session.PlayState?.MediaSourceId, session.PlayState?.PositionTicks, session.PlayState?.IsPaused ?? false));
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnStart;
        _sessionManager.PlaybackProgress -= OnProgress;
        _sessionManager.PlaybackStopped -= OnStopped;
        return Task.CompletedTask;
    }

    private static PlaybackReport ReportOf(SessionInfo session, Guid itemId, string? mediaSourceId, long? positionTicks, bool paused)
    {
        // A player that shows no subtitles reports -1.
        var subtitle = session.PlayState?.SubtitleStreamIndex;
        return new PlaybackReport(
            session.Id,
            session.UserId,
            session.DeviceName ?? string.Empty,
            session.Client ?? string.Empty,
            itemId,
            string.IsNullOrEmpty(mediaSourceId) ? itemId.ToString("N") : mediaSourceId,
            (positionTicks ?? 0) / TimeSpan.TicksPerMillisecond,
            paused,
            session.PlayState?.AudioStreamIndex,
            subtitle is >= 0 ? subtitle : null);
    }

    // Jellyfin 12.4 leaves IsPaused out of the start event. The session took it from the player's report.
    private void OnStart(object? sender, PlaybackProgressEventArgs e) => Take(e, e.Session?.PlayState?.IsPaused ?? false);

    private void OnProgress(object? sender, PlaybackProgressEventArgs e) => Take(e, e.IsPaused);

    private void Take(PlaybackProgressEventArgs e, bool paused)
    {
        if (e.Session is null || e.Session.UserId == Guid.Empty || e.Item is not Video)
        {
            return;
        }

        _tracker.Report(ReportOf(
            e.Session,
            e.Item.Id,
            e.MediaSourceId ?? e.Session.PlayState?.MediaSourceId,
            e.PlaybackPositionTicks ?? e.Session.PlayState?.PositionTicks,
            paused));
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Session is not null)
        {
            _tracker.Stop(e.Session.Id, e.PlaybackPositionTicks is null ? null : e.PlaybackPositionTicks.Value / TimeSpan.TicksPerMillisecond);
        }
    }
}
