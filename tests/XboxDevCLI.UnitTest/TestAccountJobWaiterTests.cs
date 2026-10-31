// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class TestAccountJobWaiterTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WaitAsync_ReturnsAtOnceWhenTheJobIsNotRunning()
    {
        var clock = new FakeClock();
        TestAccountJobResult initial = Result("succeeded");

        TestAccountJobResult final = await TestAccountJobWaiter.WaitAsync(
            initial, _ => throw new InvalidOperationException("no read"), TimeSpan.FromMinutes(5),
            clock.DelayAsync, clock.Now, null, CancellationToken.None);

        Assert.Same(initial, final);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task WaitAsync_HonorsClampedRetryAfterAndReportsProgress()
    {
        var clock = new FakeClock();
        var reads = new Queue<TestAccountJobResult>(
        [
            Result("running", retryAfter: TimeSpan.FromHours(1)),
            Result("running", retryAfter: null),
            Result("succeeded"),
        ]);
        var progress = new List<TestAccountJobResult>();

        TestAccountJobResult final = await TestAccountJobWaiter.WaitAsync(
            Result("running", retryAfter: TimeSpan.FromMilliseconds(10)),
            _ => Task.FromResult(reads.Dequeue()),
            TimeSpan.FromHours(1),
            clock.DelayAsync,
            clock.Now,
            progress.Add,
            CancellationToken.None);

        Assert.True(final.Job.IsSucceeded);
        Assert.Equal(
            [TestAccountJobWaiter.MinimumInterval, TestAccountJobWaiter.MaximumInterval, TestAccountJobWaiter.DefaultInterval],
            clock.Delays);
        Assert.Equal(3, progress.Count);
    }

    [Fact]
    public async Task WaitAsync_ReturnsTheLastRunningReadAtTheDeadline()
    {
        var clock = new FakeClock();
        int reads = 0;

        TestAccountJobResult final = await TestAccountJobWaiter.WaitAsync(
            Result("running"),
            _ => Task.FromResult(Result("running", succeeded: ++reads)),
            TimeSpan.FromSeconds(25),
            clock.DelayAsync,
            clock.Now,
            null,
            CancellationToken.None);

        // No read is started once the last sleep reaches the deadline.
        Assert.True(final.Job.IsRunning);
        Assert.Equal(2, reads);
        Assert.Equal(2, final.Job.Succeeded);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)], clock.Delays);
    }

    [Fact]
    public async Task WaitAsync_StopsAReadThatOutlastsTheDeadline()
    {
        var clock = new FakeClock();
        TestAccountJobResult initial = Result("running");

        TestAccountJobResult final = await TestAccountJobWaiter.WaitAsync(
            initial,
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Result("succeeded");
            },
            TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(50),
            clock.DelayAsync,
            clock.Now,
            null,
            CancellationToken.None);

        Assert.Same(initial, final);
        Assert.Equal([TimeSpan.FromSeconds(10)], clock.Delays);
    }

    [Fact]
    public async Task WaitAsync_AbandonsAReadThatIgnoresTheDeadline()
    {
        var clock = new FakeClock();
        TestAccountJobResult initial = Result("running");
        var never = new TaskCompletionSource<TestAccountJobResult>();

        TestAccountJobResult final = await TestAccountJobWaiter.WaitAsync(
            initial,
            _ => never.Task,
            TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(50),
            clock.DelayAsync,
            clock.Now,
            null,
            CancellationToken.None);

        Assert.Same(initial, final);
    }

    [Fact]
    public async Task WaitAsync_CallerCancelDuringAReadStillThrows()
    {
        var clock = new FakeClock();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestAccountJobWaiter.WaitAsync(
            Result("running"),
            async token =>
            {
                await cancellation.CancelAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Result("succeeded");
            },
            TimeSpan.FromHours(1),
            clock.DelayAsync,
            clock.Now,
            null,
            cancellation.Token));
    }

    [Fact]
    public async Task WaitAsync_WaitsAsAThrottledReadsRetryAfterAsks()
    {
        var clock = new FakeClock();
        var outcomes = new Queue<Func<TestAccountJobResult>>(
        [
            () => throw Api(HttpStatusCode.TooManyRequests, null, TimeSpan.FromSeconds(60)),
            () => throw Api(HttpStatusCode.ServiceUnavailable, null, TimeSpan.FromHours(1)),
            () => Result("succeeded"),
        ]);

        TestAccountJobResult final = await TestAccountJobWaiter.WaitAsync(
            Result("running"),
            _ => Task.FromResult(outcomes.Dequeue()()),
            TimeSpan.FromHours(1),
            clock.DelayAsync,
            clock.Now,
            null,
            CancellationToken.None);

        Assert.True(final.Job.IsSucceeded);
        Assert.Equal(
            [TestAccountJobWaiter.DefaultInterval, TimeSpan.FromSeconds(60), TestAccountJobWaiter.MaximumInterval],
            clock.Delays);
    }

    [Fact]
    public async Task WaitAsync_ToleratesFewerThanThreeConsecutiveTransientFailures()
    {
        var clock = new FakeClock();
        var outcomes = new Queue<Func<TestAccountJobResult>>(
        [
            () => throw new HttpRequestException("reset"),
            () => throw Api(HttpStatusCode.GatewayTimeout, null),
            () => Result("running"),
            () => throw Api(HttpStatusCode.TooManyRequests, null),
            () => throw new IOException("closed"),
            () => Result("failed"),
        ]);

        TestAccountJobResult final = await TestAccountJobWaiter.WaitAsync(
            Result("running"),
            _ => Task.FromResult(outcomes.Dequeue()()),
            TimeSpan.FromHours(1),
            clock.DelayAsync,
            clock.Now,
            null,
            CancellationToken.None);

        Assert.True(final.Job.IsFailed);
        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task WaitAsync_ThrowsOnTheThirdConsecutiveTransientFailure()
    {
        var clock = new FakeClock();
        int reads = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => TestAccountJobWaiter.WaitAsync(
            Result("running"),
            _ =>
            {
                reads++;
                throw new HttpRequestException("reset");
            },
            TimeSpan.FromHours(1),
            clock.DelayAsync,
            clock.Now,
            null,
            CancellationToken.None));

        Assert.Equal(TestAccountJobWaiter.MaxConsecutiveReadFailures, reads);
    }

    [Fact]
    public async Task WaitAsync_DoesNotRetryAPermanentFailure()
    {
        var clock = new FakeClock();

        var exception = await Assert.ThrowsAsync<XboxPartnerApiException>(() => TestAccountJobWaiter.WaitAsync(
            Result("running"),
            _ => throw Api(HttpStatusCode.Forbidden, """{"code":"test_account_access_denied"}"""),
            TimeSpan.FromHours(1),
            clock.DelayAsync,
            clock.Now,
            null,
            CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task WaitAsync_StopsWhenTheCallerCancels()
    {
        var clock = new FakeClock();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestAccountJobWaiter.WaitAsync(
            Result("running"),
            _ => Task.FromResult(Result("running")),
            TimeSpan.FromHours(1),
            clock.DelayAsync,
            clock.Now,
            null,
            cancellation.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, null, true)]
    [InlineData(HttpStatusCode.BadGateway, null, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"code":"downstream_throttled"}""", true)]
    [InlineData(HttpStatusCode.GatewayTimeout, """{"code":"downstream_gateway_timeout"}""", true)]
    [InlineData(HttpStatusCode.BadGateway, """{"code":"downstream_service_failed"}""", true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"code":"feature_disabled"}""", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"code":"service_configuration_invalid"}""", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, true)]
    [InlineData(HttpStatusCode.BadGateway, """{"code":"downstream_request_rejected"}""", true)]
    [InlineData(HttpStatusCode.BadGateway, """{"code":"downstream_authentication_failed"}""", true)]
    [InlineData(HttpStatusCode.BadGateway, """{"code":"downstream_connection_failed"}""", true)]
    [InlineData(HttpStatusCode.BadGateway, """{"code":"downstream_response_ended"}""", true)]
    [InlineData(HttpStatusCode.BadGateway, """{"code":"downstream_transport_failed"}""", true)]
    [InlineData(HttpStatusCode.BadGateway, """{"code":"downstream_name_resolution_failed"}""", true)]
    [InlineData(HttpStatusCode.GatewayTimeout, """{"code":"downstream_timeout"}""", true)]
    [InlineData(HttpStatusCode.GatewayTimeout, null, true)]
    [InlineData(HttpStatusCode.InternalServerError, null, false)]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"test_account_access_denied"}""", false)]
    [InlineData(HttpStatusCode.NotFound, null, false)]
    public void IsTransient_ClassifiesGatewayFailuresByCode(HttpStatusCode statusCode, string? body, bool expected)
    {
        Assert.Equal(expected, TestAccountJobWaiter.IsTransient(Api(statusCode, body), CancellationToken.None));
    }

    [Fact]
    public void IsTransient_OnlyTreatsTimeoutsTheCallerDidNotCauseAsTransient()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.True(TestAccountJobWaiter.IsTransient(new TaskCanceledException(), CancellationToken.None));
        Assert.False(TestAccountJobWaiter.IsTransient(new OperationCanceledException(), cancellation.Token));
        Assert.False(TestAccountJobWaiter.IsTransient(new InvalidOperationException(), CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, false)]
    [InlineData(HttpRequestError.SecureConnectionError, false)]
    [InlineData(HttpRequestError.ConnectionError, true)]
    [InlineData(HttpRequestError.ResponseEnded, true)]
    [InlineData(HttpRequestError.Unknown, true)]
    public void IsTransient_DoesNotRetryDnsOrTlsFailures(HttpRequestError error, bool expected)
    {
        var exception = new HttpRequestException(error, "network failure");

        Assert.Equal(expected, TestAccountJobWaiter.IsTransient(exception, CancellationToken.None));
    }

    internal static TestAccountJobResult Result(string state, TimeSpan? retryAfter = null, int succeeded = 0) =>
        new(
            $$"""{"batchId":42,"jobId":"job-1","state":"{{state}}"}""",
            new TestAccountJob(42, "job-1", state, 5, succeeded, 0, null, null, null),
            retryAfter,
            "correlation");

    private static XboxPartnerApiException Api(HttpStatusCode statusCode, string? body, TimeSpan? retryAfter = null) =>
        new(statusCode, null, body ?? string.Empty, new Uri("http://localhost/job"), retryAfter: retryAfter);

    private sealed class FakeClock
    {
        private DateTimeOffset _now = Start;

        public List<TimeSpan> Delays { get; } = [];

        public DateTimeOffset Now() => _now;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            _now += delay;
            return Task.CompletedTask;
        }
    }
}

