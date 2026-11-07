// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class TestAccountCreateCommandTests : IDisposable
{
    private const string Password = "Sup3r-Secret-Pw";
    private const string Answer = "secret-hobby-answer";

    private static readonly string[] SuggestedQuestions =
    [
        "What is your favorite hobby?",
        "Who is your favorite athlete?",
        "What is your maternal grandmother's first name?",
        "What was your childhood best friend's first name?",
        "What was the name of your first pet?",
        "What is your current best friend's first name?",
        "What was your favorite city to visit as a child?",
    ];

    private readonly string _passwordFile;
    private readonly string _answerFile;
    private readonly string? _originalProfile = Environment.GetEnvironmentVariable(ContextProfiles.EnvironmentVariable);

    public TestAccountCreateCommandTests()
    {
        // A profile from the developer's shell would add --profile to every recovery command.
        Environment.SetEnvironmentVariable(ContextProfiles.EnvironmentVariable, null);
        _passwordFile = Path.GetTempFileName();
        _answerFile = Path.GetTempFileName();
        File.WriteAllText(_passwordFile, Password + Environment.NewLine);
        File.WriteAllText(_answerFile, Answer);
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies();
    }

    public void Dispose()
    {
        TestAccountCommandFactory.Dependencies = TestAccountCommandDependencies.Default;
        Environment.SetEnvironmentVariable(ContextProfiles.EnvironmentVariable, _originalProfile);
        File.Delete(_passwordFile);
        File.Delete(_answerFile);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Create_NoWaitPostsOnceAndPrintsTheJob()
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running", retryAfterSeconds: 30));

        Invocation result = await InvokeAsync(
            CreateArgs("--confirm", "--no-wait", "--tag", " nightly ", "--subscription", "Silver", "--child-count", "0"),
            handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        CapturedHttpRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/xboxpartner/v1/testaccounts/batches", request.RequestUri!.AbsolutePath);
        using JsonDocument body = JsonDocument.Parse(request.Body!);
        Assert.Equal(1234567, body.RootElement.GetProperty("sellerId").GetInt64());
        Assert.Equal(5, body.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("childCount").GetInt32());
        Assert.Equal("ABCD-tester", body.RootElement.GetProperty("emailPrefix").GetString());
        Assert.Equal(Password, body.RootElement.GetProperty("password").GetString());
        Assert.Equal(Answer, body.RootElement.GetProperty("securityAnswer").GetString());
        Assert.Equal(
            TestAccountCommandFactory.DefaultSecurityQuestion,
            body.RootElement.GetProperty("securityQuestion").GetString());
        Assert.Equal("ABCD.1", body.RootElement.GetProperty("sandboxes")[0].GetString());
        Assert.Equal("nightly", body.RootElement.GetProperty("keywords")[0].GetString());
        Assert.Equal("Silver", body.RootElement.GetProperty("subscription").GetString());
        Assert.False(body.RootElement.TryGetProperty("locale", out _));
        using JsonDocument output = JsonDocument.Parse(result.Stdout);
        Assert.Equal("job-1", output.RootElement.GetProperty("jobId").GetString());
        Assert.Contains("job accepted: batch 42, job job-1", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            "xbox test-account job wait job-1 --batch-id 42 --seller-id 1234567",
            result.Stderr,
            StringComparison.Ordinal);
        AssertNoSecrets(result);
    }

    [Fact]
    public async Task Create_WaitsUntilTheJobSucceeds()
    {
        var handler = new ScriptedHandler(
            Job(HttpStatusCode.Accepted, "running"),
            Job(HttpStatusCode.OK, "running", succeeded: 2),
            Job(HttpStatusCode.OK, "succeeded", succeeded: 5));

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Equal(
            "/xboxpartner/v1/testaccounts/batches/42/jobs/job-1",
            handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("?sellerId=1234567", handler.Requests[1].RequestUri!.Query);
        Assert.Equal("succeeded", JsonDocument.Parse(result.Stdout).RootElement.GetProperty("state").GetString());
        Assert.Contains("Waiting up to 5 minutes for job job-1", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Job job-1: 2 of 5 created, 0 failed.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Job job-1 succeeded: 5 of 5 accounts created.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("xbox test-account batch get 42 --seller-id 1234567", result.Stderr, StringComparison.Ordinal);
        AssertNoSecrets(result);
    }

    [Fact]
    public async Task Create_FailedJobExitsTwoWithTheJobError()
    {
        var handler = new ScriptedHandler(
            Job(HttpStatusCode.Accepted, "running"),
            Job(HttpStatusCode.OK, "failed", succeeded: 1, errorCode: "test_account_job_failed", errorMessage: "Creation\nstopped"));

        Invocation result = await InvokeAsync(CreateArgs("--yes"), handler);

        Assert.Equal(TestAccountCommandFactory.JobFailedExitCode, result.ExitCode);
        Assert.Contains(
            "Job job-1 failed: test_account_job_failed: Creation stopped. 1 of 5 accounts were created.",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_StillRunningAtTheTimeoutExitsFourWithTheResumeCommand()
    {
        var handler = new ScriptedHandler(
            Job(HttpStatusCode.Accepted, "running"),
            Job(HttpStatusCode.OK, "running", succeeded: 1),
            Job(HttpStatusCode.OK, "running", succeeded: 1));

        Invocation result = await InvokeAsync(CreateArgs("-y", "--timeout", "20s"), handler);

        Assert.Equal(TestAccountCommandFactory.JobNotFinishedExitCode, result.ExitCode);
        Assert.Equal("running", JsonDocument.Parse(result.Stdout).RootElement.GetProperty("state").GetString());
        Assert.Contains("still running after 20 seconds (1 of 5 created)", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("resume waiting with: xbox test-account job wait job-1", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_PollFailureAfterAcceptanceExitsFourWithTheResumeCommand()
    {
        var handler = new ScriptedHandler(
            Job(HttpStatusCode.Accepted, "running"),
            Problem(HttpStatusCode.Forbidden, "test_account_access_denied"));

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler);

        Assert.Equal(TestAccountCommandFactory.JobNotFinishedExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("job accepted: batch 42, job job-1", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Could not read job job-1; it may still be running.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            "Resume waiting with: xbox test-account job wait job-1 --batch-id 42 --seller-id 1234567",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_TableOutputShowsTheJobRow()
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "succeeded", succeeded: 5));

        Invocation result = await InvokeAsync(CreateArgs("--confirm", "--output", "table"), handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("JOB ID", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("job-1", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "test_account_create_outcome_unknown")]
    [InlineData(HttpStatusCode.GatewayTimeout, "test_account_create_outcome_unknown")]
    [InlineData(HttpStatusCode.GatewayTimeout, null)]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    [InlineData(HttpStatusCode.BadGateway, null)]
    [InlineData(HttpStatusCode.BadGateway, "downstream_service_failed")]
    [InlineData(HttpStatusCode.BadGateway, "downstream_authentication_failed")]
    [InlineData(HttpStatusCode.GatewayTimeout, "downstream_gateway_timeout")]
    [InlineData(HttpStatusCode.InternalServerError, "internal_error")]
    [InlineData(HttpStatusCode.ServiceUnavailable, null)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "something_new")]
    [InlineData(HttpStatusCode.TemporaryRedirect, null)]
    [InlineData(HttpStatusCode.PermanentRedirect, null)]
    public async Task Create_OutcomeUnknownExitsThreeWithRecoverySteps(HttpStatusCode statusCode, string? code)
    {
        var handler = new ScriptedHandler(Problem(statusCode, code));

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler);

        AssertOutcomeUnknown(result, handler);
    }

    [Theory]
    [InlineData("feature_disabled")]
    [InlineData("service_configuration_invalid")]
    public async Task Create_ServiceUnavailableExitsOne(string? code)
    {
        var handler = new ScriptedHandler(Problem(HttpStatusCode.ServiceUnavailable, code));

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.DoesNotContain("it is unknown whether", result.Stderr, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Create_SignedOutSendsNothingAndExitsOne()
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(
            CreateArgs("--confirm"),
            handler,
            tokenFactory: () => Task.FromResult<string?>(null));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("auth login", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("The create was not sent.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_CanceledBeforeSendingExitsOne()
    {
        using var cancellation = new CancellationTokenSource();
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies(
            interactive: true,
            confirmation: () =>
            {
                cancellation.Cancel();
                return "y";
            });
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(CreateArgs("--no-wait"), handler, cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("Canceled before the create was sent. Nothing was created.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_CtrlCWhileWaitingExitsFourWithTheResumeCommand()
    {
        using var cancellation = new CancellationTokenSource();
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies() with
        {
            DelayAsync = (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler, cancellation.Token);

        Assert.Equal(TestAccountCommandFactory.JobNotFinishedExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        CapturedHttpRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("job accepted: batch 42, job job-1", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Stopped waiting; job job-1 keeps running on the service.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            "xbox test-account job wait job-1 --batch-id 42 --seller-id 1234567",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain("it is unknown whether", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_TokenFailureWhileWaitingExitsFour()
    {
        int tokens = 0;
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(
            CreateArgs("--confirm"),
            handler,
            tokenFactory: () => ++tokens == 1
                ? Task.FromResult<string?>("caller-token")
                : throw new Microsoft.Identity.Client.MsalUiRequiredException("invalid_grant", "Sign in again."));

        Assert.Equal(TestAccountCommandFactory.JobNotFinishedExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Single(handler.Requests);
        Assert.Contains("Sign in again.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Could not read job job-1; it may still be running.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ClientTimeoutWhilePollingReportsATimeout()
    {
        int reads = 0;
        var handler = new ScriptedHandler(() => ++reads == 1
            ? Job(HttpStatusCode.Accepted, "running")
            : throw new TaskCanceledException("timeout"));
        handler.Repeat = true;

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler);

        Assert.Equal(TestAccountCommandFactory.JobNotFinishedExitCode, result.ExitCode);
        Assert.Equal(1 + TestAccountJobWaiter.MaxConsecutiveReadFailures, handler.Requests.Count);
        Assert.Contains("Request timed out.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Could not read job job-1", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_UnexpectedPollFailureReportsTheMessage()
    {
        int reads = 0;
        var handler = new ScriptedHandler(() => ++reads == 1
            ? Job(HttpStatusCode.Accepted, "running")
            : throw new InvalidOperationException("Handler exploded."));
        handler.Repeat = true;

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler);

        Assert.Equal(TestAccountCommandFactory.JobNotFinishedExitCode, result.ExitCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("Request failed: Handler exploded.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_CtrlCAfterTheCreateWasSentIsAnUnknownOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new ScriptedHandler(() =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler, cancellation.Token);

        AssertOutcomeUnknown(result, handler);
        Assert.Contains("Canceled while waiting for the service to answer the create.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_UnexpectedFailureAfterTheCreateWasSentIsAnUnknownOutcome()
    {
        var handler = new ScriptedHandler(() => throw new InvalidOperationException("Handler exploded."));

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler);

        AssertOutcomeUnknown(result, handler);
        Assert.Contains(
            "The create was sent, but failed before its answer could be read: Handler exploded.",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ThrottledIsNotRetriedAndExitsOne()
    {
        HttpResponseMessage response = Problem(HttpStatusCode.TooManyRequests, "downstream_throttled");
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        var handler = new ScriptedHandler(response);

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains("\"code\": \"downstream_throttled\"", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("\"retryAfterSeconds\": 30", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("The test account service is throttling requests. Try again in 30 seconds.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("it is unknown whether", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_RecoveryCommandQuotesAJobIdThatLooksLikeAPlaceholder()
    {
        var handler = new ScriptedHandler(Respond(
            HttpStatusCode.Accepted,
            """{"batchId":42,"jobId":"<job>\u001b[31m","state":"running","total":5,"succeeded":0,"failed":0}"""));

        Invocation result = await InvokeAsync(CreateArgs("--confirm", "--no-wait"), handler);

        Assert.Equal(0, result.ExitCode);
        string quoted = ShellQuoting.Quote("<job> [31m");
        Assert.NotEqual("<job> [31m", quoted);
        Assert.Contains(
            $"Follow it with: xbox test-account job wait {quoted} --batch-id 42 --seller-id 1234567",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<job-id>")]
    [InlineData("<batch-id>")]
    public async Task Create_RecoveryCommandQuotesAJobIdEqualToAPlaceholder(string jobId)
    {
        var handler = new ScriptedHandler(Respond(
            HttpStatusCode.Accepted,
            $$"""{"batchId":42,"jobId":"{{jobId}}","state":"running","total":5,"succeeded":0,"failed":0}"""));

        Invocation result = await InvokeAsync(CreateArgs("--confirm", "--no-wait"), handler);

        Assert.Equal(0, result.ExitCode);
        string quoted = ShellQuoting.Quote(jobId);
        Assert.NotEqual(jobId, quoted);
        Assert.Contains(
            $"Follow it with: xbox test-account job wait {quoted} --batch-id 42 --seller-id 1234567",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_RecoveryCommandKeepsAnExplicitProfile()
    {
        var handler = new ScriptedHandler(Respond(
            HttpStatusCode.Accepted,
            """{"batchId":42,"jobId":"job-1","state":"running","total":5,"succeeded":0,"failed":0}"""));

        Invocation result = await InvokeAsync(["--profile", "work", .. CreateArgs("--confirm", "--no-wait")], handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "Follow it with: xbox --profile work test-account job wait job-1 --batch-id 42 --seller-id 1234567",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_RecoveryCommandKeepsAProfileFromTheEnvironment()
    {
        var handler = new ScriptedHandler(Respond(
            HttpStatusCode.Accepted,
            """{"batchId":42,"jobId":"job-1","state":"running","total":5,"succeeded":0,"failed":0}"""));
        Environment.SetEnvironmentVariable(ContextProfiles.EnvironmentVariable, "work");

        Invocation result = await InvokeAsync(CreateArgs("--confirm", "--no-wait"), handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "Follow it with: xbox --profile work test-account job wait job-1 --batch-id 42 --seller-id 1234567",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_VerbosePollingCleansAHostileJobId()
    {
        const string HostileJob = """{"batchId":42,"jobId":"job\u001b[31m\nFORGED","state":"running","total":5,"succeeded":0,"failed":0}""";
        var handler = new ScriptedHandler(
            Respond(HttpStatusCode.Accepted, HostileJob),
            Respond(HttpStatusCode.OK, HostileJob.Replace("running", "succeeded", StringComparison.Ordinal)
                .Replace("\"succeeded\":0", "\"succeeded\":5", StringComparison.Ordinal)));

        Invocation result = await InvokeAsync(CreateArgs("--confirm", "--verbose"), handler);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("Reading test account job ", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("XboxPartner", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/xboxpartner/", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\u001b", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\nFORGED", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_RecoveryCommandQuotesAJobIdSoTheShellCannotExpandIt()
    {
        var handler = new ScriptedHandler(Respond(
            HttpStatusCode.Accepted,
            """{"batchId":42,"jobId":"job$(Get-Date)'x\"y","state":"running","total":5,"succeeded":0,"failed":0}"""));

        Invocation result = await InvokeAsync(CreateArgs("--confirm", "--no-wait"), handler);

        Assert.Equal(0, result.ExitCode);
        string expected = OperatingSystem.IsWindows()
            ? "'job$(Get-Date)''x\"y'"
            : "'job$(Get-Date)'\"'\"'x\"y'";
        Assert.Contains(
            $"Follow it with: xbox test-account job wait {expected} --batch-id 42 --seller-id 1234567",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmCreate_WithoutASellerSaysXboxPartnerUsesYourOnlySeller()
    {
        var request = new CreateTestAccountBatchRequest
        {
            Count = 1,
            EmailPrefix = "ABCD-tester",
            Password = string.Empty,
            SecurityQuestion = "Q?",
            SecurityAnswer = string.Empty,
            Sandboxes = ["ABCD.1"],
        };
        using var stderr = new StringWriter();

        bool confirmed = TestAccountCommandFactory.ConfirmCreate(
            confirmed: false, TestAccountSecretReaderTests.Dependencies(), request, stderr);

        Assert.False(confirmed);
        Assert.Contains("for your only seller (no seller was given", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("pass --seller-id to choose", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("the service uses your only one", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_VerboseOutputNeverContainsTheSecrets()
    {
        var handler = new ScriptedHandler(
            Job(HttpStatusCode.Accepted, "running"),
            Job(HttpStatusCode.OK, "succeeded", succeeded: 5));

        Invocation result = await InvokeAsync(CreateArgs("--confirm", "--verbose"), handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Creating a test account batch using the signed in Entra ID token", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("XboxPartner", result.Stderr, StringComparison.OrdinalIgnoreCase);
        AssertNoSecrets(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_UnreadableSecretSourceFailsBeforeBuildingTheClient(bool missingFile)
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));
        string[] secretArgs = missingFile
            ? ["--password-file", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "--security-answer-file", _answerFile]
            : ["--security-answer-file", _answerFile];

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "create", "--seller-id", "1234567", "--email-prefix", "ABCD-tester",
             "--count", "1", "--sandbox-id", "ABCD.1", "--confirm", .. secretArgs],
            handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, s_clientsBuilt);
        Assert.Empty(handler.Requests);
        Assert.StartsWith(
            missingFile ? "Could not read --password-file" : "No terminal is available to prompt for the password.",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("y", 4)]
    [InlineData("n", 0)]
    public async Task Create_ConfirmsBeforeAskingForSecrets(string answer, int expectedPrompts)
    {
        var events = new List<string>();
        bool questionSelected = false;
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies(
            interactive: true,
            masked: prompt =>
            {
                events.Add(prompt);
                return prompt.Contains("assword", StringComparison.Ordinal) ? Password : Answer;
            },
            confirmation: () =>
            {
                if (!questionSelected)
                {
                    questionSelected = true;
                    events.Add("question");
                    return string.Empty;
                }

                events.Add("confirm");
                return answer;
            });
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "create", "--seller-id", "1234567", "--email-prefix", "ABCD-tester",
             "--count", "1", "--sandbox-id", "ABCD.1", "--no-wait"],
            handler);

        Assert.Equal("question", events[0]);
        Assert.Equal("confirm", events[1]);
        Assert.Equal(expectedPrompts, events.Count - 2);
        Assert.Equal(expectedPrompts == 0 ? 0 : 1, handler.Requests.Count);
        Assert.Equal(expectedPrompts == 0 ? 1 : 0, result.ExitCode);
    }

    // ConnectionError is included because the handler can resend on a new connection after a
    // pooled one drops, so a failed connect does not prove the create never reached the service.
    [Theory]
    [InlineData(HttpRequestError.ResponseEnded)]
    [InlineData(HttpRequestError.ConnectionError)]
    public async Task Create_DroppedConnectionIsAnUnknownOutcome(HttpRequestError error)
    {
        var handler = new ScriptedHandler(() => throw new HttpRequestException(error, "The connection failed."));

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler);

        AssertOutcomeUnknown(result, handler);
        Assert.Contains("The connection failed while waiting", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ClientTimeoutIsAnUnknownOutcome()
    {
        var handler = new ScriptedHandler(() => throw new TaskCanceledException("timeout"));

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler);

        AssertOutcomeUnknown(result, handler);
        Assert.Contains("Timed out waiting for the service", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_UnreadableAcceptedResponseIsAnUnknownOutcome()
    {
        var handler = new ScriptedHandler(Respond(HttpStatusCode.Accepted, "{}"));

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler);

        AssertOutcomeUnknown(result, handler);
        Assert.Contains("its response could not be read", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ConnectionThatNeverReachedTheServiceExitsOne()
    {
        var handler = new ScriptedHandler(() => throw new HttpRequestException(
            HttpRequestError.NameResolutionError, "No such host is known."));

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("did not reach the service", result.Stderr, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Create_RejectedRequestExitsOneWithCodeAndHint()
    {
        var response = Problem(HttpStatusCode.BadRequest, "test_account_subscription_unavailable");
        response.Headers.Add("x-ms-correlation-id", "server-correlation");
        var handler = new ScriptedHandler(response);

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("x-ms-correlation-id: server-correlation", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("\"code\": \"test_account_subscription_unavailable\"", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("some environments offer only Silver", result.Stderr, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Create_WithoutConfirmationOrTerminalSendsNothing()
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(CreateArgs(), handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("This creates 5 test accounts with email prefix ABCD-tester", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("in sandboxes ABCD.1 for seller 1234567.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Security question: What is your favorite hobby?", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Subscription: Gold (service default)", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("First name: Test (service default)", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Last name: Account (service default)", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("limit until the batch is deleted.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be removed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Access expires: three months from now (service default)", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Run again with --confirm", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_SummaryShowsTheValuesGiven()
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(
            CreateArgs(
                "--security-question", " Favorite game? ", "--subscription", "Silver", "--locale", "fr-FR",
                "--first-name", "Ada", "--last-name", "Lovelace", "--alternate-email", "dev@example.com",
                "--access-expiration", "2030-01-31", "--tag", "smoke"),
            handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("Security question: Favorite game?", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("First name: Ada", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Last name: Lovelace", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Subscription: Silver", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Locale: fr-FR", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Alternate email: dev@example.com", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Access expires: 2030-01-31", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Tags: smoke", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_DescribesScopeSafetyAndOutput()
    {
        var handler = new ScriptedHandler();

        Invocation result = await InvokeAsync(["test-account", "batch", "create", "--help"], handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        string help = string.Join(' ', result.Stdout.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("for --seller-id, XBOX_SELLER_ID, or the seller saved by 'context set'.", help, StringComparison.Ordinal);
        Assert.DoesNotContain("'seller set'", help, StringComparison.Ordinal);
        Assert.Contains("Mutates production state.", help, StringComparison.Ordinal);
        Assert.Contains("until the batch is deleted with 'xbox test-account batch delete'.", help, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be removed", help, StringComparison.Ordinal);
        Assert.Contains("The job may still be running with --no-wait or when --timeout passes first.", help, StringComparison.Ordinal);
        Assert.Contains(
            "stdout is empty when the create was refused or never sent (exit 1), when the outcome is unknown (exit 3), " +
                "and after Ctrl+C or a failed read while waiting (exit 4).",
            help,
            StringComparison.Ordinal);
        Assert.Contains("3 the outcome is unknown", help, StringComparison.Ordinal);
        Assert.Contains("$ xbox test-account batch create --email-prefix ABCD-tester", help, StringComparison.Ordinal);
        Assert.Contains("numbered choice before confirmation", help, StringComparison.Ordinal);
        Assert.DoesNotContain("See LEARN MORE", help, StringComparison.Ordinal);
        Assert.DoesNotContain(SuggestedQuestions[1], help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_SendsTheGivenSecurityQuestion()
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(
            CreateArgs("--confirm", "--no-wait", "--security-question", " Favorite game? "),
            handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("Favorite game?", body.RootElement.GetProperty("securityQuestion").GetString());
    }

    [Theory]
    [InlineData("silver", "Silver")]
    [InlineData(" GOLD ", "Gold")]
    public async Task Create_SendsTheSubscriptionInTheServiceSpelling(string given, string expected)
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(CreateArgs("--confirm", "--no-wait", "--subscription", given), handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal(expected, body.RootElement.GetProperty("subscription").GetString());
    }

    [Fact]
    public async Task Create_SendsOnlyTheFieldsTheServiceDefines()
    {
        // The service rejects unknown body fields, so every field sent must be in its contract.
        string[] contract =
        [
            "sellerId", "count", "childCount", "emailPrefix", "firstName", "lastName", "password",
            "securityQuestion", "securityAnswer", "sandboxes", "alternateEmail", "accessExpirationDate",
            "keywords", "locale", "subscription",
        ];
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(
            CreateArgs(
                "--confirm", "--no-wait", "--child-count", "1", "--first-name", "Ada", "--last-name", "Lovelace",
                "--security-question", "Favorite game?", "--alternate-email", "dev@example.com",
                "--access-expiration", "2030-01-31", "--tag", "smoke", "--locale", "fr-FR", "--subscription", "Silver"),
            handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        string[] sent = [.. body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];
        Assert.Equal(contract.Order(StringComparer.Ordinal), sent);
    }

    [Fact]
    public async Task Create_ValidationProblemShowsTheFieldErrorsAndExitsOne()
    {
        var handler = new ScriptedHandler(Respond(
            HttpStatusCode.BadRequest,
            """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"$.subscriptions":["The JSON property 'subscriptions' could not be mapped to any .NET member contained in type 'CreateTestAccountsRequest'."]}}"""));

        Invocation result = await InvokeAsync(CreateArgs("--confirm", "--no-wait"), handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("$.subscriptions", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("could not be mapped", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("A field sent by the CLI may not be supported.", result.Stderr, StringComparison.Ordinal);
        AssertNoSecrets(result);
    }

    [Theory]
    [InlineData("y", 1)]
    [InlineData("n", 0)]
    public async Task Create_PromptsForConfirmationInATerminal(string answer, int expectedRequests)
    {
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies(
            interactive: true, confirmation: () => answer);
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(CreateArgs("--no-wait", "--child-count", "2"), handler);

        Assert.Equal(expectedRequests, handler.Requests.Count);
        Assert.Equal(expectedRequests == 1 ? 0 : 1, result.ExitCode);
        Assert.Contains("This creates 5 test accounts, plus 2 child accounts linked to the first one, with email prefix", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Create these test accounts? [y/N]:", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "" }, "What is your favorite hobby?")]
    [InlineData(new[] { "1" }, "What is your favorite hobby?")]
    [InlineData(new[] { "7" }, "What was your favorite city to visit as a child?")]
    [InlineData(new[] { "invalid", "0", "8", " 2 " }, "Who is your favorite athlete?")]
    public async Task Create_SelectsQuestionBeforeConfirmationAndSecretPrompts(string[] choices, string expectedQuestion)
    {
        var prompts = new List<string>();
        var selections = new Queue<string>(choices);
        bool confirmed = false;
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies(
            interactive: true,
            masked: prompt =>
            {
                Assert.True(confirmed);
                prompts.Add(prompt);
                return prompt.Contains("assword", StringComparison.Ordinal) ? Password : Answer;
            },
            confirmation: () =>
            {
                if (selections.Count > 0)
                {
                    return selections.Dequeue();
                }

                confirmed = true;
                return "yes";
            });
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "create", "--seller-id", "1234567", "--email-prefix", "ABCD-tester",
             "--count", "1", "--sandbox-id", "ABCD.1", "--no-wait"],
            handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(4, prompts.Count);
        Assert.Contains($"Answer to \"{expectedQuestion}\": ", prompts);
        Assert.Contains($"Security question: {expectedQuestion}", result.Stderr, StringComparison.Ordinal);
        Assert.True(
            result.Stderr.IndexOf("Choose 1 to 7 [1]:", StringComparison.Ordinal) <
            result.Stderr.IndexOf("This creates", StringComparison.Ordinal));
        if (choices.Length > 1)
        {
            Assert.Contains("Enter a number from 1 to 7, or press Enter for 1.", result.Stderr, StringComparison.Ordinal);
        }
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal(Password, body.RootElement.GetProperty("password").GetString());
        Assert.Equal(expectedQuestion, body.RootElement.GetProperty("securityQuestion").GetString());
        AssertNoSecrets(result);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public async Task Create_SkipsPickerForExplicitInputsOrNoTerminal(
        bool interactive, bool explicitQuestion, bool answerFile)
    {
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies(
            interactive: interactive,
            masked: _ => Answer,
            confirmation: () => throw new InvalidOperationException("Unexpected picker."));
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "create", "--seller-id", "1234567", "--email-prefix", "ABCD-tester",
                "--count", "1", "--sandbox-id", "ABCD.1", "--password-file", _passwordFile, "--confirm", "--no-wait",
                .. (explicitQuestion ? new[] { "--security-question", "Favorite game?" } : Array.Empty<string>()),
                .. (answerFile ? new[] { "--security-answer-file", _answerFile } : Array.Empty<string>())],
            handler);

        Assert.DoesNotContain("Choose 1 to 7", result.Stderr, StringComparison.Ordinal);
        if (!interactive && !answerFile)
        {
            Assert.Equal(1, result.ExitCode);
            Assert.Empty(handler.Requests);
            Assert.Equal(0, s_clientsBuilt);
            Assert.Contains("No terminal is available", result.Stderr, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(0, result.ExitCode);
            using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
            Assert.Equal(
                explicitQuestion ? "Favorite game?" : TestAccountCommandFactory.DefaultSecurityQuestion,
                body.RootElement.GetProperty("securityQuestion").GetString());
        }
    }

    [Theory]
    [InlineData("eof")]
    [InlineData("cancel")]
    [InlineData("io")]
    public async Task Create_PickerAbortDoesNotReadSecretsOrBuildTheClient(string failure)
    {
        using var cancellation = new CancellationTokenSource();
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies(
            interactive: true,
            confirmation: () =>
            {
                if (failure == "io")
                {
                    throw new IOException("Input unavailable.");
                }

                if (failure == "cancel")
                {
                    cancellation.Cancel();
                    return string.Empty;
                }

                return null;
            });
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "create", "--seller-id", "1234567", "--email-prefix", "ABCD-tester",
                "--count", "1", "--sandbox-id", "ABCD.1", "--password-file", _passwordFile, "--confirm", "--no-wait"],
            handler,
            cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, s_clientsBuilt);
        Assert.Contains(
            failure == "io" ? "Could not read a security question selection" : "Test account creation canceled",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ServicePasswordRejectionPreservesDiagnosticsWithoutRetrying()
    {
        var response = Respond(
            HttpStatusCode.BadRequest,
            $$"""{"status":400,"title":"Failed","detail":"{{TestAccountSecretReader.PasswordRequirements}}","code":"test_account_password_invalid"}""");
        response.Headers.Add("x-ms-correlation-id", "server-correlation");
        var handler = new ScriptedHandler(response);

        Invocation result = await InvokeAsync(CreateArgs("--confirm"), handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Single(handler.Requests);
        using JsonDocument problem = JsonDocument.Parse(
            result.Stderr[result.Stderr.IndexOf('{')..]);
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("test_account_password_invalid", problem.RootElement.GetProperty("code").GetString());
        Assert.StartsWith(
            TestAccountSecretReader.PasswordRequirements,
            problem.RootElement.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.Contains("x-ms-correlation-id: server-correlation", result.Stderr, StringComparison.Ordinal);
        AssertNoSecrets(result);
    }

    [Fact]
    public async Task Create_HelpExplainsPasswordRequirementsWithoutReadingSecrets()
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(["test-account", "batch", "create", "--help"], handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("Password must be 8 to 16 characters", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Underscore does not count as a symbol.", result.Stdout, StringComparison.Ordinal);
        AssertNoSecrets(result);
    }

    [Fact]
    public async Task Create_ShowsRequirementsBeforePromptingAndSendsPasswordForServiceValidation()
    {
        const string password = "Abcdefg_";
        var secrets = new Queue<string>([password, password, Answer, Answer]);
        var prompts = new List<string>();
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies(
            interactive: true,
            masked: prompt =>
            {
                Console.Error.WriteLine("Masked prompt opened.");
                prompts.Add(prompt);
                return secrets.Dequeue();
            },
            confirmation: () => string.Empty);
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "create", "--seller-id", "1234567", "--email-prefix", "ABCD-tester",
             "--count", "1", "--sandbox-id", "ABCD.1", "--confirm", "--no-wait"],
            handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Empty(secrets);
        Assert.Single(prompts, prompt => prompt == "Password: ");
        Assert.Single(prompts, prompt => prompt == "Confirm password: ");
        Assert.Contains("Underscore does not count as a symbol.", result.Stderr, StringComparison.Ordinal);
        Assert.True(
            result.Stderr.IndexOf(TestAccountSecretReader.PasswordRequirements, StringComparison.Ordinal) <
            result.Stderr.IndexOf("Masked prompt opened.", StringComparison.Ordinal));
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal(password, body.RootElement.GetProperty("password").GetString());
        Assert.DoesNotContain(password, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(password, result.Stdout, StringComparison.Ordinal);
        AssertNoSecrets(result);
    }

    [Fact]
    public async Task Create_PasswordFromFileOrStdinIsSentForServiceValidation()
    {
        const string password = "Abcdefg_";
        File.WriteAllText(_passwordFile, password);
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies(stdin: password);

        foreach (string source in new[] { _passwordFile, "-" })
        {
            var response = Respond(
                HttpStatusCode.BadRequest,
                $$"""{"status":400,"title":"Failed","detail":"{{TestAccountSecretReader.PasswordRequirements}}","code":"test_account_password_invalid"}""");
            var handler = new ScriptedHandler(response);
            string[] args = CreateArgs("--confirm", "--no-wait");
            args[Array.IndexOf(args, "--password-file") + 1] = source;

            Invocation result = await InvokeAsync(args, handler);

            Assert.Equal(1, result.ExitCode);
            using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
            Assert.Equal(password, body.RootElement.GetProperty("password").GetString());
            Assert.Empty(result.Stdout);
            Assert.Contains(TestAccountSecretReader.PasswordRequirements, result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(password, result.Stderr, StringComparison.Ordinal);
            AssertNoSecrets(result);
        }
    }

    [Theory]
    [InlineData("file")]
    [InlineData("stdin")]
    [InlineData("prompt")]
    public async Task Create_EmptyPasswordFailsBeforeSubmission(string source)
    {
        File.WriteAllText(_passwordFile, string.Empty);
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies(
            interactive: source == "prompt", masked: _ => string.Empty);
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "create", "--seller-id", "1234567", "--email-prefix", "ABCD-tester",
             "--count", "1", "--sandbox-id", "ABCD.1", "--confirm", "--no-wait",
             "--security-answer-file", _answerFile,
             .. (source == "prompt" ? Array.Empty<string>() : new[] { "--password-file", source == "stdin" ? "-" : _passwordFile })],
            handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains("The password must not be empty.", result.Stderr, StringComparison.Ordinal);
        AssertNoSecrets(result);
    }

    [Fact]
    public async Task Create_PasswordMatchingSecurityAnswerIsSentForServiceValidation()
    {
        const string answer = "Abcdef12";
        File.WriteAllText(_passwordFile, "Abcdef12");
        File.WriteAllText(_answerFile, answer);
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(CreateArgs("--confirm", "--no-wait"), handler);

        Assert.Equal(0, result.ExitCode);
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("Abcdef12", body.RootElement.GetProperty("password").GetString());
        Assert.Equal(answer, body.RootElement.GetProperty("securityAnswer").GetString());

        Assert.DoesNotContain(answer, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(answer, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Abcdef12", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ReadsThePasswordFromStandardInput()
    {
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies(stdin: Password + "\n");
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "create", "--seller-id", "1234567", "--email-prefix", "ABCD-tester",
             "--count", "1", "--sandbox-id", "ABCD.1", "--password-file", "-", "--security-answer-file", _answerFile,
             "--confirm", "--no-wait"],
            handler);

        Assert.True(result.ExitCode == 0, result.Stderr);
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal(Password, body.RootElement.GetProperty("password").GetString());
        Assert.Equal(Answer, body.RootElement.GetProperty("securityAnswer").GetString());
        AssertNoSecrets(result);
    }

    [Theory]
    [InlineData("--tag", "smoke")]
    [InlineData("--sandbox-id", "ABCD.2")]
    public async Task Create_ARepeatedOptionWithoutAValueFailsBeforeAnyRequest(string option, string value)
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));
        string[] args =
        [
            "test-account", "batch", "create", "--seller-id", "1234567", "--email-prefix", "ABCD-tester",
            "--sandbox-id", "ABCD.1", "--count", "5", "--confirm", "--no-wait", option, value, option,
        ];

        Invocation result = await InvokeAsync(args, handler);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains($"Each {option} requires a non-empty value.", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "--count", "0" }, "--count must be at least 1.")]
    [InlineData(new[] { "--child-count", "-1" }, "--child-count must not be negative.")]
    [InlineData(new[] { "--tag", " " }, "Each --tag requires a non-empty value.")]
    [InlineData(new[] { "--security-question", " " }, "--security-question must not be empty.")]
    [InlineData(new[] { "--subscription", "Platinum" }, "--subscription must be Silver or Gold.")]
    [InlineData(new[] { "--access-expiration", "soon" }, "--access-expiration must be a date")]
    [InlineData(new[] { "--no-wait", "--timeout", "5m" }, "--no-wait and --timeout cannot be combined.")]
    [InlineData(new[] { "--timeout", "8d" }, "--timeout must be a positive duration")]
    [InlineData(new[] { "--timeout", "0s" }, "--timeout must be a positive duration")]
    [InlineData(new[] { "--password-file", "-", "--security-answer-file", "-" }, "cannot both read stdin")]
    public async Task Create_InvalidOptionsFailBeforeAnyRequest(string[] options, string expected)
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.Accepted, "running"));
        string[] args =
        [
            "test-account", "batch", "create", "--seller-id", "1234567", "--email-prefix", "ABCD-tester",
            "--sandbox-id", "ABCD.1", "--confirm", .. options, .. (options.Contains("--count") ? Array.Empty<string>() : ["--count", "5"]),
        ];

        Invocation result = await InvokeAsync(args, handler);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobGet_RunningJobExitsZeroWithRetryHint()
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.OK, "running", retryAfterSeconds: 120));

        Invocation result = await InvokeAsync(
            ["test-account", "job", "get", "job-1", "--batch-id", "42", "--seller-id", "1234567"], handler);

        Assert.Equal(0, result.ExitCode);
        CapturedHttpRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/xboxpartner/v1/testaccounts/batches/42/jobs/job-1", request.RequestUri!.AbsolutePath);
        Assert.Equal("job-1", JsonDocument.Parse(result.Stdout).RootElement.GetProperty("jobId").GetString());
        Assert.Contains("Check again in 2 minutes", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobGet_FailedJobExitsTwo()
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.OK, "failed", errorCode: "test_account_job_failed"));

        Invocation result = await InvokeAsync(["test-account", "job", "get", "job-1", "--batch-id", "42", "--seller-id", "1234567"], handler);

        Assert.Equal(TestAccountCommandFactory.JobFailedExitCode, result.ExitCode);
        Assert.Contains("xbox test-account batch get 42", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobGet_AccessDeniedExplainsOwnership()
    {
        var handler = new ScriptedHandler(Problem(HttpStatusCode.Forbidden, "test_account_access_denied"));

        Invocation result = await InvokeAsync(["test-account", "job", "get", "job-1", "--batch-id", "42", "--seller-id", "1234567"], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Another developer or another seller owns this job", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobGet_TableOutputSendsProgressAndErrorNotesToStderr()
    {
        var handler = new ScriptedHandler(Job(
            HttpStatusCode.OK,
            "failed",
            succeeded: 1,
            errorCode: "test_account_job_failed",
            errorMessage: "Creation stopped",
            progress: "1 of 5"));

        Invocation result = await InvokeAsync(
            ["test-account", "job", "get", "job-1", "--batch-id", "42", "--seller-id", "1234567", "--output", "table"],
            handler);

        Assert.Equal(TestAccountCommandFactory.JobFailedExitCode, result.ExitCode);
        Assert.Contains("job-1", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Progress:", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Progress: 1 of 5", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Error: test_account_job_failed: Creation stopped", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--batch-id", "0")]
    [InlineData("--batch-id", "abc")]
    public async Task JobGet_InvalidBatchIdFailsBeforeAnyRequest(string name, string value)
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.OK, "running"));

        Invocation result = await InvokeAsync(["test-account", "job", "get", "job-1", "--seller-id", "1234567", name, value], handler);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task JobWait_PollsUntilSucceededWithTable()
    {
        var handler = new ScriptedHandler(
            Job(HttpStatusCode.OK, "running"),
            Job(HttpStatusCode.OK, "succeeded", succeeded: 5));

        Invocation result = await InvokeAsync(
            ["test-account", "job", "wait", "job-1", "--batch-id", "42", "--seller-id", "1234567", "--output", "table"], handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("succeeded", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Job job-1 succeeded", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobWait_StoppedJobReturnsWithoutPolling()
    {
        var handler = new ScriptedHandler(Job(HttpStatusCode.OK, "failed"));

        Invocation result = await InvokeAsync(["test-account", "job", "wait", "job-1", "--batch-id", "42", "--seller-id", "1234567"], handler);

        Assert.Equal(TestAccountCommandFactory.JobFailedExitCode, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("Waiting up to", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobWait_ReadFailureWhileWaitingExitsFour()
    {
        var handler = new ScriptedHandler(
            Job(HttpStatusCode.OK, "running"),
            Problem(HttpStatusCode.ServiceUnavailable, "feature_disabled"));

        Invocation result = await InvokeAsync(
            ["test-account", "job", "wait", "job-1", "--batch-id", "42", "--seller-id", "1234567", "--timeout", "1h"], handler);

        Assert.Equal(TestAccountCommandFactory.JobNotFinishedExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Waiting up to 1 hour", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Test account creation is disabled.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Resume waiting with: xbox test-account job wait job-1 --batch-id 42", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobWait_CtrlCExitsFourWithTheResumeCommand()
    {
        using var cancellation = new CancellationTokenSource();
        TestAccountCommandFactory.Dependencies = TestAccountSecretReaderTests.Dependencies() with
        {
            DelayAsync = (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };
        var handler = new ScriptedHandler(Job(HttpStatusCode.OK, "running"));

        Invocation result = await InvokeAsync(
            ["test-account", "job", "wait", "job-1", "--batch-id", "42", "--seller-id", "1234567"],
            handler,
            cancellation.Token);

        Assert.Equal(TestAccountCommandFactory.JobNotFinishedExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Single(handler.Requests);
        Assert.Contains("Stopped waiting; job job-1 keeps running on the service.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobWait_StillRunningAtTheTimeoutExitsFour()
    {
        var handler = new ScriptedHandler(
            Job(HttpStatusCode.OK, "running"),
            Job(HttpStatusCode.OK, "running", succeeded: 2),
            Job(HttpStatusCode.OK, "running", succeeded: 2));

        Invocation result = await InvokeAsync(
            ["test-account", "job", "wait", "job-1", "--batch-id", "42", "--seller-id", "1234567", "--timeout", "20s"], handler);

        Assert.Equal(TestAccountCommandFactory.JobNotFinishedExitCode, result.ExitCode);
        Assert.Equal("running", JsonDocument.Parse(result.Stdout).RootElement.GetProperty("state").GetString());
        Assert.Contains("still running after 20 seconds (2 of 5 created)", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("resume waiting with: xbox test-account job wait job-1 --batch-id 42", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobWait_InitialReadFailureExitsOne()
    {
        var handler = new ScriptedHandler(Respond(HttpStatusCode.OK, "not json"));

        Invocation result = await InvokeAsync(["test-account", "job", "wait", "job-1", "--batch-id", "42", "--seller-id", "1234567"], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("x-ms-correlation-id: ", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Request failed: ", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("test_account_sandbox_access_denied", HttpStatusCode.Forbidden, true, "--sandbox-id")]
    [InlineData("test_account_sandbox_access_denied", HttpStatusCode.Forbidden, true, "grant no sandboxes at all")]
    [InlineData("test_account_count_invalid", HttpStatusCode.BadRequest, true, "--count")]
    [InlineData("test_account_child_count_invalid", HttpStatusCode.BadRequest, true, "--child-count")]
    [InlineData("test_account_email_prefix_invalid", HttpStatusCode.BadRequest, true, "ABCD-tester")]
    [InlineData("test_account_email_prefix_in_use", HttpStatusCode.Conflict, true, null)]
    [InlineData("test_account_name_invalid", HttpStatusCode.BadRequest, true, "--first-name")]
    [InlineData("test_account_password_invalid", HttpStatusCode.BadRequest, true, "password rules")]
    [InlineData("test_account_security_question_invalid", HttpStatusCode.BadRequest, true, "--security-question")]
    [InlineData("test_account_security_answer_invalid", HttpStatusCode.BadRequest, true, "differs from the password")]
    [InlineData("test_account_alternate_email_invalid", HttpStatusCode.BadRequest, true, "--alternate-email")]
    [InlineData("test_account_keyword_invalid", HttpStatusCode.BadRequest, true, "--tag")]
    [InlineData("test_account_locale_invalid", HttpStatusCode.BadRequest, true, "en-US")]
    [InlineData("test_account_limit_exceeded", HttpStatusCode.Conflict, true, "test account limit")]
    [InlineData("test_account_access_denied", HttpStatusCode.Forbidden, true, "Partner Center role")]
    [InlineData("downstream_request_rejected", HttpStatusCode.BadRequest, true, "sandbox groups")]
    [InlineData("downstream_request_rejected", HttpStatusCode.BadGateway, false, null)]
    [InlineData("downstream_request_rejected", HttpStatusCode.NotFound, false, "No such job")]
    [InlineData("downstream_throttled", HttpStatusCode.ServiceUnavailable, false, "throttling")]
    [InlineData("rate_limited", HttpStatusCode.TooManyRequests, true, "too many requests")]
    [InlineData("downstream_service_failed", HttpStatusCode.BadGateway, true, null)]
    [InlineData("downstream_service_failed", HttpStatusCode.BadGateway, false, "Retry.")]
    [InlineData("downstream_gateway_timeout", HttpStatusCode.BadGateway, false, "Retry.")]
    [InlineData("service_configuration_invalid", HttpStatusCode.ServiceUnavailable, false, "not configured")]
    [InlineData("authentication_interaction_required", HttpStatusCode.Forbidden, true, null)]
    [InlineData("invalid_request", HttpStatusCode.BadRequest, true, "see detail and errors")]
    [InlineData("invalid_request", HttpStatusCode.BadRequest, false, null)]
    [InlineData(null, HttpStatusCode.BadRequest, true, "see errors")]
    [InlineData(null, HttpStatusCode.BadRequest, false, null)]
    [InlineData(null, HttpStatusCode.NotFound, false, "--batch-id")]
    [InlineData(null, HttpStatusCode.NotFound, true, "Creating test accounts may be unavailable.")]
    [InlineData(null, HttpStatusCode.InternalServerError, true, null)]
    public void DescribeFailure_CreationHintsAreKeyedOnTheCode(
        string? code,
        HttpStatusCode statusCode,
        bool isCreate,
        string? expected)
    {
        string? hint = TestAccountCommandFactory.DescribeFailure(
            statusCode,
            code is null ? null : $$"""{"code":"{{code}}","detail":"ignored test_account_access_denied"}""",
            isCreate ? TestAccountCommandFactory.TestAccountRequest.Create : TestAccountCommandFactory.TestAccountRequest.Job);

        if (expected is null)
        {
            Assert.Null(hint);
        }
        else
        {
            Assert.Contains(expected, hint, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("10M", 600)]
    [InlineData("2h", 7200)]
    [InlineData("7d", 604800)]
    public void ParseDuration_AcceptsUnits(string value, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), TestAccountCommandFactory.ParseDuration(value));
    }

    [Theory]
    [InlineData("10")]
    [InlineData("-5m")]
    [InlineData("1.5h")]
    [InlineData("604801s")]
    [InlineData("1234567890s")]
    public void ParseDuration_RejectsInvalidValues(string value)
    {
        Assert.Null(TestAccountCommandFactory.ParseDuration(value));
    }

    [Theory]
    [InlineData(1, "1 second")]
    [InlineData(90, "90 seconds")]
    [InlineData(300, "5 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(172800, "2 days")]
    public void FormatDuration_UsesTheLargestWholeUnit(int seconds, string expected)
    {
        Assert.Equal(expected, TestAccountCommandFactory.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    private string[] CreateArgs(params string[] extra) =>
    [
        "test-account", "batch", "create",
        "--seller-id", "1234567",
        "--email-prefix", " ABCD-tester ",
        "--count", "5",
        "--sandbox-id", "ABCD.1",
        "--password-file", _passwordFile,
        "--security-answer-file", _answerFile,
        .. extra,
    ];

    private static void AssertOutcomeUnknown(Invocation result, ScriptedHandler handler)
    {
        Assert.Equal(TestAccountCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains("it is unknown whether a batch was created", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            "xbox test-account batch list --mine --email-prefix ABCD-tester --seller-id 1234567",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Contains(
            "xbox test-account job wait <job-id> --batch-id <batch-id> --seller-id 1234567",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Contains("Create again only if no such batch exists.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("XboxPartner", result.Stderr, StringComparison.OrdinalIgnoreCase);
        AssertNoSecrets(result);
    }

    private static void AssertNoSecrets(Invocation result)
    {
        foreach (string secret in new[] { Password, Answer })
        {
            Assert.DoesNotContain(secret, result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, result.Stderr, StringComparison.Ordinal);
        }
    }

    private static HttpResponseMessage Job(
        HttpStatusCode statusCode,
        string state,
        int succeeded = 0,
        string? errorCode = null,
        string? errorMessage = null,
        int? retryAfterSeconds = null,
        string? progress = null)
    {
        var response = Respond(
            statusCode,
            JsonSerializer.Serialize(new
            {
                batchId = 42,
                jobId = "job-1",
                state,
                total = 5,
                succeeded,
                failed = 0,
                errorCode,
                errorMessage,
                progress,
            }));
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    private static HttpResponseMessage Problem(HttpStatusCode statusCode, string? code) =>
        Respond(
            statusCode,
            code is null
                ? """{"title":"Gateway Timeout"}"""
                : $$"""{"title":"Failed","status":{{(int)statusCode}},"detail":"Server detail.","code":"{{code}}"}""");

    private static HttpResponseMessage Respond(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<Invocation> InvokeAsync(
        string[] args,
        HttpMessageHandler handler,
        CancellationToken cancellationToken = default,
        Func<Task<string?>>? tokenFactory = null)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        s_clientsBuilt = 0;
        Program.XboxPartnerClientFactory = configuration =>
        {
            s_clientsBuilt++;
            return new XboxPartnerClient(
                configuration,
                new HttpClient(handler),
                tokenFactory ?? (() => Task.FromResult<string?>("caller-token")));
        };

        try
        {
            // Main cannot take a token, so a test that presses Ctrl+C invokes the parsed command
            // directly with one.
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

    private static int s_clientsBuilt;

    /// <summary>Returns scripted responses in order, or throws, and records every request.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;

        public ScriptedHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<Func<HttpResponseMessage>>(responses.Select(response => (Func<HttpResponseMessage>)(() => response)));
        }

        public ScriptedHandler(Func<HttpResponseMessage> response)
        {
            _responses = new Queue<Func<HttpResponseMessage>>([response]);
        }

        public List<CapturedHttpRequest> Requests { get; } = [];

        /// <summary>When true, the single scripted response answers every request.</summary>
        public bool Repeat { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedHttpRequest(
                request.Method,
                request.RequestUri,
                body,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase)));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No scripted response remains for this request.");
            }

            return Repeat ? _responses.Peek()() : _responses.Dequeue()();
        }
    }
}
