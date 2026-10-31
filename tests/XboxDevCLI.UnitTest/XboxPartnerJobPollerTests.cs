// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class XboxPartnerJobPollerTests
{
    [Fact]
    public async Task PollAsync_ReturnsInitialResponseWhenAlreadyCompleted()
    {
        var initial = CreateJob(status: "completed", result: "succeeded");
        var statusCalls = 0;
        var delayCalls = 0;

        var result = await XboxPartnerJobPoller.PollAsync(
            initial,
            _ =>
            {
                statusCalls++;
                return Task.FromResult(initial);
            },
            (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Same(initial, result);
        Assert.Equal(0, statusCalls);
        Assert.Equal(0, delayCalls);
    }

    [Fact]
    public async Task PollAsync_UsesDefaultIntervalThenRetryAfter()
    {
        var initial = CreateJob();
        var responses = new Queue<XboxPartnerGameCreationJob>(
        [
            CreateJob(retryAfter: TimeSpan.FromSeconds(7)),
            CreateJob(status: "completed", result: "succeeded"),
        ]);
        var clock = new FakeClock();

        var result = await XboxPartnerJobPoller.PollAsync(
            initial,
            _ => Task.FromResult(responses.Dequeue()),
            clock.DelayAsync,
            CancellationToken.None,
            () => clock.UtcNow);

        Assert.NotNull(result);
        Assert.True(result.Completed);
        Assert.True(result.Succeeded);
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7)],
            clock.Delays);
        Assert.Empty(responses);
    }

    [Fact]
    public async Task PollAsync_BoundsRetryAfterAtDefaultDeadline()
    {
        var initial = CreateJob();
        var statusCalls = 0;
        var clock = new FakeClock();

        var result = await XboxPartnerJobPoller.PollAsync(
            initial,
            _ =>
            {
                statusCalls++;
                return Task.FromResult(
                    CreateJob(retryAfter: TimeSpan.FromSeconds(40)));
            },
            clock.DelayAsync,
            CancellationToken.None,
            () => clock.UtcNow);

        Assert.Null(result);
        Assert.Equal(TimeSpan.FromSeconds(120), XboxPartnerJobPoller.DefaultTimeout);
        Assert.Equal(3, statusCalls);
        Assert.Equal(
            [
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(40),
                TimeSpan.FromSeconds(40),
                TimeSpan.FromSeconds(35),
            ],
            clock.Delays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(500)]
    public async Task PollAsync_WaitsAtLeastTheMinimumDelayForAShortRetryAfter(int retryAfterMilliseconds)
    {
        var initial = CreateJob(retryAfter: TimeSpan.FromMilliseconds(retryAfterMilliseconds));
        var running = CreateJob(retryAfter: TimeSpan.Zero);
        var completed = CreateJob(status: "completed", result: "succeeded");
        var responses = new Queue<XboxPartnerGameCreationJob>([running, completed]);
        var clock = new FakeClock();

        var result = await XboxPartnerJobPoller.PollAsync(
            initial,
            _ => Task.FromResult(responses.Dequeue()),
            clock.DelayAsync,
            CancellationToken.None,
            () => clock.UtcNow);

        Assert.Same(completed, result);
        Assert.Equal(TimeSpan.FromSeconds(1), XboxPartnerJobPoller.MinimumDelay);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], clock.Delays);
    }

    [Fact]
    public async Task PollAsync_ReturnsNullWhenWallClockTimeoutCancelsDelay()
    {
        var result = await XboxPartnerJobPoller.PollAsync(
            CreateJob(),
            _ => Task.FromResult(CreateJob()),
            Task.Delay,
            CancellationToken.None,
            () => DateTimeOffset.UnixEpoch,
            interval: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromMilliseconds(10));

        Assert.Null(result);
    }

    [Fact]
    public async Task PollAsync_DoesNotSwallowCallerCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => XboxPartnerJobPoller.PollAsync(
                CreateJob(),
                _ => Task.FromResult(CreateJob()),
                Task.Delay,
                cancellationSource.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PollAsync_RejectsNonPositiveDurations(bool invalidInterval)
    {
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => XboxPartnerJobPoller.PollAsync(
                CreateJob(),
                _ => Task.FromResult(CreateJob()),
                Task.Delay,
                CancellationToken.None,
                interval: invalidInterval
                    ? TimeSpan.Zero
                    : TimeSpan.FromSeconds(1),
                timeout: invalidInterval
                    ? TimeSpan.FromSeconds(1)
                    : TimeSpan.Zero));

        Assert.Equal(
            invalidInterval ? "interval" : "timeout",
            exception.ParamName);
    }

    private static XboxPartnerGameCreationJob CreateJob(
        string? status = "running",
        string? result = "pending",
        TimeSpan? retryAfter = null) =>
        new()
        {
            RawResponse = "{}",
            JobId = "job-1",
            Status = status,
            Result = result,
            RetryAfter = retryAfter,
        };

    private sealed class FakeClock
    {
        internal DateTimeOffset UtcNow { get; private set; } =
            DateTimeOffset.UnixEpoch;

        internal List<TimeSpan> Delays { get; } = [];

        internal Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            UtcNow = UtcNow.Add(delay);
            return Task.CompletedTask;
        }
    }
}
