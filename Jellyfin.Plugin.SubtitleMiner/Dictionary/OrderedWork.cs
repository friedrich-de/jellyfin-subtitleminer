using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubtitleMiner.Dictionary;

/// <summary>
/// Does a list of pieces of work on several threads and hands each result to the caller's thread in the list's
/// order. That is the shape of an import: the banks of a dictionary are read side by side, and one writer puts them
/// into the file as if they had been read one after another.
/// </summary>
internal static class OrderedWork
{
    /// <summary>Does the work.</summary>
    /// <typeparam name="TState">What each thread keeps for itself while it works, such as its own view of the zip.</typeparam>
    /// <typeparam name="TResult">What a piece of work gives.</typeparam>
    /// <param name="count">How many pieces of work there are.</param>
    /// <param name="threads">How many threads work.</param>
    /// <param name="weight">How much room a piece of work takes, by its place in the list.</param>
    /// <param name="budget">
    /// How much room the work that is started but not taken yet may take together. More is only taken when nothing
    /// else is under way, so one piece that is larger than this still gets done, alone.
    /// </param>
    /// <param name="open">Makes what a thread keeps for itself.</param>
    /// <param name="work">Does a piece of work, by its place in the list.</param>
    /// <param name="take">Takes a result, on the calling thread, in the list's order.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    public static void Run<TState, TResult>(
        int count,
        int threads,
        Func<int, long> weight,
        long budget,
        Func<TState> open,
        Func<TState, int, TResult> work,
        Action<int, TResult> take,
        CancellationToken cancellationToken)
        where TState : IDisposable
    {
        if (count == 0)
        {
            return;
        }

        threads = Math.Clamp(threads, 1, count);

        // A little more than one piece for each thread may wait for the writer, so that no thread idles while the
        // writer is busy with a large piece.
        var window = threads + 2;
        var gate = new object();
        var results = new TResult?[count];
        var done = new bool[count];
        var next = 0;
        var taken = 0;
        var underWay = 0L;
        ExceptionDispatchInfo? failure = null;
        var stopped = false;

        void Fail(Exception error)
        {
            lock (gate)
            {
                failure ??= ExceptionDispatchInfo.Capture(error);
                Monitor.PulseAll(gate);
            }
        }

        void Worker()
        {
            TState? state = default;
            try
            {
                while (true)
                {
                    int index;
                    lock (gate)
                    {
                        while (!stopped && failure is null && next < count
                            && (next - taken >= window || (underWay > 0 && underWay + weight(next) > budget)))
                        {
                            Monitor.Wait(gate);
                        }

                        if (stopped || failure is not null || next >= count)
                        {
                            return;
                        }

                        index = next++;
                        underWay += weight(index);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    state ??= open();
                    var result = work(state, index);
                    lock (gate)
                    {
                        results[index] = result;
                        done[index] = true;
                        Monitor.PulseAll(gate);
                    }
                }
            }
            catch (Exception error)
            {
                Fail(error);
            }
            finally
            {
                state?.Dispose();
            }
        }

        var workers = new Task[threads];
        for (var i = 0; i < threads; i++)
        {
            workers[i] = Task.Factory.StartNew(Worker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        try
        {
            for (var index = 0; index < count; index++)
            {
                TResult result;
                lock (gate)
                {
                    while (failure is null && !done[index])
                    {
                        // A stop is noticed here too, in case every thread waits on a long piece.
                        Monitor.Wait(gate, 250);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            failure ??= ExceptionDispatchInfo.Capture(new OperationCanceledException(cancellationToken));
                        }
                    }

                    if (failure is not null)
                    {
                        break;
                    }

                    result = results[index]!;
                    results[index] = default;
                }

                take(index, result);
                lock (gate)
                {
                    taken = index + 1;
                    underWay -= weight(index);
                    Monitor.PulseAll(gate);
                }
            }
        }
        catch (Exception error)
        {
            Fail(error);
        }
        finally
        {
            lock (gate)
            {
                stopped = true;
                Monitor.PulseAll(gate);
            }

            Task.WaitAll(workers, CancellationToken.None);
        }

        failure?.Throw();
    }
}