public class TestAccountSecretReaderTests
{
    [Fact]
    public void Read_FileTrimsOneTrailingNewline()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, " secret value \r\n\r\n");

            Assert.Equal(" secret value \r\n", TestAccountSecretReader.Read(path, "--password-file", "Password", Dependencies()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_MissingFileExplainsWhichOption()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.txt");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TestAccountSecretReader.Read(path, "--password-file", "Password", Dependencies()));

        Assert.StartsWith("Could not read --password-file", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_DashReadsStandardInput()
    {
        var dependencies = Dependencies(stdin: "from stdin\n");

        Assert.Equal("from stdin", TestAccountSecretReader.Read("-", "--security-answer-file", "Security answer", dependencies));
    }

    [Fact]
    public void Read_PromptsTwiceInATerminal()
    {
        var prompts = new List<string>();
        var dependencies = Dependencies(interactive: true, masked: prompt =>
        {
            prompts.Add(prompt);
            return "typed";
        });

        Assert.Equal("typed", TestAccountSecretReader.Read(null, "--password-file", "Password", dependencies));
        Assert.Equal(["Password: ", "Confirm password: "], prompts);
    }

    [Fact]
    public void Read_MismatchedPromptEntriesFail()
    {
        var answers = new Queue<string>(["one", "two"]);
        var dependencies = Dependencies(interactive: true, masked: _ => answers.Dequeue());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TestAccountSecretReader.Read(null, "--password-file", "Password", dependencies));

        Assert.Equal("The password entries did not match.", exception.Message);
        Assert.DoesNotContain("one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_WithoutATerminalPointsToTheFileOption()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TestAccountSecretReader.Read(null, "--security-answer-file", "Security answer", Dependencies()));

        Assert.Contains("--security-answer-file -", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    public void Read_RejectsAnEmptyValue(string stdin)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TestAccountSecretReader.Read("-", "--password-file", "Password", Dependencies(stdin: stdin)));

        Assert.Equal("The password must not be empty.", exception.Message);
    }

    [Fact]
    public void Read_EmptyPromptEntryIsRejectedWithoutAskingAgain()
    {
        int prompts = 0;
        var dependencies = Dependencies(interactive: true, masked: _ =>
        {
            prompts++;
            return string.Empty;
        });

        Assert.Throws<InvalidOperationException>(() =>
            TestAccountSecretReader.Read(null, "--password-file", "Password", dependencies));
        Assert.Equal(1, prompts);
    }

    internal static TestAccountCommandDependencies Dependencies(
        bool interactive = false,
        Func<string, string?>? masked = null,
        string stdin = "",
        Func<string?>? confirmation = null)
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        return new(
            (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                now += delay;
                return Task.CompletedTask;
            },
            () => now,
            () => interactive,
            masked ?? (_ => throw new InvalidOperationException("Unexpected prompt.")),
            confirmation ?? (() => throw new InvalidOperationException("Unexpected confirmation.")),
            () => new StringReader(stdin));
    }
}
