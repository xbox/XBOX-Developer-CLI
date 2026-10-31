// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class GameCreationCommandTests
{
    private const string Accepted =
        """{"jobId":"job-1","status":"notStarted","result":"pending","game":null,"errors":null}""";
    private const string Running =
        """{"jobId":"job-1","status":"running","result":"pending","game":null,"errors":null}""";
    private const string Succeeded =
        """{"jobId":"job-1","status":"completed","result":"succeeded","game":{"productId":"00000000000000000042"},"errors":null}""";
    private const string SucceededWithoutDetails =
        """{"jobId":"job-1","status":"completed","result":"succeeded","game":null,"errors":null}""";
    private const string SucceededWithOmittedFields =
        """{"jobId":"job-1","status":"completed","result":"succeeded"}""";
    private const string SucceededWithErrors =
        """{"jobId":"job-1","status":"completed","result":"succeeded","game":{"productId":"00000000000000000042"},"errors":[{"code":"serviceUnavailable","message":"Enablement is incomplete."}]}""";
    private const string SucceededWithFullSummary =
        """{"jobId":"job-1","status":"completed","result":"succeeded","game":{"productId":"00000000000000000042","storeId":"9N0000000000","xboxTitleId":"FFFFFFFF","SCID":"00000000-0000-0000-0000-0000FFFFFFFF","primaryDevelopmentSandbox":"TESTSB.42","name":"Nod Arena","packageIdentityName":"Nod.Arena","packageIdentityPublisher":"CN=Nod Publishers","packageFamilyName":"Nod.Arena_123","msaAppId":null},"errors":null}""";
    private const string Failed =
        """{"jobId":"job-1","status":"completed","result":"failed","game":null,"errors":[{"code":"invalidRequest","message":"The name is reserved.","details":[{"code":"businessValidationError","message":"Another publisher."}]}]}""";
    private static readonly string[] CreateArguments =
        ["game", "create", "--name", "Nod Arena", "--supported-device", "xbox"];

    [Theory]
    [InlineData("--confirm", 0)]
    [InlineData("--yes", 0)]
    [InlineData("-y", 0)]
    [InlineData("--confirm", 1)]
    [InlineData("--yes", 1)]
    [InlineData("-y", 1)]
    [InlineData("--confirm", 2)]
    [InlineData("--yes", 2)]
    [InlineData("-y", 2)]
    public async Task ConfirmationAliases_WorkAtEveryCommandDepth(string alias, int position)
    {
        var handler = Handler(Succeeded);
        var args = CreateArguments.ToList();
        args.Insert(position, alias);
        var result = await InvokeAsync(handler, args.ToArray());

        Assert.Equal(0, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("[y/N]", result.Stderr);
    }

    [Theory]
    [InlineData(false, "", false)]
    [InlineData(true, "n", false)]
    [InlineData(true, "", false)]
    [InlineData(true, "OK", false)]
    [InlineData(true, "y", true)]
    [InlineData(true, "yes", true)]
    [InlineData(true, "Y", true)]
    public async Task Confirmation_FailsClosedOrPromptsOnStderr(
        bool interactive, string answer, bool accepted)
    {
        var handler = Handler(Succeeded);
        var result = await InvokeAsync(handler, CreateArguments, interactive, answer);

        Assert.Equal(accepted ? 0 : 1, result.ExitCode);
        Assert.Equal(accepted ? 1 : 0, handler.Requests.Count);
        Assert.Contains(
            "Creating the game 'Nod Arena' under seller seller-1 is irreversible from this CLI.",
            result.Stderr);
        Assert.DoesNotContain("POST ", result.Stderr);
        Assert.DoesNotContain("/xboxpartner/v1/games", result.Stderr);
        Assert.DoesNotContain("""{"name":"Nod Arena","deviceFamilies":["xbox"]}""", result.Stderr);
        Assert.Equal(interactive, result.Stderr.Contains("Create this game? [y/N]: ", StringComparison.Ordinal));
        Assert.DoesNotContain("[y/N]", result.Stdout);
        if (!accepted)
        {
            Assert.Empty(result.Stdout);
        }
    }

    [Fact]
    public async Task LocalFalse_DoesNotInheritRootConfirmation()
    {
        var handler = Handler(Succeeded);
        var result = await InvokeAsync(
            handler, ["--confirm", .. CreateArguments, "--confirm", "false"]);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("Re-run with --confirm", result.Stderr);
    }

    [Fact]
    public async Task Create_MapsNamedInputsAndPreservesCompactJson()
    {
        var handler = Handler(Succeeded);
        var result = await InvokeAsync(handler,
            ["game", "create", "--name", "  Nod Arena  ", "--supported-device", "XBOX",
                "--supported-device", "Desktop", "--sandbox-id", "TESTSB.42",
                "--confirm", "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Succeeded, result.Stdout.Trim());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/xboxpartner/v1/games", request.RequestUri!.AbsolutePath);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("seller-1", Assert.Single(request.Headers["X-DeveloperAccountId"]));
        Assert.Equal("Bearer access-token", Assert.Single(request.Headers["Authorization"]));
        Assert.True(Guid.TryParse(Assert.Single(request.Headers["x-ms-correlation-id"]), out _));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("Nod Arena", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("TESTSB.42", body.RootElement.GetProperty("sandboxId").GetString());
        Assert.Equal(["xbox", "desktop"],
            body.RootElement.GetProperty("deviceFamilies").EnumerateArray().Select(value => value.GetString()));
        Assert.False(body.RootElement.TryGetProperty("serviceManagementId", out _));
    }

    [Fact]
    public async Task Create_OmitsUnspecifiedSandboxId()
    {
        var handler = Handler(Succeeded);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);
        Assert.Equal(0, result.ExitCode);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.False(body.RootElement.TryGetProperty("sandboxId", out _));
        Assert.False(body.RootElement.TryGetProperty("serviceManagementId", out _));
    }

    [Theory]
    [InlineData(true, "json")]
    [InlineData(true, "json-compact")]
    [InlineData(true, "JSON-COMPACT")]
    [InlineData(true, "Json-Compact")]
    [InlineData(true, "JSON")]
    [InlineData(false, "json")]
    [InlineData(false, "json-compact")]
    [InlineData(false, "JSON-COMPACT")]
    [InlineData(false, "Json-Compact")]
    [InlineData(false, "JSON")]
    public async Task JobOutput_DisplaysGameBeforeErrors(bool create, string format)
    {
        const string response =
            """{"jobId":"job-1","status":"completed","result":"succeeded","errors":[{"code":"enablementFailed","details":[{"code":"nested"}]}],"game":{"productId":"000123","xboxTitleId":"FFFFFFFF","SCID":"00000000-0000-0000-0000-0000FFFFFFFF","primaryDevelopmentSandbox":"TESTSB.42"},"extra":{"value":true}}""";
        var handler = create ? Handler(Accepted, response) : Handler(response);
        string[] args = create
            ? [.. CreateArguments, "--confirm", "--output", format]
            : ["game", "job", "get", "job-1", "--output", format];

        var result = await InvokeAsync(handler, args);

        Assert.Equal(0, result.ExitCode);
        using var actual = JsonDocument.Parse(result.Stdout);
        using var expected = JsonDocument.Parse(response);
        Assert.Equal(
            ["jobId", "status", "result", "game", "extra", "errors"],
            actual.RootElement.EnumerateObject().Select(property => property.Name));
        foreach (var property in expected.RootElement.EnumerateObject())
        {
            Assert.True(JsonElement.DeepEquals(
                property.Value, actual.RootElement.GetProperty(property.Name)));
        }
        Assert.Equal(
            string.Equals(format, "json", StringComparison.OrdinalIgnoreCase),
            result.Stdout.Trim().Contains('\n'));
    }

    [Theory]
    [InlineData(true, "json-compact")]
    [InlineData(true, "JSON-COMPACT")]
    [InlineData(true, "Json-Compact")]
    [InlineData(false, "json-compact")]
    [InlineData(false, "JSON-COMPACT")]
    [InlineData(false, "Json-Compact")]
    public async Task MalformedJobResponse_HonorsCompactOutputCaseInsensitively(bool create, string format)
    {
        const string body = """
            {
              "jobId": "job-1",
              "status": false
            }
            """;
        string[] args = create
            ? [.. CreateArguments, "--confirm", "--output", format]
            : ["game", "job", "get", "job-1", "--output", format];

        var result = await InvokeAsync(Handler(body), args);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(
            """{"jobId":"job-1","status":false}""" + Environment.NewLine,
            result.Stderr);
    }

    [Theory]
    [InlineData("""{"jobId":"job-1","errors":null,"game":null}""",
        """{"jobId":"job-1","game":null,"errors":null}""")]
    [InlineData("""{"jobId":"job-1","errors":null}""",
        """{"jobId":"job-1","errors":null}""")]
    [InlineData("""{"jobId":"job-1","game":null}""",
        """{"jobId":"job-1","game":null}""")]
    [InlineData("""{"jobId":"job-1"}""", """{"jobId":"job-1"}""")]
    [InlineData(
        """{"jobId":"job-1","game":{"xboxTitleId":null,"SCID":null,"primaryDevelopmentSandbox":null}}""",
        """{"jobId":"job-1","game":{"xboxTitleId":null,"SCID":null,"primaryDevelopmentSandbox":null}}""")]
    public async Task JobOutput_PreservesNullAndOmittedFields(string response, string expected)
    {
        var result = await InvokeAsync(
            Handler(response), ["game", "job", "get", "job-1", "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.Stdout.Trim());
    }

    [Theory]
    [InlineData(true, "json")]
    [InlineData(true, "json-compact")]
    [InlineData(false, "json")]
    [InlineData(false, "json-compact")]
    public async Task JobOutput_PreservesDeviceFamilies(bool create, string format)
    {
        foreach (var fields in new[]
        {
            "",
            "\"deviceFamilies\":null",
            "\"deviceFamilies\":[]",
            "\"deviceFamilies\":[\"xbox\",\"desktop\"]",
            "\"deviceFamilies\":[\"FutureDevice\",\"Desktop\",\"xbox\",\"FutureDevice\"]",
        })
        {
            var response =
                """{"jobId":"job-1","status":"completed","result":"succeeded","game":{""" + fields + "}}";
            var handler = create ? Handler(Accepted, response) : Handler(response);
            string[] args = create
                ? [.. CreateArguments, "--confirm", "--output", format]
                : ["game", "job", "get", "job-1", "--output", format];

            var result = await InvokeAsync(handler, args);

            Assert.Equal(0, result.ExitCode);
            using var output = JsonDocument.Parse(result.Stdout);
            using var expected = JsonDocument.Parse(response);
            Assert.True(JsonElement.DeepEquals(expected.RootElement, output.RootElement));
        }
    }

    public static TheoryData<string[]> InvalidInputs => new(new string[][]
    {
        ["game", "create"],
        ["game", "create", "--name", "Game"],
        ["game", "create", "--supported-device", "xbox"],
        ["game", "create", "Game", "xbox"],
        [.. CreateArguments, "--supported-device", "mobile"],
        [.. CreateArguments, "--supported-device", "9999"],
        ["game", "job", "get"],
        ["game", "job", "get", " "],
        ["game", "job", "get", "job-1", "--poll-interval", "5"],
    });

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task InvalidInputs_FailBeforeAuthentication(string[] args)
    {
        var handler = Handler(Succeeded);
        var auth = CreateAuth();
        var result = await InvokeAsync(handler, args, auth: auth);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        auth.Verify(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("--service-management-id", "tree-id")]
    [InlineData("--no-wait", null)]
    [InlineData("--wait-timeout", "120")]
    [InlineData("--poll-interval", "5")]
    public async Task RemovedOptions_AreRejectedBeforeAuthentication(string option, string? value)
    {
        var handler = Handler(Succeeded);
        var auth = CreateAuth();
        var args = new List<string>(CreateArguments) { "--confirm", option };
        if (value != null)
        {
            args.Add(value);
        }

        var result = await InvokeAsync(handler, args.ToArray(), auth: auth);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(option, result.Stderr);
        Assert.Empty(handler.Requests);
        auth.Verify(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task InvalidSeller_FailsBeforeAuthentication(string sellerId)
    {
        var handler = Handler(Succeeded);
        var auth = CreateAuth();
        var result = await InvokeAsync(
            handler, [.. CreateArguments, "--confirm"], auth: auth, sellerId: sellerId);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        auth.Verify(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Game")]
    public async Task Create_DoesNotValidateNameOrSandboxLocally(string name)
    {
        var sandbox = new string('s', 257);
        var handler = Handler(Succeeded);
        var result = await InvokeAsync(handler,
            ["game", "create", "--name", name, "--supported-device", "xbox",
                "--sandbox-id", sandbox, "--confirm"]);

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal(name.Trim(), json.RootElement.GetProperty("name").GetString());
        Assert.Equal(sandbox, json.RootElement.GetProperty("sandboxId").GetString());
    }

    [Fact]
    public async Task Create_ForwardsLongNamesAndOpaqueSellerIds()
    {
        var name = new string('n', 201);
        var seller = "seller/ " + new string('s', 129);
        var handler = Handler(Succeeded);
        var result = await InvokeAsync(handler,
            ["game", "create", "--name", name, "--supported-device", "desktop", "--confirm"],
            sellerId: $" {seller} ");

        Assert.Equal(0, result.ExitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(seller, Assert.Single(request.Headers["X-DeveloperAccountId"]));
        using var json = JsonDocument.Parse(request.Body!);
        Assert.Equal(name, json.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Create_LeavesDuplicateDeviceValidationToService()
    {
        var handler = Handler(Succeeded);
        var result = await InvokeAsync(handler,
            [.. CreateArguments, "--supported-device", "XBOX", "--confirm"]);

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("""["xbox","xbox"]""", json.RootElement.GetProperty("deviceFamilies").GetRawText());
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("http://gateway.example")]
    public async Task InvalidGateway_FailsBeforeAuthentication(string baseUrl)
    {
        var handler = Handler(Succeeded);
        var auth = CreateAuth();
        var result = await InvokeAsync(
            handler, [.. CreateArguments, "--confirm"], auth: auth, baseUrl: baseUrl);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        auth.Verify(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Timeout_PreservesJobIdAndCorrelationBeforeReturning()
    {
        var handler = Handler(Enumerable.Repeat(Running, 25).ToArray());
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Contains(
            $"Game creation job submitted: job-1 (Correlation ID: response-correlation){Environment.NewLine}",
            result.Stderr);
        Assert.Contains("game job get job-1 --seller-id seller-1 --base-url https://gateway.example", result.Stderr);
        Assert.DoesNotContain("x-ms-correlation-id: response-correlation", result.Stderr);
        using var body = JsonDocument.Parse(result.Stdout);
        Assert.Equal("running", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Timeout_RecoveryCommandKeepsTheProfile()
    {
        var handler = Handler(Enumerable.Repeat(Running, 25).ToArray());
        var result = await InvokeAsync(handler, ["--profile", "default", .. CreateArguments, "--confirm"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "xbox --profile default game job get job-1 --seller-id seller-1 --base-url https://gateway.example",
            result.Stderr);
    }

    [Fact]
    public async Task Polling_SubmitsExactlyOnceAndFinishes()
    {
        var handler = Handler(Running, Running, Succeeded);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm", "--verbose"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Contains("job job-1 completed.", result.Stderr);
        Assert.Contains("POST https://gateway.example/xboxpartner/v1/games", result.Stderr);
        Assert.Contains("GET https://gateway.example/xboxpartner/v1/jobs/job-1", result.Stderr);
        Assert.Contains("succeeded", result.Stdout);
        Assert.DoesNotContain("\r", result.Stderr.Replace(Environment.NewLine, "\n"));
    }

    [Fact]
    public async Task Polling_DoesNotPrintStatusRequestCorrelationId()
    {
        var completed = Response(Succeeded);
        completed.Headers.Remove("x-ms-correlation-id");
        completed.Headers.Add("x-ms-correlation-id", "poll-correlation");
        var handler = new XboxPartnerHttpMessageHandler(
            Response(Accepted, HttpStatusCode.Accepted), completed);

        var result = await InvokeAsync(
            handler, [.. CreateArguments, "--confirm", "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Succeeded, result.Stdout.Trim());
        Assert.Contains(
            "Game creation job submitted: job-1 (Correlation ID: response-correlation)",
            result.Stderr);
        Assert.DoesNotContain("x-ms-correlation-id:", result.Stderr);
        Assert.DoesNotContain("poll-correlation", result.Stderr);
    }

    [Theory]
    [InlineData(Succeeded, 0)]
    [InlineData(SucceededWithoutDetails, 0)]
    [InlineData(SucceededWithOmittedFields, 0)]
    [InlineData(SucceededWithErrors, 0)]
    [InlineData(SucceededWithFullSummary, 0)]
    [InlineData(Failed, 2)]
    public async Task Create_PollsAcceptedJobAndPreservesUpdatedContract(string completed, int exitCode)
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(Accepted, HttpStatusCode.Accepted),
            Response(Running),
            Response(completed));
        var result = await InvokeAsync(
            handler, [.. CreateArguments, "--confirm", "--output", "json-compact"]);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(completed, result.Stdout.Trim());
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.All(handler.Requests.Skip(1), request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/xboxpartner/v1/jobs/job-1", request.RequestUri!.AbsolutePath);
        });
        Assert.DoesNotContain("could not be verified", result.Stderr);
    }

    [Fact]
    public async Task Create_AcceptsOmittedGameAndErrorsThroughoutPolling()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response("""{"jobId":"job-1","status":"notStarted","result":"pending"}""", HttpStatusCode.Accepted),
            Response("""{"jobId":"job-1","status":"running","result":"pending"}"""),
            Response(SucceededWithOmittedFields));

        var result = await InvokeAsync(
            handler, [.. CreateArguments, "--confirm", "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(SucceededWithOmittedFields, result.Stdout.Trim());
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Progress_CoversSubmissionAndRemainsVisibleThroughoutPolling()
    {
        var handler = new ProgressObservingHandler();
        var result = await InvokeAsync(
            handler, [.. CreateArguments, "--confirm", "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, handler.Count);
        Assert.Equal(Succeeded, result.Stdout.Trim());
        Assert.True(result.Stderr.IndexOf("Submitting game creation job...", StringComparison.Ordinal)
            < result.Stderr.IndexOf("[request 1 started]", StringComparison.Ordinal));
        Assert.True(result.Stderr.IndexOf("[request 1 completed]", StringComparison.Ordinal)
            < result.Stderr.IndexOf("Game creation job submitted: job-1", StringComparison.Ordinal));

        const string message = "Waiting for game creation job job-1...";
        var waiting = result.Stderr.IndexOf(message, StringComparison.Ordinal);
        Assert.True(waiting >= 0);
        Assert.True(waiting < result.Stderr.IndexOf("[request 2 started]", StringComparison.Ordinal));
        Assert.Equal(waiting, result.Stderr.LastIndexOf(message, StringComparison.Ordinal));
        Assert.DoesNotContain("\r", result.Stderr.Replace(Environment.NewLine, "\n"));
        Assert.DoesNotContain("\u001b", result.Stderr[waiting..], StringComparison.Ordinal);
        Assert.True(result.Stderr.IndexOf("[request 3 completed]", StringComparison.Ordinal)
            < result.Stderr.IndexOf("job job-1 completed.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Timeout_UsesFixedTwoMinuteWaitAndFiveSecondIntervalWithoutResubmitting()
    {
        var handler = Handler(Enumerable.Repeat(Running, 25).ToArray());
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(24, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Contains("still running after 120 seconds", result.Stderr);
        Assert.Contains("game job get job-1", result.Stderr);
    }

    [Fact]
    public async Task PollingFailure_ReportsAcceptedSubmissionAndRecovery()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(Running), Response("""{"title":"boom"}""", HttpStatusCode.InternalServerError));
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Contains("submitted, but its status could not be read", result.Stderr);
        Assert.Contains("game job get job-1", result.Stderr);
        Assert.Contains("response-correlation", result.Stderr);
    }

    [Theory]
    [InlineData("""{"jobId":"job-1","status":true}""")]
    [InlineData("<html>bad response</html>")]
    public async Task MalformedSubmission_FailsWithGameListRecovery(string body)
    {
        var handler = Handler(body);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Single(handler.Requests);
        Assert.Contains("Do not resubmit", result.Stderr);
        Assert.Contains("game list", result.Stderr);
        Assert.DoesNotContain("game job get", result.Stderr);
        Assert.Contains("response-correlation", result.Stderr);
    }

    [Fact]
    public async Task TerminalFailure_UsesExitTwo()
    {
        var handler = Handler(Failed);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("failed", result.Stdout);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(Accepted, 0)]
    [InlineData(Running, 0)]
    [InlineData(Succeeded, 0)]
    [InlineData(SucceededWithoutDetails, 0)]
    [InlineData(SucceededWithOmittedFields, 0)]
    [InlineData(SucceededWithErrors, 0)]
    [InlineData(SucceededWithFullSummary, 0)]
    [InlineData(Failed, 2)]
    public async Task JobGet_ReturnsStructuredResponseAndCorrectExit(
        string response, int exitCode)
    {
        var handler = Handler(response);
        var result = await InvokeAsync(handler,
            ["game", "job", "get", "job /? id", "--output", "json-compact", "--data-only"]);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(response, result.Stdout.Trim());
        Assert.DoesNotContain("x-ms-correlation-id", result.Stderr);
        Assert.DoesNotContain("response-correlation", result.Stderr);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith("/xboxpartner/v1/jobs/job%20%2F%3F%20id", request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task JobGet_RetrievalFailureIsDistinctFromTerminalFailure()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response("""{"title":"no such job"}""", HttpStatusCode.NotFound));
        var result = await InvokeAsync(handler, ["game", "job", "get", "job-1", "--verbose"]);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("no such job", result.Stderr);
        Assert.Contains("response-correlation", result.Stderr);
        Assert.DoesNotContain("seller endpoint", result.Stderr);
        Assert.DoesNotContain("Check its status", result.Stderr);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("get")]
    public async Task UndeclaredTableOutput_IsRejectedBeforeAnyRequest(string action)
    {
        var handler = Handler(Succeeded);
        string[] args = action == "create"
            ? [.. CreateArguments, "--confirm", "--output", "table"]
            : ["game", "job", action, "job-1", "--output", "table"];
        var result = await InvokeAsync(handler, args, throughMain: true);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("job")]
    [InlineData("job get")]
    public async Task HelpRoutes_AreEquivalent(string path)
    {
        var segments = path.Split(' ');
        var first = await InvokeAsync(
            Handler(Succeeded), ["game", .. segments, "--help"], addScope: false);
        var second = await InvokeAsync(
            Handler(Succeeded), ["help", "game", .. segments], addScope: false);
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.Stdout, second.Stdout);
        Assert.DoesNotContain("/xboxpartner/", first.Stdout);
        Assert.DoesNotContain("Calls:", first.Stdout);
        if (path == "create")
        {
            Assert.Contains("Mutates production state", first.Stdout);
            Assert.Contains("Agent approval is still required",
                string.Join(' ', first.Stdout.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
            Assert.Contains("120", first.Stdout);
            Assert.DoesNotContain("--service-management-id", first.Stdout);
            Assert.DoesNotContain("--no-wait", first.Stdout);
            Assert.DoesNotContain("--wait-timeout", first.Stdout);
            Assert.DoesNotContain("--poll-interval", first.Stdout);
        }
    }

    /// <summary>
    /// <c>XBOX_SELLER_ID</c> names the seller without a sign-in lookup. It isn't on the command
    /// line, so the irreversible create says which seller it uses and where it came from, even
    /// confirmed.
    /// </summary>
    [Fact]
    public async Task EnvironmentSeller_IsUsedWithoutIdentityLookupAndNamed()
    {
        var previous = SetSellerVariable("2222222");
        try
        {
            var handler = Handler(Succeeded);
            var auth = CreateAuth();
            var result = await InvokeAsync(
                handler, [.. CreateArguments, "--confirm"], sellerId: null, auth: auth);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("2222222",
                Assert.Single(Assert.Single(handler.Requests).Headers["X-DeveloperAccountId"]));
            Assert.StartsWith("Using --seller-id 2222222 from XBOX_SELLER_ID." + Environment.NewLine, result.Stderr);
            auth.Verify(service => service.GetCachedAccountAsync(It.IsAny<CancellationToken>()), Times.Never);
            auth.Verify(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            RestoreSellerVariable(previous);
        }
    }

    /// <summary>
    /// <c>--seller-id</c> wins over <c>XBOX_SELLER_ID</c> and the saved seller, without a sign-in
    /// lookup, and isn't announced the way an implicit seller is.
    /// </summary>
    [Fact]
    public async Task SellerFlag_WinsOverTheEnvironmentAndSavedSellerWithoutIdentityLookup()
    {
        var previous = SetSellerVariable("2222222");
        var directory = await SaveSellerAsync();
        try
        {
            var handler = Handler(Succeeded);
            var auth = CreateAuth();
            var result = await InvokeAsync(
                handler, [.. CreateArguments, "--confirm"], auth: auth, stateDirectory: directory);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("seller-1",
                Assert.Single(Assert.Single(handler.Requests).Headers["X-DeveloperAccountId"]));
            Assert.DoesNotContain("Using --seller-id", result.Stderr);
            auth.Verify(service => service.GetCachedAccountAsync(It.IsAny<CancellationToken>()), Times.Never);
            auth.Verify(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            RestoreSellerVariable(previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedSeller_IsResolvedByIdentityAndGateway(bool saved)
    {
        var previous = SetSellerVariable(null);
        var directory = saved
            ? await SaveSellerAsync()
            : Path.Combine(Path.GetTempPath(), $"game-seller-tests-{Guid.NewGuid():N}");
        try
        {
            var handler = Handler(Succeeded);
            var result = await InvokeAsync(handler,
                [.. CreateArguments, "--confirm"],
                sellerId: null, stateDirectory: directory);
            Assert.Equal(saved ? 0 : 1, result.ExitCode);
            if (!saved)
            {
                Assert.Contains(
                    "No seller-id is set. Pass --seller-id, set XBOX_SELLER_ID, or run "
                    + "'xbox context set seller-id <value>'.",
                    result.Stderr);
                Assert.DoesNotContain("Creating the game", result.Stderr);
                Assert.Empty(handler.Requests);
            }
            else
            {
                Assert.Equal("7654321",
                    Assert.Single(Assert.Single(handler.Requests).Headers["X-DeveloperAccountId"]));

                // The seller isn't on the command line, so the irreversible create says which one
                // it uses and where it came from, on stderr, even with --confirm.
                Assert.StartsWith("Using --seller-id 7654321 from saved context." + Environment.NewLine, result.Stderr);
            }
        }
        finally
        {
            RestoreSellerVariable(previous);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Signed out with no seller from anywhere, there's nothing saved to fall back on and
    /// <c>context set</c> would need a sign-in too, so the error says to sign in.
    /// </summary>
    [Fact]
    public async Task NoSeller_NotSignedInAsksForASignIn()
    {
        var previous = SetSellerVariable(null);
        try
        {
            var handler = Handler(Succeeded);
            var auth = CreateAuth(signedIn: false);
            var result = await InvokeAsync(
                handler, [.. CreateArguments, "--confirm"], sellerId: null, auth: auth);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Not signed in. Run 'xbox auth login' first.", result.Stderr);
            Assert.DoesNotContain("Creating the game", result.Stderr);
            Assert.Empty(handler.Requests);
            auth.Verify(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            RestoreSellerVariable(previous);
        }
    }

    [Fact]
    public async Task Recovery_PreservesAuthenticationOverrides()
    {
        var handler = Handler(Enumerable.Repeat(Running, 25).ToArray());
        var result = await InvokeAsync(handler,
            [.. CreateArguments, "--confirm", "--client-id", "client-override",
                "--tenant-id", "tenant-override", "--scopes", "api://scope/.default"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("xbox --client-id client-override --tenant-id tenant-override " +
            "--scopes api://scope/.default game job get job-1", result.Stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_DoesNotResubmitAndReportsRecovery(bool afterSubmission)
    {
        var handler = new CancelingHandler(afterSubmission);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(afterSubmission ? 2 : 1, handler.Count);
        Assert.Empty(result.Stdout);
        Assert.Contains(afterSubmission ? "game job get job-1" : "game list", result.Stderr);
        Assert.Contains(afterSubmission ? "Stopped waiting" : "Do not resubmit", result.Stderr);
    }

    private static XboxPartnerHttpMessageHandler Handler(params string[] responses) =>
        new(responses.Select(response => Response(response)).ToArray());

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        response.Headers.Add("x-ms-correlation-id", "response-correlation");
        return response;
    }

    /// <summary>
    /// A sign-in whose token cache holds tenant <c>test-tenant</c>, user <c>user</c>, the
    /// identity saved sellers are looked up for; signed out when <paramref name="signedIn"/> is
    /// <c>false</c>.
    /// </summary>
    private static Mock<AuthenticationService> CreateAuth(bool signedIn = true)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AzureAd:ClientId"] = "test-client",
                ["AzureAd:TenantId"] = "test-tenant",
                ["AzureAd:Scopes"] = "api://test/.default",
            }).Build();
        var account = Mock.Of<IAccount>(
            account => account.HomeAccountId == new AccountId("id", "user", "test-tenant"));
        var auth = new Mock<AuthenticationService>(configuration);
        auth.Setup(service => service.GetCachedAccountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(signedIn ? account : null);
        auth.Setup(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(signedIn
            ? new AuthenticationResult(
                "access-token", false, "user", DateTimeOffset.UtcNow.AddHours(1),
                DateTimeOffset.UtcNow.AddHours(1), "test-tenant", account,
                "id-token", ["api://test/.default"], Guid.NewGuid())
            : null);
        return auth;
    }

    /// <summary>
    /// Sets <c>XBOX_SELLER_ID</c> to <paramref name="value"/>, or unsets it, and unsets
    /// <c>XBOX_PROFILE</c>, returning what they were so <see cref="RestoreSellerVariable"/> can put
    /// them back.
    /// </summary>
    private static (string? Seller, string? Profile) SetSellerVariable(string? value)
    {
        var previous = (
            Environment.GetEnvironmentVariable("XBOX_SELLER_ID"),
            Environment.GetEnvironmentVariable(ContextProfiles.EnvironmentVariable));
        Environment.SetEnvironmentVariable("XBOX_SELLER_ID", value);
        Environment.SetEnvironmentVariable(ContextProfiles.EnvironmentVariable, null);
        return previous;
    }

    private static void RestoreSellerVariable((string? Seller, string? Profile) previous)
    {
        Environment.SetEnvironmentVariable("XBOX_SELLER_ID", previous.Seller);
        Environment.SetEnvironmentVariable(ContextProfiles.EnvironmentVariable, previous.Profile);
    }

    /// <summary>
    /// Saves seller 7654321 for test-tenant/user on the test gateway, in a new state directory,
    /// and returns the directory.
    /// </summary>
    private static async Task<string> SaveSellerAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"game-seller-tests-{Guid.NewGuid():N}");
        await new ContextStore(Path.Combine(directory, ContextStore.FileName)).UpdateAsync(
            new SignedInIdentity("test-tenant", "user"),
            [new ContextChange("seller-id", "https://gateway.example", "7654321")]);
        return directory;
    }

    private static async Task<CapturedInvocation> InvokeAsync(
        HttpMessageHandler handler,
        string[] arguments,
        bool interactive = false,
        string input = "",
        Mock<AuthenticationService>? auth = null,
        string? sellerId = "seller-1",
        string baseUrl = "https://gateway.example",
        bool addScope = true,
        bool throughMain = false,
        string? stateDirectory = null)
    {
        auth ??= CreateAuth();
        using var httpClient = new HttpClient(handler);
        var clock = DateTimeOffset.UnixEpoch;
        var dependencies = new XboxPartnerJobDependencies(
            (delay, token) =>
            {
                token.ThrowIfCancellationRequested();
                clock += delay;
                return Task.CompletedTask;
            },
            () => clock,
            () => interactive);

        var root = Program.BuildRootCommand(gameCommandFactory: () => GameCommandFactory.Create(
            configuration => new XboxPartnerClient(configuration, httpClient, auth.Object),
            dependencies));
        var args = new List<string>(arguments);
        if (addScope)
        {
            args.AddRange(["--base-url", baseUrl]);
            if (sellerId != null)
            {
                args.AddRange(["--seller-id", sellerId]);
            }
        }

        var oldOut = Console.Out;
        var oldError = Console.Error;
        var oldIn = Console.In;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var stdin = new StringReader(input);
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Console.SetIn(stdin);

            // Context values come from the state directory given, for the account auth signs in as.
            ContextResolver.ReadIdentityAsync = _ => SignedInIdentity.FromCacheAsync(auth.Object, CancellationToken.None);
            if (stateDirectory is not null)
            {
                ContextStore.DefaultDirectory = () => stateDirectory;
            }

            var parseResult = throughMain
                ? null
                : await Program.ResolveContextAsync(root.Parse(args.ToArray(), Program.NoResponseFileConfig), args);
            var exitCode = throughMain
                ? await Program.Main(args.ToArray())
                : parseResult is null ? 1 : await parseResult.InvokeAsync();
            return new CapturedInvocation(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            Console.SetIn(oldIn);
            TestEnvironment.Reset();
        }
    }

    private sealed record CapturedInvocation(int ExitCode, string Stdout, string Stderr);

    private sealed class ProgressObservingHandler : HttpMessageHandler
    {
        internal int Count { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Console.Error.Write($"[request {Count} started]");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Console.Error.Write($"[request {Count} completed]");
            return Response(Count < 3 ? Running : Succeeded);
        }
    }

    private sealed class CancelingHandler(bool afterSubmission) : HttpMessageHandler
    {
        internal int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            if (afterSubmission && Count == 1)
            {
                return Task.FromResult(Response(Running));
            }

            throw new TaskCanceledException("Simulated interrupted request.");
        }
    }
}
