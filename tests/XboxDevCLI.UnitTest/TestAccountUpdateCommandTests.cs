// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class TestAccountUpdateCommandTests : IDisposable
{
    public TestAccountUpdateCommandTests()
    {
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies();
    }

    public void Dispose()
    {
        TestAccountCommandFactory.Dependencies = TestAccountCommandDependencies.Default;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Update_NoWaitSendsOnePatchWithOnlyTheRequestedFields()
    {
        var handler = new XboxPartnerHttpMessageHandler(Update(retryAfterSeconds: 30));

        Invocation result = await InvokeAsync(
            UpdateArgs("--confirm", "--no-wait", "--tag", " nightly ", "--tag", "regression"), handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        CapturedHttpRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/xboxpartner/v1/testaccounts/batches/42", request.RequestUri!.AbsolutePath);
        Assert.Equal("?sellerId=1234567", request.RequestUri!.Query);
        Assert.Equal("application/json", request.ContentType);
        using JsonDocument body = JsonDocument.Parse(request.Body!);
        Assert.Equal(["keywords"], body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["nightly", "regression"],
            body.RootElement.GetProperty("keywords").EnumerateArray().Select(value => value.GetString()));
        using JsonDocument output = JsonDocument.Parse(result.Stdout);
        Assert.Equal("updating", output.RootElement.GetProperty("state").GetString());
        Assert.Equal("upd-1", output.RootElement.GetProperty("jobId").GetString());
        Assert.Contains("update accepted: batch 42, job upd-1", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("xbox test-account batch get 42 --seller-id 1234567", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_SendsSandboxAndExpiryChangesWithAllowNotMine()
    {
        var handler = new XboxPartnerHttpMessageHandler(Update());

        Invocation result = await InvokeAsync(
            UpdateArgs(
                "-y",
                "--no-wait",
                "--allow-not-mine",
                "--add-sandbox-id",
                "ABCD.1",
                "--remove-sandbox-id",
                "ABCD.2",
                "--access-expiration",
                "2030-01-15"),
            handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        CapturedHttpRequest request = Assert.Single(handler.Requests);
        Assert.Equal("?sellerId=1234567&allowNotMine=true", request.RequestUri!.Query);
        using JsonDocument body = JsonDocument.Parse(request.Body!);
        Assert.Equal("ABCD.1", body.RootElement.GetProperty("addSandboxes")[0].GetString());
        Assert.Equal("ABCD.2", body.RootElement.GetProperty("removeSandboxes")[0].GetString());
        Assert.StartsWith("2030-01-15", body.RootElement.GetProperty("accessExpirationDate").GetString(), StringComparison.Ordinal);
        Assert.False(body.RootElement.TryGetProperty("keywords", out _));
    }

    [Fact]
    public async Task Update_ClearTagsReplacesWithAnEmptyKeywordList()
    {
        var handler = new XboxPartnerHttpMessageHandler(Update(keywords: []));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--no-wait", "--clear-tags"), handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal("""{"keywords":[],"replaceKeywords":true}""", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task Update_ReplaceTagsSendsReplaceKeywords()
    {
        var handler = new XboxPartnerHttpMessageHandler(Update());

        Invocation result = await InvokeAsync(
            UpdateArgs("--confirm", "--no-wait", "--tag", "nightly", "--replace-tags"), handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal("""{"keywords":["nightly"],"replaceKeywords":true}""", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task Update_AddedTagsLandEvenWhenTheBatchKeepsOtherTags()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Update(keywords: ["old", "nightly", "CreatedWithXboxDevCLI"]),
            Batch(jobId: null, keywords: ["old", "Nightly", "other", "CreatedWithXboxDevCLI"]),
            Batch(jobId: null, keywords: ["old", "Nightly", "other", "CreatedWithXboxDevCLI"]));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly"), handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        using JsonDocument output = JsonDocument.Parse(result.Stdout);
        Assert.Equal(TestAccountCommandFactory.UpdatedState, output.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Update_ReplacedTagsMustMatchExactly()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Update(),
            Batch(jobId: null, keywords: ["old", "nightly", "CreatedWithXboxDevCLI"]),
            Batch(jobId: null, keywords: ["old", "nightly", "CreatedWithXboxDevCLI"]));

        Invocation result = await InvokeAsync(
            UpdateArgs("--confirm", "--tag", "nightly", "--replace-tags"), handler);

        Assert.Equal(TestAccountCommandFactory.JobFailedExitCode, result.ExitCode);
        Assert.Contains("does not show the new keywords", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_ReplacedTagsCompareWithCase()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Update(keywords: ["Smoke", "CreatedWithXboxDevCLI"]),
            Batch(jobId: null, keywords: ["smoke", "CreatedWithXboxDevCLI"]),
            Batch(jobId: null, keywords: ["smoke", "CreatedWithXboxDevCLI"]));

        Invocation result = await InvokeAsync(
            UpdateArgs("--confirm", "--tag", "Smoke", "--replace-tags"), handler);

        Assert.Equal(TestAccountCommandFactory.JobFailedExitCode, result.ExitCode);
        Assert.Contains("does not show the new keywords", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_TrimsTrailingWhitespaceFromTags()
    {
        var handler = new XboxPartnerHttpMessageHandler(Update());

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--no-wait", "--tag", "nightly\n"), handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal("""{"keywords":["nightly"]}""", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task Update_UnchangedAnswerExitsZeroWithoutWaiting()
    {
        var handler = new XboxPartnerHttpMessageHandler(Update(state: "unchanged", jobId: null, status: HttpStatusCode.OK));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly"), handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Single(handler.Requests);
        using JsonDocument output = JsonDocument.Parse(result.Stdout);
        Assert.Equal("unchanged", output.RootElement.GetProperty("state").GetString());
        Assert.Contains("already has these values. Nothing was changed.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_WaitsUntilTheJobClearsAndTheBatchShowsTheChange()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Update(),
            Batch(jobId: "upd-1", keywords: ["old"]),
            Batch(jobId: null, keywords: ["nightly", "CreatedWithXboxDevCLI"]),
            Batch(jobId: null, keywords: ["NIGHTLY", "CreatedWithXboxDevCLI"]));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly"), handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Patch);
        using JsonDocument output = JsonDocument.Parse(result.Stdout);
        Assert.Equal(TestAccountCommandFactory.UpdatedState, output.RootElement.GetProperty("state").GetString());
        Assert.Contains("Waiting up to 5 minutes for batch 42 to be updated", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Batch 42 updated.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_JobClearedWithoutTheChangeExitsTwo()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Update(),
            Batch(jobId: null, keywords: ["old"]),
            Batch(jobId: null, keywords: ["old"]));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly"), handler);

        Assert.Equal(TestAccountCommandFactory.JobFailedExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("does not show the new keywords", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_TimeoutWithTheJobStillSetExitsFour()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Update(),
            Batch(jobId: "upd-1"),
            Batch(jobId: "upd-1"),
            Batch(jobId: "upd-1"),
            Batch(jobId: "upd-1"));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly", "--timeout", "30s"), handler);

        Assert.Equal(TestAccountCommandFactory.JobNotFinishedExitCode, result.ExitCode);
        using JsonDocument output = JsonDocument.Parse(result.Stdout);
        Assert.Equal("updating", output.RootElement.GetProperty("state").GetString());
        Assert.Contains("Its jobId is upd-1", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("may have stalled", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_ReplaceWhoseCaseChangeNeverLandedExitsTwo()
    {
        // The answer and the batch agree on the old casing, so only the request shows the change is missing.
        var handler = new XboxPartnerHttpMessageHandler(
            Update(keywords: ["smoke", "CreatedWithXboxDevCLI"]),
            Batch(jobId: null, keywords: ["smoke", "CreatedWithXboxDevCLI"]),
            Batch(jobId: null, keywords: ["smoke", "CreatedWithXboxDevCLI"]));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "Smoke", "--replace-tags"), handler);

        Assert.Equal(TestAccountCommandFactory.JobFailedExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("does not show the new keywords", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_WaitsForSandboxAndExpiryChanges()
    {
        var handler = new XboxPartnerHttpMessageHandler(Update(), Batch(jobId: null), Batch(jobId: null));

        Invocation result = await InvokeAsync(
            UpdateArgs("--confirm", "--add-sandbox-id", "abcd.1", "--access-expiration", "2030-01-15"), handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("Batch 42 updated.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_AnswerThatKeepsARemovedSandboxIsNotConfirmed()
    {
        // The answer and the batch agree, but both still show the sandbox the request removed.
        var handler = new XboxPartnerHttpMessageHandler(Update(), Batch(jobId: null), Batch(jobId: null));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--remove-sandbox-id", "ABCD.1"), handler);

        Assert.Equal(TestAccountCommandFactory.JobFailedExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("does not show the new sandboxes", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_ThreeFailedReadsWhileWaitingExitFour()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Update(),
            Problem(HttpStatusCode.BadGateway, null),
            Problem(HttpStatusCode.BadGateway, null),
            Problem(HttpStatusCode.BadGateway, null));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly"), handler);

        Assert.Equal(TestAccountCommandFactory.JobNotFinishedExitCode, result.ExitCode);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Patch);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Empty(result.Stdout);
        Assert.Contains("Could not read batch 42. The update may still be running", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_OneFailedReadWhileWaitingIsTolerated()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Update(),
            Problem(HttpStatusCode.BadGateway, null),
            Batch(jobId: null, keywords: ["nightly"]),
            Batch(jobId: null, keywords: ["nightly"]));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly"), handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Update_ReadThatCannotSucceedWhileWaitingExitsFour()
    {
        var handler = new XboxPartnerHttpMessageHandler(Update(), Problem(HttpStatusCode.NotFound, "test_account_batch_not_found"));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly"), handler);

        Assert.Equal(TestAccountCommandFactory.JobNotFinishedExitCode, result.ExitCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Empty(result.Stdout);
        Assert.Contains("Could not read batch 42", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_CanceledWhileWaitingExitsFour()
    {
        using var cancellation = new CancellationTokenSource();
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies() with
        {
            DelayAsync = async (_, token) =>
            {
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();
            },
        };
        var handler = new XboxPartnerHttpMessageHandler(Update());

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly"), handler, cancellation.Token);

        Assert.Equal(TestAccountCommandFactory.JobNotFinishedExitCode, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains("Stopped waiting. The update may still be running", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(30, 30)]
    public async Task WaitAsync_PollsAsRetryAfterAsksWithinBounds(int retryAfterSeconds, int expectedSeconds)
    {
        var delays = new List<TimeSpan>();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        TestAccountBatchWait wait = await TestAccountBatchWaiter.WaitAsync(
            TimeSpan.FromSeconds(retryAfterSeconds),
            _ => Task.FromResult(BatchJson(jobId: null)),
            deleting: false,
            TimeSpan.FromMinutes(5),
            (delay, _) =>
            {
                delays.Add(delay);
                now += delay;
                return Task.CompletedTask;
            },
            () => now,
            CancellationToken.None);

        Assert.Equal(TestAccountBatchWaitOutcome.JobEnded, wait.Outcome);
        Assert.Equal([TimeSpan.FromSeconds(expectedSeconds), TimeSpan.FromSeconds(expectedSeconds)], delays);
    }

    [Fact]
    public async Task WaitAsync_AbandonsAReadThatOutlastsTheDeadline()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var never = new TaskCompletionSource<string>();
        int reads = 0;

        TestAccountBatchWait wait = await TestAccountBatchWaiter.WaitAsync(
            retryAfter: null,
            _ => ++reads == 1 ? Task.FromResult(BatchJson(jobId: "upd-1")) : never.Task,
            deleting: false,
            TimeSpan.FromSeconds(20) + TimeSpan.FromMilliseconds(50),
            (delay, _) =>
            {
                now += delay;
                return Task.CompletedTask;
            },
            () => now,
            CancellationToken.None);

        Assert.Equal(TestAccountBatchWaitOutcome.TimedOut, wait.Outcome);
        Assert.Equal("upd-1", wait.LastBatch?.JobId);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task WaitAsync_WaitsAsAFailedReadAsksThenAsTheUpdateAsked()
    {
        var delays = new List<TimeSpan>();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var batchUri = new Uri("https://example.test/batches/42");
        var reads = new Queue<Func<string>>(
        [
            () => throw new XboxPartnerApiException(
                HttpStatusCode.TooManyRequests, null, string.Empty, batchUri, retryAfter: TimeSpan.FromSeconds(60)),
            () => throw new XboxPartnerApiException(HttpStatusCode.BadGateway, null, string.Empty, batchUri),
            () => BatchJson(jobId: null),
            () => BatchJson(jobId: null),
        ]);

        TestAccountBatchWait wait = await TestAccountBatchWaiter.WaitAsync(
            TimeSpan.FromSeconds(30),
            _ => Task.FromResult(reads.Dequeue()()),
            deleting: false,
            TimeSpan.FromMinutes(5),
            (delay, _) =>
            {
                delays.Add(delay);
                now += delay;
                return Task.CompletedTask;
            },
            () => now,
            CancellationToken.None);

        Assert.Equal(TestAccountBatchWaitOutcome.JobEnded, wait.Outcome);
        Assert.Equal(
            [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)],
            delays);
    }

    [Theory]
    [InlineData(true, "bypasses only the creator check")]
    [InlineData(false, "lacks the Partner Center role")]
    public async Task Update_AccessDeniedHintDependsOnAllowNotMine(bool allowNotMine, string hint)
    {
        var handler = new XboxPartnerHttpMessageHandler(Problem(HttpStatusCode.Forbidden, "test_account_access_denied"));
        string[] extra = allowNotMine ? ["--allow-not-mine"] : [];

        Invocation result = await InvokeAsync(UpdateArgs(["--confirm", "--tag", "nightly", .. extra]), handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Contains(hint, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_PromptReadFailureChangesNothing()
    {
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies(interactive: true);
        var handler = new XboxPartnerHttpMessageHandler(Problem(HttpStatusCode.NotFound, "test_account_batch_not_found"));

        Invocation result = await InvokeAsync(UpdateArgs("--tag", "nightly"), handler);

        Assert.Equal(1, result.ExitCode);
        CapturedHttpRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Empty(result.Stdout);
        Assert.Contains("Could not read batch 42, so nothing was changed.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_TableOutputShowsOneRowWithListNotes()
    {
        var handler = new XboxPartnerHttpMessageHandler(Update());

        Invocation result = await InvokeAsync(
            UpdateArgs("--confirm", "--no-wait", "--tag", "nightly", "--output", "table"), handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("BATCH ID", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("upd-1", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("2030-01-15", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Sandboxes: ABCD.1", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Keywords: nightly", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "test_account_batch_not_owned", "--allow-not-mine")]
    [InlineData(HttpStatusCode.Conflict, "test_account_batch_busy", "once its jobId is null")]
    [InlineData(HttpStatusCode.Forbidden, "test_account_sandbox_access_denied", "does not grant access to a sandbox")]
    [InlineData(HttpStatusCode.BadRequest, "test_account_keyword_invalid", "Check the --tag values.")]
    [InlineData(HttpStatusCode.NotFound, "test_account_batch_not_found", "Check the batch ID")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited", "You sent too many requests")]
    [InlineData(HttpStatusCode.MethodNotAllowed, null, "Updating test account batches may be unavailable.")]
    public async Task Update_RefusalExitsOneWithAHint(HttpStatusCode status, string? code, string hint)
    {
        var handler = new XboxPartnerHttpMessageHandler(Problem(status, code));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly"), handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains(hint, result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "test_account_update_outcome_unknown")]
    [InlineData(HttpStatusCode.GatewayTimeout, null)]
    public async Task Update_ServerFailureIsAnUnknownOutcome(HttpStatusCode status, string? code)
    {
        var handler = new XboxPartnerHttpMessageHandler(Problem(status, code));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly"), handler);

        Assert.Equal(TestAccountCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains("unknown whether batch 42 is being changed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Sending it again is safe", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("XboxPartner", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_CanceledBeforeSendingChangesNothing()
    {
        var handler = new XboxPartnerHttpMessageHandler(Update());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly"), handler, cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains("Nothing was changed", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_WithoutASellerExplainsTheDefaultInThePrompt()
    {
        var handler = new XboxPartnerHttpMessageHandler();

        Invocation result = await InvokeAsync(["test-account", "batch", "update", "42", "--tag", "nightly"], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("the service uses your only one", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("XboxPartner", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_VerbosePollingDoesNotExposeInternalServices()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Update(), Batch(jobId: null, keywords: ["nightly"]), Batch(jobId: null, keywords: ["nightly"]));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--verbose", "--tag", "nightly"), handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("Updating test account batch 42 using the signed in Entra ID token", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Reading test account batch 42", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("XboxPartner", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("timeout", "Timed out waiting for the service to answer the update")]
    [InlineData("dropped", "The connection failed while waiting for the service to answer the update")]
    public async Task Update_FailureAfterSendingIsAnUnknownOutcomeAndNotRetried(string failure, string message)
    {
        var handler = new XboxPartnerHttpMessageHandler(() => failure == "timeout"
            ? throw new TaskCanceledException("timed out")
            : throw new HttpRequestException(HttpRequestError.ResponseEnded, "response ended"));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly"), handler);

        Assert.Equal(TestAccountCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains(message, result.Stderr, StringComparison.Ordinal);
        Assert.Contains("unknown whether batch 42 is being changed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("xbox test-account batch get 42 --seller-id 1234567", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_NameResolutionFailureChangesNothing()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            () => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no such host"));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly"), handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains("did not reach the service", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_RefusesWithoutConfirmationOutsideATerminal()
    {
        var handler = new XboxPartnerHttpMessageHandler();

        Invocation result = await InvokeAsync(UpdateArgs("--add-sandbox-id", "ABCD.1"), handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("This changes test account batch 42 for seller 1234567", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Give access to sandboxes: ABCD.1", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Run again with --confirm (--yes, -y)", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_PromptShowsCurrentValuesAndUpdatesOnYes()
    {
        TestAccountCommandFactory.Dependencies =
            TestAccountSecretReaderTests.Dependencies(interactive: true, confirmation: () => "y");
        var handler = new XboxPartnerHttpMessageHandler(Batch(jobId: null, keywords: ["old"]), Update());

        Invocation result = await InvokeAsync(UpdateArgs("--no-wait", "--tag", "nightly"), handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal([HttpMethod.Get, HttpMethod.Patch], handler.Requests.Select(request => request.Method));
        Assert.Contains("This changes test account batch 42 (ABCD-tester, 5 accounts)", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Add tags: nightly (now old)", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Update this batch? [y/N]:", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_PromptDeclinedChangesNothing()
    {
        TestAccountCommandFactory.Dependencies =
            TestAccountSecretReaderTests.Dependencies(interactive: true, confirmation: () => "n");
        var handler = new XboxPartnerHttpMessageHandler(Batch(jobId: "create-1"));

        Invocation result = await InvokeAsync(UpdateArgs("--clear-tags"), handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Contains("Remove every tag", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("A job is running on this batch (create-1)", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Test account batch update canceled.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_RejectsTagsOverOneHundredCharacters()
    {
        var handler = new XboxPartnerHttpMessageHandler();

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", new string('a', 101)), handler);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("Each --tag must be at most 100 characters.", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Nothing to change.")]
    [InlineData("--tag and --clear-tags cannot be combined.", "--tag", "a", "--clear-tags")]
    [InlineData("--replace-tags needs --tag.", "--replace-tags")]
    [InlineData("--replace-tags and --clear-tags cannot be combined.", "--replace-tags", "--clear-tags")]
    [InlineData("Sandbox ABCD.1 cannot be both added and removed.", "--add-sandbox-id", "ABCD.1", "--remove-sandbox-id", "abcd.1")]
    [InlineData("--no-wait and --timeout cannot be combined.", "--tag", "a", "--no-wait", "--timeout", "1m")]
    [InlineData("--access-expiration must be a date", "--access-expiration", "soon")]
    public async Task Update_RejectsInvalidOptionsBeforeSending(string error, params string[] extra)
    {
        var handler = new XboxPartnerHttpMessageHandler();

        Invocation result = await InvokeAsync(UpdateArgs(["--confirm", .. extra]), handler);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains(error, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_PartialResponseIsAnUnknownOutcome()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Respond(HttpStatusCode.Accepted, """{"batchId":42,"jobId":"upd-1","state":"updating"}"""),
            Batch(jobId: null, keywords: ["CreatedWithXboxDevCLI"]),
            Batch(jobId: null, keywords: ["CreatedWithXboxDevCLI"]));

        Invocation result = await InvokeAsync(UpdateArgs("--confirm", "--tag", "nightly"), handler);

        Assert.Equal(TestAccountCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public void FindMismatches_TreatsMissingExpectedValuesAsMismatches()
    {
        var request = new UpdateTestAccountBatchRequest
        {
            AddSandboxes = ["ABCD.2"],
            AccessExpirationDate = new DateTimeOffset(2030, 1, 15, 0, 0, 0, TimeSpan.Zero),
            Keywords = ["nightly"],
            ReplaceKeywords = true,
        };
        var expected = new TestAccountBatchUpdate(42, "upd-1", "updating", null, null, null);
        var batch = new TestAccountBatch(
            42, "t", 5, 1, null, null, new DateTimeOffset(2030, 1, 15, 0, 0, 0, TimeSpan.Zero),
            null, ["nightly"], ["ABCD.2"], true, null, null);

        Assert.Equal(
            ["sandboxes", "access expiration", "keywords"],
            TestAccountBatchWaiter.FindMismatches(request, expected, batch));
        Assert.Empty(TestAccountBatchWaiter.FindMismatches(
            new UpdateTestAccountBatchRequest { Keywords = ["nightly"] }, expected, batch));
    }

    [Fact]
    public void FindMismatches_EmptyExpectedListsDoNotMatchMissingBatchLists()
    {
        var request = new UpdateTestAccountBatchRequest
        {
            RemoveSandboxes = ["ABCD.1"],
            Keywords = [],
            ReplaceKeywords = true,
        };
        var expected = new TestAccountBatchUpdate(42, "upd-1", "updating", [], null, []);
        var batch = new TestAccountBatch(42, "t", 5, 1, null, null, null, null, null, null, true, null, null);

        Assert.Equal(["sandboxes", "keywords"], TestAccountBatchWaiter.FindMismatches(request, expected, batch));
        Assert.Empty(TestAccountBatchWaiter.FindMismatches(
            request, expected, batch with { SandboxAccess = [], Keywords = [] }));
    }

    [Fact]
    public void FindMismatches_ChecksSandboxesAndExpiryAgainstTheRequest()
    {
        var expiry = new DateTimeOffset(2030, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var expected = new TestAccountBatchUpdate(42, "upd-1", "updating", ["ABCD.1", "ABCD.2"], expiry, null);
        var batch = new TestAccountBatch(
            42, "t", 5, 1, null, null, expiry, null, null, ["abcd.1", "ABCD.2"], true, null, null);

        Assert.Empty(TestAccountBatchWaiter.FindMismatches(
            new UpdateTestAccountBatchRequest { AddSandboxes = ["abcd.2"], AccessExpirationDate = expiry }, expected, batch));
        Assert.Equal(
            ["sandboxes"],
            TestAccountBatchWaiter.FindMismatches(
                new UpdateTestAccountBatchRequest { AddSandboxes = ["ABCD.3"] }, expected, batch));
        Assert.Equal(
            ["sandboxes"],
            TestAccountBatchWaiter.FindMismatches(
                new UpdateTestAccountBatchRequest { RemoveSandboxes = ["ABCD.2"] }, expected, batch));
        Assert.Equal(
            ["sandboxes"],
            TestAccountBatchWaiter.FindMismatches(
                new UpdateTestAccountBatchRequest { AddSandboxes = ["ABCD.2"] }, expected, batch with { SandboxAccess = ["ABCD.2"] }));
        Assert.Equal(
            ["access expiration"],
            TestAccountBatchWaiter.FindMismatches(
                new UpdateTestAccountBatchRequest { AccessExpirationDate = expiry.AddDays(1) }, expected, batch));
        Assert.Equal(
            ["access expiration"],
            TestAccountBatchWaiter.FindMismatches(
                new UpdateTestAccountBatchRequest { AccessExpirationDate = expiry },
                expected,
                batch with { AccessExpirationDate = expiry.AddDays(1) }));
    }

    [Fact]
    public void FindMismatches_ComparesOnlyRequestedFields()
    {
        var request = new UpdateTestAccountBatchRequest
        {
            AccessExpirationDate = new DateTimeOffset(2030, 1, 15, 8, 0, 0, TimeSpan.Zero),
        };
        var expected = new TestAccountBatchUpdate(
            42, "upd-1", "updating", ["ABCD.1"], new DateTimeOffset(2030, 1, 15, 0, 0, 0, TimeSpan.Zero), ["x"]);
        var batch = new TestAccountBatch(
            42, "t", 5, 1, null, null, new DateTimeOffset(2030, 1, 15, 23, 0, 0, TimeSpan.Zero),
            null, ["other"], ["OTHER.1"], true, null, null);

        Assert.Empty(TestAccountBatchWaiter.FindMismatches(request, expected, batch));
        Assert.Equal(
            ["access expiration"],
            TestAccountBatchWaiter.FindMismatches(request, expected, batch with { AccessExpirationDate = null }));
    }

    private static string[] UpdateArgs(params string[] extra) =>
        ["test-account", "batch", "update", "42", "--seller-id", "1234567", .. extra];

    private static HttpResponseMessage Update(
        string state = "updating",
        string? jobId = "upd-1",
        string[]? keywords = null,
        HttpStatusCode status = HttpStatusCode.Accepted,
        int? retryAfterSeconds = null)
    {
        var response = Respond(
            status,
            JsonSerializer.Serialize(new
            {
                batchId = 42,
                jobId,
                state,
                sandboxes = new[] { "ABCD.1" },
                accessExpirationDate = "2030-01-15T00:00:00Z",
                keywords = keywords ?? ["nightly", "CreatedWithXboxDevCLI"],
            }));
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    private static HttpResponseMessage Batch(string? jobId, string[]? keywords = null, string[]? sandboxes = null) =>
        Respond(HttpStatusCode.OK, BatchJson(jobId, keywords, sandboxes));

    private static string BatchJson(string? jobId, string[]? keywords = null, string[]? sandboxes = null) =>
        JsonSerializer.Serialize(new
        {
            batch = new
            {
                id = 42,
                emailTemplate = "ABCD-tester",
                batchSize = 5,
                createdBy = 777,
                isComplete = jobId is null,
                jobId,
                keywords = keywords ?? [],
                sandboxAccess = sandboxes ?? ["ABCD.1"],
                accessExpirationDate = "2030-01-15T00:00:00Z",
            },
            accounts = Array.Empty<object>(),
        });

    private static HttpResponseMessage Problem(HttpStatusCode statusCode, string? code) =>
        Respond(
            statusCode,
            code is null
                ? """{"title":"Failed"}"""
                : $$"""{"title":"Failed","status":{{(int)statusCode}},"detail":"Server detail.","code":"{{code}}"}""");

    private static HttpResponseMessage Respond(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<Invocation> InvokeAsync(
        string[] args,
        HttpMessageHandler handler,
        CancellationToken cancellationToken = default)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Program.XboxPartnerClientFactory = configuration => new XboxPartnerClient(
            configuration,
            new HttpClient(handler),
            () => Task.FromResult<string?>("caller-token"));

        try
        {
            int exitCode = cancellationToken.CanBeCanceled
                ? await Program.BuildRootCommand()
                    .Parse(args, Program.NoResponseFileConfig)
                    .InvokeAsync(cancellationToken: cancellationToken)
                : await Program.Main(args);
            return new Invocation(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record Invocation(int ExitCode, string Stdout, string Stderr);
}
