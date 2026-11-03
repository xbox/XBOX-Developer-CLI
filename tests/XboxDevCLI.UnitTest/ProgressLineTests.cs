// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Time.Testing;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

public sealed class ProgressLineTests
{
    [Fact]
    public void RedrawsInPlaceThenErasesOnce()
    {
        using var stderr = new StringWriter();
        string text = "Reading games... 12s";
        var line = new ProgressLine(stderr, enabled: false, () => text, () => 0);

        line.Draw();
        text = "Short";
        line.Draw();
        line.Dispose();
        line.Dispose();
        line.Draw();

        Assert.Equal(
            "\rReading games... 12s\rShort" + new string(' ', 15) + "\r" + new string(' ', 5) + "\r",
            stderr.ToString());
    }

    [Fact]
    public void ALineWiderThanTheTerminalIsCutBeforeItsLastColumn()
    {
        using var stderr = new StringWriter();
        using (var line = new ProgressLine(
            stderr, enabled: false, () => "Reading configurations... 1646 found, 35 pages, 115s", () => 40))
        {
            line.Draw();
        }

        Assert.Equal(
            "\rReading configurations... 1646 found...\r" + new string(' ', 39) + "\r",
            stderr.ToString());
    }

    [Fact]
    public void WhenEnabledItWaitsBeforeDrawingThenRedraws()
    {
        var time = new FakeTimeProvider();
        using var stderr = new StringWriter();
        int draws = 0;
        using var line = new ProgressLine(
            stderr, enabled: true, () => $"Reading games... {++draws}", () => 0, time);

        time.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Equal(0, draws);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, draws);

        time.Advance(TimeSpan.FromMilliseconds(199));
        Assert.Equal(1, draws);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("\rReading games... 1\rReading games... 2", stderr.ToString());
    }

    [Fact]
    public async Task ADrawSkipsItsRedrawWhileAnotherIsStillWriting()
    {
        using var stderr = new StringWriter();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unblock = new ManualResetEventSlim();
        using var line = new ProgressLine(stderr, enabled: false, () =>
        {
            writing.TrySetResult();
            unblock.Wait();
            return "Reading games... 1s";
        }, () => 0);

        Task stuck = Task.Run(line.Draw);
        try
        {
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Returns at once, rather than waiting for the stuck draw.
            await Task.Run(line.Draw).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            unblock.Set();
        }

        await stuck.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("\rReading games... 1s", stderr.ToString());
    }
}
