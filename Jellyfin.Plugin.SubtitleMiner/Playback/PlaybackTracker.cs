using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;

namespace Jellyfin.Plugin.SubtitleMiner.Playback;

/// <summary>
/// Remembers what every player is playing and what was watched lately, and tells the open pages when
/// something changed that they cannot work out themselves. A page moves the position with its own clock,
/// so a steady progress report is no news: a start, a stop, a pause, a jump and a change of track are.
/// </summary>
internal sealed class PlaybackTracker
{
    /// <summary>How long a watch stays listed after playback ended.</summary>
    internal static readonly TimeSpan KeepStopped = TimeSpan.FromHours(12);

    /// <summary>A player that has said nothing for this long is taken to have stopped.</summary>
    internal static readonly TimeSpan SilentAfter = TimeSpan.FromMinutes(10);

    /// <summary>How far a report may be from where the pages think playback is before they are told.</summary>
    internal const long DriftMs = 700;

    /// <summary>The most watches one user is shown.</summary>
    internal const int MaxPerUser = 12;

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, List<Channel<bool>>> _listeners = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackTracker"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    public PlaybackTracker(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// Takes in what a player reported: that it started, or how far it is.
    /// </summary>
    /// <param name="report">The report.</param>
    public void Report(PlaybackReport report)
    {
        var now = _time.GetUtcNow();
        var news = false;
        HashSet<Guid> silent;
        lock (_lock)
        {
            var id = IdOf(report);

            // A player plays one video at a time. When it moves on, what it played before is over.
            foreach (var other in _entries.Values.Where(e => e.Watch.Playing && e.Watch.Report.SessionId == report.SessionId && e.Watch.Id != id).ToList())
            {
                other.Watch = other.Watch with { Playing = false, StoppedAt = now };
                news = true;
            }

            if (_entries.TryGetValue(id, out var entry) && entry.Watch.Playing)
            {
                var old = entry.Watch.Report;
                var expected = entry.Told.PositionAt(now);
                news |= old.Paused != report.Paused
                    || old.AudioStreamIndex != report.AudioStreamIndex
                    || old.SubtitleStreamIndex != report.SubtitleStreamIndex
                    || Math.Abs(report.PositionMs - expected) > DriftMs;
                entry.Watch = entry.Watch with { Report = report, At = now };
            }
            else
            {
                entry = new Entry { Watch = new Watch(id, report, now, true, null) };
                _entries[id] = entry;
                news = true;
            }

            if (news)
            {
                entry.Told = entry.Watch;
            }

            silent = Forget(now);
        }

        if (news)
        {
            silent.Add(report.UserId);
        }

        foreach (var user in silent)
        {
            Tell(user);
        }
    }

    /// <summary>
    /// Takes in that a player closed its video.
    /// </summary>
    /// <param name="sessionId">The player's session.</param>
    /// <param name="positionMs">Where playback ended, when the player said so.</param>
    public void Stop(string sessionId, long? positionMs)
    {
        var now = _time.GetUtcNow();
        var users = new HashSet<Guid>();
        lock (_lock)
        {
            foreach (var entry in _entries.Values.Where(e => e.Watch.Playing && e.Watch.Report.SessionId == sessionId))
            {
                var report = entry.Watch.Report;
                entry.Watch = entry.Watch with
                {
                    Report = positionMs is null ? report with { PositionMs = entry.Watch.PositionAt(now) } : report with { PositionMs = positionMs.Value },
                    At = now,
                    Playing = false,
                    StoppedAt = now,
                };
                entry.Told = entry.Watch;
                users.Add(report.UserId);
            }
        }

        foreach (var user in users)
        {
            Tell(user);
        }
    }

