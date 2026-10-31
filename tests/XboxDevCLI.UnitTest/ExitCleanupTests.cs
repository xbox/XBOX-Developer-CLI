// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.CommandLine;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// After Ctrl-C, System.CommandLine stops waiting for a command after two seconds, and
/// <c>Main</c> waits on <see cref="ExitCleanup"/> so cleanup the command started still finishes.
/// </summary>
public class ExitCleanupTests
{
    [Fact]
    public async Task WaitAsync_WaitsForTrackedWorkToFinish()
    {
        var cleanup = new ExitCleanup();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = cleanup.Track(release.Task);

        var wait = cleanup.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);

        release.SetResult(1);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitAsync_StopsWaitingAtTheTimeout()
    {
        var cleanup = new ExitCleanup();
        _ = cleanup.Track(new TaskCompletionSource<int>().Task);

        await cleanup.WaitAsync(TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitAsync_DoesNotWaitForWorkThatHasFinishedOrFailed()
    {
        var cleanup = new ExitCleanup();
        _ = cleanup.Track(Task.FromResult(1));
        _ = cleanup.Track(Task.FromException<int>(new InvalidOperationException("reported elsewhere")));

        await cleanup.WaitAsync(Timeout.InfiniteTimeSpan).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
