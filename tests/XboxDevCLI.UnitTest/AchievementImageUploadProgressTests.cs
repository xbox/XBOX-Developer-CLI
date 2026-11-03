// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Extensions.Time.Testing;
using XboxDevCLI.Commands;
using Xunit;

namespace XboxDevCLI.UnitTest;

public sealed class AchievementImageUploadProgressTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void RedirectedStderrNeverShowsProgress()
    {
        var time = new FakeTimeProvider();
        using var stderr = new StringWriter();
        using (var progress = new AchievementImageUploadProgress(
            stderr, enabled: false, "achievement.png", 20 * MiB, time, () => 0))
        {
            progress.SetPhase("Uploading");
            progress.ReportBytes(10 * MiB);
            time.Advance(TimeSpan.FromSeconds(1));
            progress.SetPhase("Registering image");
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void InteractiveProgressShowsTransferAndOpaquePhaseThenClearsLine()
    {
        var time = new FakeTimeProvider();
        using var stderr = new StringWriter();
        using (var progress = new AchievementImageUploadProgress(
            stderr, enabled: true, "achievement.png", 20 * MiB, time, () => 0))
        {
            progress.SetPhase("Uploading");
            progress.ReportBytes(10 * MiB);
            time.Advance(TimeSpan.FromMilliseconds(500));
            Assert.Contains("Uploading achievement.png  10.0 / 20.0 MiB  50%  20.0 MiB/s",
                stderr.ToString(), StringComparison.Ordinal);

            progress.SetPhase("Registering image");
            time.Advance(TimeSpan.FromSeconds(2));
            Assert.Contains("Registering image... 2s", stderr.ToString(), StringComparison.Ordinal);
        }

        Assert.EndsWith("\r" + new string(' ', "Registering image... 2s".Length) + "\r",
            stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FastUploadDoesNotShowProgress()
    {
        var time = new FakeTimeProvider();
        using var stderr = new StringWriter();
        using var progress = new AchievementImageUploadProgress(
            stderr, enabled: true, "achievement.png", 1024, time, () => 0);
        progress.SetPhase("Uploading");
        progress.ReportBytes(1024);
        time.Advance(TimeSpan.FromMilliseconds(299));
        progress.Dispose();
        time.Advance(TimeSpan.FromSeconds(1));
        progress.Dispose();

        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void InteractiveProgressNormalizesFileNameBeforeRendering()
    {
        var time = new FakeTimeProvider();
        using var stderr = new StringWriter();
        using (var progress = new AchievementImageUploadProgress(
            stderr, enabled: true, "achievement\r\n.png", 1024, time, () => 0))
        {
            progress.SetPhase("Uploading");
            time.Advance(TimeSpan.FromMilliseconds(300));
        }

        Assert.Contains("Uploading achievement  .png", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain('\n', stderr.ToString());
    }
}