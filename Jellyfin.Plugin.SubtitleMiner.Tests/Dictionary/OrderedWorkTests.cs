using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;

namespace Jellyfin.Plugin.SubtitleMiner.Tests.Dictionary;

public sealed class OrderedWorkTests
{
    private sealed class Opened(Action closed) : IDisposable
    {
        public void Dispose() => closed();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void Results_are_taken_in_order_however_the_work_ends(int threads)
    {
        var taken = new List<(int Place, string Result)>();
        var caller = Environment.CurrentManagedThreadId;
        var takers = new HashSet<int>();
        var opened = 0;
        var closed = 0;

        OrderedWork.Run(
            200,
            threads,
            _ => 1,
            long.MaxValue,
            () =>
            {
                Interlocked.Increment(ref opened);
                return new Opened(() => Interlocked.Increment(ref closed));
            },
            (_, place) =>
            {
                // The earlier a piece, the longer it takes.
                Thread.SpinWait((200 - place) * 200);
                return "piece " + place;
            },
            (place, result) =>
            {
                takers.Add(Environment.CurrentManagedThreadId);
                taken.Add((place, result));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(0, 200).Select(place => (place, "piece " + place)), taken);
        Assert.Equal([caller], takers);
        Assert.InRange(opened, 1, threads);
        Assert.Equal(opened, closed);
    }

    [Fact]
    public void Nothing_to_do_opens_nothing()
    {
        OrderedWork.Run<Opened, int>(0, 4, _ => 1, 10, () => throw new InvalidOperationException("opened"), (_, _) => 0, (_, _) => { }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Work_under_way_stays_within_the_budget_unless_one_piece_alone_is_over_it()
    {
        // Pieces of 3, with one of 50 among them, and room for 10.
        long Weight(int place) => place == 20 ? 50 : 3;
        var gate = new object();
        var underWay = 0L;
        var most = 0L;
        var mostBesideTheLarge = 0L;
        var started = new HashSet<int>();

        OrderedWork.Run(
            60,
            8,
            Weight,
            10,
            () => new Opened(() => { }),
            (_, place) =>
            {
                lock (gate)
                {
                    started.Add(place);
                    underWay += Weight(place);
                    most = Math.Max(most, place == 20 || started.Contains(20) && underWay > 50 ? 0 : underWay);
                    if (place == 20)
                    {
                        mostBesideTheLarge = underWay;
                    }
                }

                Thread.Sleep(1);
                return place;
            },
            (place, _) =>
            {
                Thread.Sleep(1);
                lock (gate)
                {
                    underWay -= Weight(place);
                }
            },
            TestContext.Current.CancellationToken);

        Assert.InRange(most, 3, 10);
        Assert.Equal(50, mostBesideTheLarge);
        Assert.Equal(0, underWay);
    }

    [Fact]
    public void An_error_in_the_work_ends_it_and_comes_out_as_it_is()
    {
        var taken = new ConcurrentBag<int>();
        var closed = 0;

        var error = Assert.Throws<System.IO.InvalidDataException>(() => OrderedWork.Run(
            100,
            4,
            _ => 1,
            long.MaxValue,
            () => new Opened(() => Interlocked.Increment(ref closed)),
            (_, place) => place == 30 ? throw new System.IO.InvalidDataException("piece 30 is damaged") : place,
            (place, _) => taken.Add(place),
            TestContext.Current.CancellationToken));

        Assert.Equal("piece 30 is damaged", error.Message);
        Assert.DoesNotContain(30, taken);
        Assert.All(taken, place => Assert.InRange(place, 0, 29));
        Assert.InRange(closed, 1, 4);
    }

    [Fact]
    public void An_error_in_taking_a_result_ends_the_work()
    {
        var worked = 0;

        var error = Assert.Throws<InvalidOperationException>(() => OrderedWork.Run(
            1000,
            4,
            _ => 1,
            long.MaxValue,
            () => new Opened(() => { }),
            (_, place) =>
            {
                Interlocked.Increment(ref worked);
                return place;
            },
            (place, _) =>
            {
                if (place == 5)
                {
                    throw new InvalidOperationException("the file is full");
                }
            },
            TestContext.Current.CancellationToken));

        Assert.Equal("the file is full", error.Message);
        Assert.InRange(worked, 6, 20);
    }

    [Fact]
    public void An_error_in_opening_ends_the_work()
    {
        var error = Assert.Throws<System.IO.IOException>(() => OrderedWork.Run<Opened, int>(
            10,
            2,
            _ => 1,
            long.MaxValue,
            () => throw new System.IO.IOException("the zip is gone"),
            (_, place) => place,
            (_, _) => { },
            TestContext.Current.CancellationToken));

        Assert.Equal("the zip is gone", error.Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_stop_ends_the_work_even_while_a_piece_takes_long()
    {
        using var stop = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var taken = new List<int>();

        var work = System.Threading.Tasks.Task.Run(
            () => Assert.ThrowsAny<OperationCanceledException>(() => OrderedWork.Run(
                50,
                2,
                _ => 1,
                long.MaxValue,
                () => new Opened(() => { }),
                (_, place) =>
                {
                    if (place == 3)
                    {
                        stop.Cancel();
                        release.Wait(TimeSpan.FromSeconds(30));
                    }

                    return place;
                },
                (place, _) => taken.Add(place),
                stop.Token)),
            TestContext.Current.CancellationToken);

        // The work is over once the piece that is under way ends, and no later piece is taken.
        var first = await System.Threading.Tasks.Task.WhenAny(work, System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken));
        Assert.NotSame(work, first);
        release.Set();
        await work.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.All(taken, place => Assert.InRange(place, 0, 2));
    }
}
