// Copyright (C) Microsoft Corporation. All rights reserved.

using XboxDevCLI.Commands;
using Xunit;

namespace XboxDevCLI.UnitTest;

public sealed class AchievementImageUploadProgressTests
{
    [Fact]
    public async Task RedirectedStderrNeverShowsProgress()
    {
        using var stderr = new StringWriter();
        using (var progress = new AchievementImageUploadProgress(
            stderr, enabled: false, "achievement.png", 20 * 1024 * 1024))
        {
            progress.SetPhase("Uploading");
            progress.ReportBytes(10 * 1024 * 1024);
            await Task.Delay(350);
            progress.SetPhase("Registering image");
        }

        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task InteractiveProgressShowsTransferAndOpaquePhaseThenClearsLine()
    {
        using var stderr = new StringWriter();
        using (var progress = new AchievementImageUploadProgress(
            stderr, enabled: true, "achievement.png", 20 * 1024 * 1024))
        {
            progress.SetPhase("Uploading");
            progress.ReportBytes(10 * 1024 * 1024);
            await Task.Delay(750);
            Assert.Contains("Uploading achievement.png  10.0 / 20.0 MiB  50%  ",
                stderr.ToString(), StringComparison.Ordinal);

            progress.SetPhase("Registering image");
            await Task.Delay(450);
            Assert.Contains("Registering image...", stderr.ToString(), StringComparison.Ordinal);
        }

        string rendered = stderr.ToString();
        Assert.EndsWith("\r" + new string(' ', "Registering image... 0s".Length) + "\r",
            rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FastUploadDoesNotShowProgress()
    {
        using var stderr = new StringWriter();
        using var progress = new AchievementImageUploadProgress(
            stderr, enabled: true, "achievement.png", 1024);
        progress.SetPhase("Uploading");
        progress.ReportBytes(1024);
        progress.Dispose();
        progress.Dispose();

        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task InteractiveProgressNormalizesFileNameBeforeRendering()
    {
        using var stderr = new StringWriter();
        using (var progress = new AchievementImageUploadProgress(
            stderr, enabled: true, "achievement\r\n.png", 1024))
        {
            progress.SetPhase("Uploading");
            await Task.Delay(750);
        }

        Assert.Contains("Uploading achievement  .png", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain('\n', stderr.ToString());
    }
}
