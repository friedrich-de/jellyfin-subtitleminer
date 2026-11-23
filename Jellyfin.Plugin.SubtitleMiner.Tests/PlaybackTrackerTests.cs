using System;
using System.Linq;
using Jellyfin.Plugin.SubtitleMiner.Playback;
using Jellyfin.Plugin.SubtitleMiner.Tests.Support;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public class PlaybackTrackerTests
{
    private readonly ManualTime _time = new();
    private readonly PlaybackTracker _tracker;

    public PlaybackTrackerTests()
    {
        _tracker = new PlaybackTracker(_time);
    }

    private static Guid Item(int number) => new(number, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

    private int NewsAfter(Action act)
    {
        using var listener = _tracker.Listen(TestServices.UserId);
        act();
        var count = 0;
        while (listener.News.TryRead(out _))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void A_playing_video_is_listed_and_its_position_moves_with_the_clock()
    {
        _tracker.Report(Sample.Report(positionMs: 60_000));
        _time.AdvanceMs(2_500);

        var watch = Assert.Single(_tracker.ListFor(TestServices.UserId));

        Assert.True(watch.Playing);
        Assert.Equal(62_500, watch.PositionAt(_time.Now));
        Assert.Equal("Living room", watch.Report.Player);
        Assert.Empty(_tracker.ListFor(TestServices.OtherUserId));
    }

    [Fact]
    public void A_paused_video_stays_where_it_is()
    {
        _tracker.Report(Sample.Report(positionMs: 60_000, paused: true));
        _time.AdvanceMs(30_000);

        Assert.Equal(60_000, _tracker.ListFor(TestServices.UserId)[0].PositionAt(_time.Now));
    }

    [Fact]
    public void Steady_progress_is_no_news()
    {
        _tracker.Report(Sample.Report(positionMs: 0));

        var news = NewsAfter(() =>
        {
            for (var second = 1; second <= 120; second++)
            {
                _time.AdvanceMs(1_000);
                _tracker.Report(Sample.Report(positionMs: (second * 1_000) + (second % 2 == 0 ? 200 : -200)));
            }
        });

        Assert.Equal(0, news);
    }

    [Fact]
    public void A_start_a_pause_a_jump_another_track_and_a_stop_are_news()
    {
        Assert.Equal(1, NewsAfter(() => _tracker.Report(Sample.Report(positionMs: 0))));

        _time.AdvanceMs(1_000);
        Assert.Equal(1, NewsAfter(() => _tracker.Report(Sample.Report(positionMs: 1_000, paused: true))));
        Assert.Equal(1, NewsAfter(() => _tracker.Report(Sample.Report(positionMs: 1_000))));

        _time.AdvanceMs(1_000);
        Assert.Equal(1, NewsAfter(() => _tracker.Report(Sample.Report(positionMs: 300_000))));

        _time.AdvanceMs(1_000);
        Assert.Equal(1, NewsAfter(() => _tracker.Report(Sample.Report(positionMs: 301_000, subtitle: 4))));
        Assert.Equal(1, NewsAfter(() => _tracker.Report(Sample.Report(positionMs: 301_000, subtitle: 4, audio: 3))));

        Assert.Equal(1, NewsAfter(() => _tracker.Stop("session-1", 301_500)));
        Assert.Equal(0, NewsAfter(() => _tracker.Stop("session-1", 301_500)));
    }

    [Fact]
    public void A_slow_drift_becomes_news_once_the_pages_are_too_far_off()
    {
        _tracker.Report(Sample.Report(positionMs: 0));

        // A player at 1.25 times the speed gains a quarter of a second each second.
        var told = Enumerable.Range(1, 12).Select(second => NewsAfter(() =>
        {
            _time.AdvanceMs(1_000);
            _tracker.Report(Sample.Report(positionMs: second * 1_250));
        })).ToList();

        Assert.Equal([0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1], told);
    }

    [Fact]
    public void News_goes_only_to_the_pages_of_the_user_who_watches()
    {
        using var other = _tracker.Listen(TestServices.OtherUserId);

        _tracker.Report(Sample.Report());

        Assert.False(other.News.TryRead(out _));
    }

    [Fact]
    public void A_page_that_has_news_waiting_is_not_told_twice_and_one_that_left_is_not_told_at_all()
    {
        var listener = _tracker.Listen(TestServices.UserId);
        _tracker.Report(Sample.Report(positionMs: 0));
        _tracker.Report(Sample.Report(positionMs: 0, paused: true));

        Assert.True(listener.News.TryRead(out _));
        Assert.False(listener.News.TryRead(out _));
        Assert.Equal(1, _tracker.ListenerCountOf(TestServices.UserId));

        listener.Dispose();
        Assert.Equal(0, _tracker.ListenerCountOf(TestServices.UserId));
    }

    [Fact]
    public void A_stopped_video_stays_listed_for_twelve_hours_at_the_place_it_stopped()
    {
        _tracker.Report(Sample.Report(positionMs: 100_000));
        _time.AdvanceMs(5_000);
        _tracker.Stop("session-1", null);
        _time.Advance(TimeSpan.FromHours(11));

        var watch = Assert.Single(_tracker.ListFor(TestServices.UserId));
        Assert.False(watch.Playing);
        Assert.Equal(105_000, watch.PositionAt(_time.Now));
        Assert.Equal(_time.Now - TimeSpan.FromHours(11), watch.StoppedAt);

        _time.Advance(TimeSpan.FromHours(1.1));
        Assert.Empty(_tracker.ListFor(TestServices.UserId));
    }

    [Fact]
    public void The_next_episode_on_the_same_player_ends_the_one_before_and_both_are_listed()
    {
        _tracker.Report(Sample.Report(itemId: Item(1), positionMs: 1_400_000));
        _time.AdvanceMs(10_000);

        Assert.Equal(1, NewsAfter(() => _tracker.Report(Sample.Report(itemId: Item(2), positionMs: 0))));

        var watches = _tracker.ListFor(TestServices.UserId);
        Assert.Equal([Item(2), Item(1)], watches.Select(watch => watch.Report.ItemId));
        Assert.Equal([true, false], watches.Select(watch => watch.Playing));
        Assert.NotEqual(watches[0].Id, watches[1].Id);
    }

    [Fact]
    public void A_video_is_listed_once_playing_before_stopped_and_the_newest_stop_first()
    {
        // Watched on the TV, then opened again on the phone; another film watched in between.
        _tracker.Report(Sample.Report(session: "tv", itemId: Item(1)));
        _tracker.Stop("tv", 500_000);
        _time.Advance(TimeSpan.FromMinutes(5));
        _tracker.Report(Sample.Report(session: "tv", itemId: Item(2)));
        _tracker.Stop("tv", 20_000);
        _time.Advance(TimeSpan.FromMinutes(5));
        _tracker.Report(Sample.Report(session: "tv", itemId: Item(3)));
        _tracker.Stop("tv", 30_000);
        _tracker.Report(Sample.Report(session: "phone", itemId: Item(1), positionMs: 500_000));

        var watches = _tracker.ListFor(TestServices.UserId);

        Assert.Equal([Item(1), Item(3), Item(2)], watches.Select(watch => watch.Report.ItemId));
        Assert.Equal("phone", watches[0].Report.SessionId);
    }

    [Fact]
    public void Opening_a_stopped_video_again_on_the_same_player_keeps_its_name()
    {
        _tracker.Report(Sample.Report(positionMs: 100));
        var id = _tracker.ListFor(TestServices.UserId)[0].Id;
        _tracker.Stop("session-1", 200);

        _tracker.Report(Sample.Report(positionMs: 200));

        var watch = Assert.Single(_tracker.ListFor(TestServices.UserId));
        Assert.Equal(id, watch.Id);
        Assert.True(watch.Playing);
        Assert.Null(watch.StoppedAt);
    }

    [Fact]
    public void A_player_that_went_silent_counts_as_stopped_when_it_last_spoke()
    {
        _tracker.Report(Sample.Report(positionMs: 100_000));
        var spoke = _time.Now;
        _time.Advance(PlaybackTracker.SilentAfter + TimeSpan.FromSeconds(1));

        var watch = Assert.Single(_tracker.ListFor(TestServices.UserId));

        Assert.False(watch.Playing);
        Assert.Equal(spoke, watch.StoppedAt);
        Assert.Equal(100_000, watch.PositionAt(_time.Now));
    }

    [Fact]
    public void No_more_than_twelve_watches_are_listed()
    {
        for (var number = 1; number <= 20; number++)
        {
            _tracker.Report(Sample.Report(itemId: Item(number)));
            _time.AdvanceMs(1_000);
        }

        var watches = _tracker.ListFor(TestServices.UserId);

        Assert.Equal(PlaybackTracker.MaxPerUser, watches.Count);
        Assert.Equal(Item(20), watches[0].Report.ItemId);
    }
}