    /// <summary>
    /// Lists a user's watches: what plays now first, then what was watched lately, newest first. A video
    /// is listed once.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The watches.</returns>
    public IReadOnlyList<Watch> ListFor(Guid userId)
    {
        var now = _time.GetUtcNow();
        IReadOnlyList<Watch> list;
        HashSet<Guid> silent;
        lock (_lock)
        {
            silent = Forget(now);
            var mine = _entries.Values.Select(entry => entry.Watch).Where(watch => watch.Report.UserId == userId).ToList();
            var playing = mine.Where(watch => watch.Playing).OrderByDescending(watch => watch.At).ToList();
            var open = playing.Select(watch => (watch.Report.ItemId, watch.Report.MediaSourceId)).ToHashSet();
            var over = mine
                .Where(watch => !watch.Playing && !open.Contains((watch.Report.ItemId, watch.Report.MediaSourceId)))
                .GroupBy(watch => (watch.Report.ItemId, watch.Report.MediaSourceId))
                .Select(group => group.MaxBy(watch => watch.StoppedAt)!)
                .OrderByDescending(watch => watch.StoppedAt);
            list = [.. playing.Concat(over).Take(MaxPerUser)];
        }

        foreach (var user in silent)
        {
            Tell(user);
        }

        return list;
    }

    /// <summary>
    /// Ends the watches of players that went silent without saying they stopped, and tells the pages. A
    /// player that loses its network sends no stop, and the pages would show it playing on.
    /// </summary>
    public void Sweep()
    {
        HashSet<Guid> silent;
        lock (_lock)
        {
            silent = Forget(_time.GetUtcNow());
        }

        foreach (var user in silent)
        {
            Tell(user);
        }
    }

    /// <summary>
    /// Starts listening for news about a user's watches.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The listener. Dispose it when the page is gone.</returns>
    public Listener Listen(Guid userId)
    {
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        lock (_lock)
        {
            if (!_listeners.TryGetValue(userId, out var list))
            {
                list = [];
                _listeners[userId] = list;
            }

            list.Add(channel);
        }

        return new Listener(channel.Reader, () =>
        {
            lock (_lock)
            {
                if (_listeners.TryGetValue(userId, out var list) && list.Remove(channel) && list.Count == 0)
                {
                    _listeners.Remove(userId);
                }
            }
        });
    }

    /// <summary>
    /// How many pages of one user listen at the moment.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The number of listeners.</returns>
    public int ListenerCountOf(Guid userId)
    {
        lock (_lock)
        {
            return _listeners.TryGetValue(userId, out var list) ? list.Count : 0;
        }
    }

    private static string IdOf(PlaybackReport report)
    {
        var name = string.Join('|', report.SessionId, report.ItemId.ToString("N"), report.MediaSourceId);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0, 8);
    }

    private void Tell(Guid userId)
    {
        List<Channel<bool>> listeners;
        lock (_lock)
        {
            if (!_listeners.TryGetValue(userId, out var list))
            {
                return;
            }

            listeners = [.. list];
        }

        foreach (var listener in listeners)
        {
            // A listener that has news waiting already will read the latest state anyway.
            listener.Writer.TryWrite(true);
        }
    }

    /// <summary>
    /// Ends the watches of players that went silent and drops what is too old. Call with the lock held.
    /// </summary>
    /// <returns>The users whose watches ended here, for the pages to be told.</returns>
    private HashSet<Guid> Forget(DateTimeOffset now)
    {
        var silent = new HashSet<Guid>();
        foreach (var (id, entry) in _entries.ToList())
        {
            var watch = entry.Watch;
            if (watch.Playing && now - watch.At > SilentAfter)
            {
                entry.Watch = watch with { Playing = false, StoppedAt = watch.At };
                entry.Told = entry.Watch;
                silent.Add(watch.Report.UserId);
            }
            else if (!watch.Playing && now - watch.StoppedAt > KeepStopped)
            {
                _entries.Remove(id);
            }
        }

        return silent;
    }

    /// <summary>
    /// A page's ear for news about one user's watches.
    /// </summary>
    internal sealed class Listener : IDisposable
    {
        private readonly Action _stop;

        internal Listener(ChannelReader<bool> news, Action stop)
        {
            News = news;
            _stop = stop;
        }

        /// <summary>
        /// Gets what yields a value each time the user's watches changed.
        /// </summary>
        public ChannelReader<bool> News { get; }

        /// <inheritdoc />
        public void Dispose() => _stop();
    }

    private sealed class Entry
    {
        /// <summary>Gets or sets the watch as the latest report left it.</summary>
        public required Watch Watch { get; set; }

        /// <summary>Gets or sets the watch as the pages were last told, which is what they count on from.</summary>
        public Watch Told { get; set; } = null!;
    }
}
