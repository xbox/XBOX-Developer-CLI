// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.CommandLine;

/// <summary>
/// Work a command must finish before the process exits, even after Ctrl-C, such as releasing a
/// lock the command holds on a remote resource.
/// </summary>
/// <remarks>
/// On Ctrl-C, System.CommandLine cancels the command's token and stops waiting for the command
/// after its process termination timeout, two seconds by default. <c>Main</c> then returns and
/// the process exits with the command still running. Work tracked here keeps <c>Main</c> waiting
/// for it, up to <see cref="MaxWait"/>, without making every command that is slow to honor
/// cancellation slower to stop. Tracked work may stop early on the command's cancellation token,
/// but anything that must still happen after Ctrl-C, such as the cancel a failed upload sends,
/// can't depend on that token. Tracked work bounds its own duration and reports its own outcome.
/// </remarks>
internal sealed class ExitCleanup
{
    /// <summary>The longest <c>Main</c> waits for tracked work before letting the process exit.</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    /// <summary>The cleanup <c>Main</c> waits for.</summary>
    internal static ExitCleanup Default { get; } = new();

    private readonly object _gate = new();
    private readonly HashSet<Task> _pending = [];

    /// <summary>Tracks <paramref name="work"/> until it completes, and returns it.</summary>
    public Task<T> Track<T>(Task<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            _pending.Add(work);
        }

        _ = work.ContinueWith(
            completed =>
            {
                lock (_gate)
                {
                    _pending.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return work;
    }

    /// <summary>
    /// Waits for the work tracked so far to complete, or for <paramref name="timeout"/> to pass.
    /// </summary>
    public async Task WaitAsync(TimeSpan timeout)
    {
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _pending];
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(timeout);
        }
        catch (Exception)
        {
            // Tracked work reports its own failures; this only keeps the process alive for it.
        }
    }
}
