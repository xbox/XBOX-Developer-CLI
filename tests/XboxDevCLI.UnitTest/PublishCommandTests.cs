// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Output;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class PublishCommandTests
{
    private const string Scid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    [Theory]
    [InlineData("XDKS.1", "XDKS.1", false, 0)]
    [InlineData("XDKS.1", "XDKS.1", true, 0)]
    [InlineData("RETAIL", "RETAIL", false, 1)]
    [InlineData("CERT", "CERT", false, 1)]
    [InlineData("cert.debug", "cert.debug", false, 1)]
    [InlineData("HIDDEN", "HIDDEN", false, 1)]
    [InlineData("XDKS.1", "TESTSB.42", false, 1)]
    [InlineData("xdks.1", "XDKS.1", false, 0)]
    [InlineData("XDKS.1", "RETAIL", false, 2)]
    [InlineData("XDKS.1", "RETAIL", true, 0)]
    [InlineData("CERT", "RETAIL", false, 2)]
    public void DescribeHighImpact_FlagsGlobalTitleWideAndCrossSandboxPublishes(
        string source,
        string destination,
        bool validateOnly,
        int reasons)
    {
        IReadOnlyList<string> described = PublishCommandFactory.DescribeHighImpact(
            Request(destination, source, validateOnly: validateOnly));

        Assert.Equal(reasons, described.Count);
    }

    [Fact]
    public void DescribeHighImpact_SaysWhatEachDestinationMeans()
    {
        Assert.Contains("players see the change", Assert.Single(
            PublishCommandFactory.DescribeHighImpact(Request("RETAIL"))), StringComparison.Ordinal);
        Assert.Contains("certification", Assert.Single(
            PublishCommandFactory.DescribeHighImpact(Request("CERT.DEBUG"))), StringComparison.Ordinal);
        Assert.Contains("title-wide", Assert.Single(
            PublishCommandFactory.DescribeHighImpact(Request("HIDDEN"))), StringComparison.Ordinal);
        Assert.Equal(
            "Publishing from XDKS.1 first replaces TESTSB.42's working documents that have the same names.",
            Assert.Single(PublishCommandFactory.DescribeHighImpact(Request("TESTSB.42", "XDKS.1"))));
    }

    [Fact]
    public async Task Confirm_GoesAheadWithoutAskingWhenNothingIsFlaggedOrConfirmed()
    {
        var prompt = new Prompt(interactive: false);
        using var stderr = new StringWriter();

        Assert.True(await PublishCommandFactory.ConfirmAsync(Request("XDKS.1"), false, prompt.Dependencies, stderr, CancellationToken.None));
        Assert.True(await PublishCommandFactory.ConfirmAsync(Request("RETAIL"), true, prompt.Dependencies, stderr, CancellationToken.None));

        Assert.Empty(stderr.ToString());
        Assert.Equal(0, prompt.Reads);
    }

    [Fact]
    public async Task Confirm_WithoutATerminal_RefusesAndSaysHowToConfirm()
    {
        var prompt = new Prompt(interactive: false);
        using var stderr = new StringWriter();

        bool confirmed = await PublishCommandFactory.ConfirmAsync(
            Request("RETAIL", "XDKS.1", configSet: 42, republish: true), false, prompt.Dependencies, stderr, CancellationToken.None);

        Assert.False(confirmed);
        Assert.Equal(0, prompt.Reads);
        Assert.Equal(
            [
                $"This publishes working version 42 of XDKS.1's configuration to RETAIL for SCID {Scid}.",
                "RETAIL is live: players see the change when the publish finishes.",
                "Publishing from XDKS.1 first replaces RETAIL's working documents that have the same names.",
                "--republish publishes in full even if nothing changed, and forces the selected configuration, by default RETAIL's latest working one, onto a locked RETAIL.",
                "Warning: publishing from XDKS.1 to RETAIL replaces RETAIL's global title storage files with XDKS.1's. " +
                    "Files with the same path are overwritten, files only XDKS.1 holds are added, and files only RETAIL " +
                    "holds are deleted. Deleted files can't be recovered.",
                "Re-run with --confirm (--yes, -y) to publish without an interactive prompt.",
                "Run with --validate-only first to see which achievements this publish adds to RETAIL.",
            ],
            Lines(stderr));
    }

    [Theory]
    [InlineData("y", true)]
    [InlineData(" YES ", true)]
    [InlineData("n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task Confirm_AtATerminal_PublishesOnlyOnYes(string? answer, bool expected)
    {
        var prompt = new Prompt(interactive: true, answer);
        using var stderr = new StringWriter();

        bool confirmed = await PublishCommandFactory.ConfirmAsync(Request("CERT"), false, prompt.Dependencies, stderr, CancellationToken.None);

        Assert.Equal(expected, confirmed);
        Assert.Equal(1, prompt.Reads);
        string text = stderr.ToString();
        Assert.Contains($"This publishes CERT's latest working configuration to CERT for SCID {Scid}.", text, StringComparison.Ordinal);
        Assert.Contains(Environment.NewLine + Environment.NewLine + "Publish to CERT? [y/N]: ", text, StringComparison.Ordinal);
        Assert.Equal(!expected, text.Contains("Publish canceled.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirm_WithTitleId_NamesTheTitleInsteadOfAnEmptyScid()
    {
        var prompt = new Prompt(interactive: false);
        using var stderr = new StringWriter();
        var request = new XboxLivePublishRequest(null, "RETAIL", "RETAIL", false, null, TitleId: "0000002A");

        Assert.False(await PublishCommandFactory.ConfirmAsync(request, false, prompt.Dependencies, stderr, CancellationToken.None));

        string text = stderr.ToString();
        Assert.Contains("This publishes RETAIL's latest working configuration to RETAIL for title 0000002A.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("for SCID", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_ToRetailWithoutATerminal_RefusesBeforeAnyRequest()
    {
        var run = await RunAsync(new Prompt(interactive: false), "--sandbox-id", "RETAIL", "--source-sandbox-id", "XDKS.1");

        Assert.Equal(1, run.ExitCode);
        Assert.Null(run.Handler.RequestBody);
        Assert.Empty(run.Stdout);
        Assert.Contains("Re-run with --confirm (--yes, -y)", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_CrossSandboxAnsweredYes_ShowsTheStorageWarningOnceBeforeAsking()
    {
        var prompt = new Prompt(interactive: true, "y");
        var run = await RunAsync(prompt, "--source-sandbox-id", "XDKS.1", "--sandbox-id", "TESTSB.42");

        Assert.Equal(0, run.ExitCode);
        Assert.NotNull(run.Handler.RequestBody);
        int warning = run.Stderr.IndexOf("global title storage files", StringComparison.Ordinal);
        Assert.InRange(warning, 0, run.Stderr.IndexOf("Publish to TESTSB.42? [y/N]: ", StringComparison.Ordinal));
        Assert.Equal(1, Occurrences(run.Stderr, "global title storage files"));
    }

    [Fact]
    public async Task Command_CrossSandboxConfirmed_ShowsTheStorageWarningOnce()
    {
        var run = await RunAsync(new Prompt(interactive: false), "--source-sandbox-id", "XDKS.1", "--sandbox-id", "TESTSB.42", "--confirm");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(1, Occurrences(run.Stderr, "global title storage files"));
        Assert.DoesNotContain("This publishes", run.Stderr, StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    [Fact]
    public async Task Command_AnsweredNo_PublishesNothing()
    {
        var prompt = new Prompt(interactive: true, "n");
        var run = await RunAsync(prompt, "--sandbox-id", "HIDDEN");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(1, prompt.Reads);
        Assert.Null(run.Handler.RequestBody);
        Assert.Contains("Publish canceled.", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirm_ForHidden_DescribesTheTitleWideConfiguration()
    {
        var prompt = new Prompt(interactive: false);
        using var stderr = new StringWriter();

        await PublishCommandFactory.ConfirmAsync(
            Request("HIDDEN"), false, prompt.Dependencies, stderr, CancellationToken.None);

        Assert.StartsWith(
            $"This publishes the latest title-wide configuration (the working documents in ALL) to HIDDEN for SCID {Scid}.",
            stderr.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HIDDEN", "--source-sandbox-id ALL", "--source-sandbox-id")]
    [InlineData("hidden", "--configset 14", "--configset")]
    [InlineData("Hidden", "--source-sandbox-id HIDDEN --configset 14", "--source-sandbox-id and --configset")]
    public void Parse_RejectsASourceOrVersionOnATitleWidePublish(string sandbox, string options, string remove)
    {
        ParseResult result = Program.BuildRootCommand().Parse(
            $"xbl config publish --scid {Scid} --sandbox-id {sandbox} {options}",
            Program.NoResponseFileConfig);

        Assert.Contains(
            result.Errors,
            error => error.Message ==
                "A title-wide (HIDDEN) publish always publishes the latest ALL working configuration, " +
                $"so it takes no source or version. Remove {remove}.");
    }

    [Theory]
    [InlineData("x")]
    [InlineData("-1")]
    [InlineData("99999999999999999999")]
    public void Parse_ReportsAConfigsetThatIsNotANumberAsAParseError(string configSet)
    {
        ParseResult result = Program.BuildRootCommand().Parse(
            ["xbl", "config", "publish", "--scid", Scid, "--sandbox-id", "XDKS.1", "--configset", configSet],
            Program.NoResponseFileConfig);

        Assert.Contains(result.Errors, error => error.Message.Contains($"'{configSet}'", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RejectsAConfigsetOfZero()
    {
        ParseResult result = Program.BuildRootCommand().Parse(
            $"xbl config publish --scid {Scid} --sandbox-id XDKS.1 --configset 0",
            Program.NoResponseFileConfig);

        Assert.Contains(result.Errors, error => error.Message == "--configset must be positive when provided.");
    }

    [Theory]
    [InlineData("--sandbox-id HIDDEN")]
    [InlineData("--sandbox-id HIDDEN --validate-only")]
    [InlineData("--sandbox-id XDKS.1 --source-sandbox-id STAGE.0 --configset 14")]
    public void Parse_AllowsTitleWideWithoutASourceOrVersion(string options)
    {
        ParseResult result = Program.BuildRootCommand().Parse(
            $"xbl config publish --scid {Scid} {options}",
            Program.NoResponseFileConfig);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Command_TitleWideWithAVersion_FailsBeforeAnyRequest()
    {
        var prompt = new Prompt(interactive: false);
        var run = await RunAsync(prompt, "--sandbox-id", "HIDDEN", "--configset", "14", "--confirm");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(0, prompt.Reads);
        Assert.Null(run.Handler.RequestBody);
        Assert.Contains("Remove --configset.", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_CanceledWhileWaitingForTheAnswer_PublishesNothing()
    {
        var prompt = new Prompt(interactive: true, blocks: true);
        using var cancellation = new CancellationTokenSource();
        _ = prompt.Asked.Task.ContinueWith(
            _ => cancellation.Cancel(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var run = await RunAsync(
                prompt,
                new CapturingHandler(HttpStatusCode.OK, RetailValidation),
                [],
                cancellation.Token,
                ["--sandbox-id", "RETAIL"])
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(1, prompt.Reads);
        using JsonDocument validation = JsonDocument.Parse(Assert.Single(run.Handler.Bodies));
        Assert.True(validation.RootElement.GetProperty("validateOnly").GetBoolean());
        Assert.Empty(run.Stdout);
        Assert.EndsWith("Publish canceled." + Environment.NewLine, run.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, """{"Status":"Running"}""", "The service didn't report which achievements this publish adds to RETAIL.")]
    [InlineData(true, """{"achievementChanges":{"added":[]}}""", "This publish would add no achievements to RETAIL.")]
    [InlineData(false, """{"achievementChanges":{"added":[]}}""", "This publish adds no achievements to RETAIL.")]
    [InlineData(false, """{"achievementChanges":{"added":["1"]}}""", "This publish adds 1 achievement to RETAIL: 1. Once they're live in RETAIL, achievements can't be removed, and their unlock rules and gamerscore can't change.")]
    [InlineData(true, """{"achievementChanges":{"added":["1",null,"2\u001b"]}}""", "This publish would add 2 achievements to RETAIL: 1, 2. Once they're live in RETAIL, achievements can't be removed, and their unlock rules and gamerscore can't change.")]
    public void FormatAchievementChanges_SaysWhatThePublishAdds(
        bool validateOnly,
        string response,
        string expected)
    {
        XboxLivePublishResponse result = JsonSerializer.Deserialize<XboxLivePublishResponse>(response)!;

        Assert.Equal(expected, PublishCommandFactory.FormatAchievementChanges(result, validateOnly));
    }

    [Theory]
    [InlineData("RETAIL", true, true)]
    [InlineData("retail", true, true)]
    [InlineData("CERT", true, false)]
    [InlineData("RETAIL", false, false)]
    public async Task RunAsync_ValidatingRetail_ListsTheAchievementsItWouldAdd(string sandbox, bool validateOnly, bool listed)
    {
        (int exitCode, string stdout, string stderr) = await RunDirectAsync(
            """{"Status":"Succeeded","SourceVersion":"52","achievementChanges":{"added":["1","2"]}}""",
            sandbox: sandbox,
            validateOnly: validateOnly);

        Assert.Equal(0, exitCode);
        Assert.Contains("achievementChanges", stdout, StringComparison.Ordinal);
        Assert.Equal(listed, stderr.Contains("This publish would add 2 achievements to RETAIL: 1, 2.", StringComparison.Ordinal));
    }

    private const string RetailValidation =
        """{"Status":"Succeeded","SourceVersion":"52","achievementChanges":{"added":["1","2"]}}""";

    [Fact]
    public async Task Command_ToRetailAnsweredYes_ListsTheAchievementsThenPublishesTheValidatedVersion()
    {
        var handler = new CapturingHandler(
            (HttpStatusCode.OK, RetailValidation),
            (HttpStatusCode.OK, """{"Status":"Running"}"""));
        var run = await RunAsync(
            new Prompt(interactive: true, "y"), handler,
            "--sandbox-id", "RETAIL", "--source-sandbox-id", "XDKS.1", "--republish");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(2, handler.Bodies.Count);
        using JsonDocument validation = JsonDocument.Parse(handler.Bodies[0]);
        Assert.True(validation.RootElement.GetProperty("validateOnly").GetBoolean());
        Assert.False(validation.RootElement.GetProperty("republish").GetBoolean());
        Assert.Equal(JsonValueKind.Null, validation.RootElement.GetProperty("configSetVersion").ValueKind);
        using JsonDocument publish = JsonDocument.Parse(handler.Bodies[1]);
        Assert.False(publish.RootElement.GetProperty("validateOnly").GetBoolean());
        Assert.True(publish.RootElement.GetProperty("republish").GetBoolean());
        Assert.Equal(52UL, publish.RootElement.GetProperty("configSetVersion").GetUInt64());

        int achievements = run.Stderr.IndexOf(
            "This publish adds 2 achievements to RETAIL: 1, 2.", StringComparison.Ordinal);
        Assert.InRange(achievements, 0, run.Stderr.IndexOf("Publish to RETAIL? [y/N]: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Command_ToRetailWithUnpublishedSourceChanges_WarnsBeforeAsking()
    {
        var handler = new CapturingHandler(
            (HttpStatusCode.OK,
                """{"Status":"Succeeded","SourceVersion":"53","sourceHasUnpublishedChanges":true,"sourceLiveVersion":52,"achievementChanges":{"added":[]}}"""),
            (HttpStatusCode.OK, """{"Status":"Running"}"""));
        var run = await RunAsync(
            new Prompt(interactive: true, "y"), handler,
            "--sandbox-id", "RETAIL", "--source-sandbox-id", "XDKS.1");

        Assert.Equal(0, run.ExitCode);
        const string warning =
            "Warning: XDKS.1 has unpublished changes. This publish uses its latest working version, 53; " +
            "what's live in XDKS.1 is working version 52. To publish that instead, add --configset 52.";
        Assert.InRange(
            run.Stderr.IndexOf(warning, StringComparison.Ordinal),
            0,
            run.Stderr.IndexOf("Publish to RETAIL? [y/N]: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Command_ToRetailWithAVersion_KeepsTheVersionGiven()
    {
        var handler = new CapturingHandler(
            (HttpStatusCode.OK, RetailValidation),
            (HttpStatusCode.OK, """{"Status":"Running"}"""));
        var run = await RunAsync(
            new Prompt(interactive: true, "y"), handler,
            "--sandbox-id", "RETAIL", "--source-sandbox-id", "XDKS.1", "--configset", "7");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.All(handler.Bodies, body =>
        {
            using JsonDocument document = JsonDocument.Parse(body);
            Assert.Equal(7UL, document.RootElement.GetProperty("configSetVersion").GetUInt64());
        });
    }

    [Fact]
    public async Task Command_ToRetailAnsweredNo_OnlyValidates()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, RetailValidation);
        var run = await RunAsync(new Prompt(interactive: true, "n"), handler, "--sandbox-id", "RETAIL");

        Assert.Equal(1, run.ExitCode);
        using JsonDocument validation = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.True(validation.RootElement.GetProperty("validateOnly").GetBoolean());
        Assert.Contains("Publish canceled.", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_ToRetailWhenTheValidationFails_DoesNotAsk()
    {
        var prompt = new Prompt(interactive: true, "y");
        var handler = new CapturingHandler(
            HttpStatusCode.BadRequest,
            """{"status":400,"code":"invalid_configuration","title":"Invalid","detail":"The configuration isn't valid."}""");
        var run = await RunAsync(prompt, handler, "--sandbox-id", "RETAIL");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(0, prompt.Reads);
        Assert.Single(handler.Bodies);
        Assert.Contains("The configuration isn't valid.", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Publish to RETAIL?", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_ToRetailOfSharedConfiguration_SaysToAllowItWithoutAsking()
    {
        var prompt = new Prompt(interactive: true, "y");
        var handler = new CapturingHandler(
            HttpStatusCode.Conflict,
            """{"status":409,"code":"shared_configuration_requires_confirmation","title":"Shared configuration","detail":"Shared.","scid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","titleId":"00000043","boundTitleIds":["00000042","00000044"]}""");
        var run = await RunAsync(prompt, handler, "--sandbox-id", "RETAIL");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(0, prompt.Reads);
        Assert.Single(handler.Bodies);
        Assert.Contains(XboxLiveConfigurationTarget.AllowSharedConfigurationHint, run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_ToRetailWithoutASourceVersion_RefusesWithoutPublishing()
    {
        var handler = new CapturingHandler(
            (HttpStatusCode.OK, """{"Status":"Succeeded","achievementChanges":{"added":[]}}"""));
        var prompt = new Prompt(interactive: true, "y");
        var run = await RunAsync(prompt, handler, "--sandbox-id", "RETAIL");

        Assert.Equal(1, run.ExitCode);
        Assert.Single(handler.Bodies);
        Assert.Contains("Nothing was published", run.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--confirm")]
    [InlineData("--yes")]
    [InlineData("-y")]
    public async Task Command_Confirmed_PublishesWithoutAsking(string confirm)
    {
        var prompt = new Prompt(interactive: false);
        var run = await RunAsync(prompt, "--sandbox-id", "RETAIL", "--source-sandbox-id", "XDKS.1", confirm);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(0, prompt.Reads);
        using JsonDocument body = JsonDocument.Parse(Assert.Single(run.Handler.Bodies));
        Assert.Equal("RETAIL", body.RootElement.GetProperty("sandbox").GetString());
        Assert.False(body.RootElement.GetProperty("validateOnly").GetBoolean());
        Assert.False(body.RootElement.GetProperty("republish").GetBoolean());
        Assert.DoesNotContain("This publishes", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_ConfirmedBeforeTheCommand_PublishesWithoutAsking()
    {
        var prompt = new Prompt(interactive: false);
        var run = await RunAsync(prompt, ["--confirm"], "--sandbox-id", "CERT");

        Assert.Equal(0, run.ExitCode);
        Assert.NotNull(run.Handler.RequestBody);
    }

    [Fact]
    public async Task Command_ToADevelopmentSandbox_NeedsNoConfirmation()
    {
        var prompt = new Prompt(interactive: false);
        var run = await RunAsync(prompt, "--sandbox-id", "XDKS.1", "--republish");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(0, prompt.Reads);
        using JsonDocument body = JsonDocument.Parse(run.Handler.RequestBody!);
        Assert.True(body.RootElement.GetProperty("republish").GetBoolean());
    }

    [Fact]
    public async Task Command_ValidateOnly_NeedsNoConfirmation()
    {
        var run = await RunAsync(new Prompt(interactive: false), "--sandbox-id", "RETAIL", "--source-sandbox-id", "XDKS.1", "--validate-only");

        Assert.Equal(0, run.ExitCode);
        using JsonDocument body = JsonDocument.Parse(run.Handler.RequestBody!);
        Assert.True(body.RootElement.GetProperty("validateOnly").GetBoolean());
    }

    [Fact]
    public void Parse_RejectsRepublishWithValidateOnly()
    {
        ParseResult result = Program.BuildRootCommand().Parse(
            $"xbl config publish --scid {Scid} --sandbox-id XDKS.1 --republish --validate-only",
            Program.NoResponseFileConfig);

        Assert.Contains(
            result.Errors,
            error => error.Message == "--republish publishes, so it can't be combined with --validate-only.");
    }

    [Fact]
    public async Task RunAsync_WritesXboxLiveDiagnosticsToStderr()
    {
        const string response = """
            {
              "Status": "Running",
              "ValidationResult": [
                { "Severity": "Warning", "Error": "FirstRetailPublishWithoutEffectiveEmbargo", "DocumentName": "", "Message": "This is the first RETAIL publish, and no embargo is set." },
                { "Severity": "Information", "DocumentName": "achievements2017.xml", "Message": "Line\u001bone" },
                { "error": "NoSeverity" },
                "not an object"
              ],
              "JobId": "8c0e4b7a-1f2d-4e3a-9b5c-6d7e8f901234"
            }
            """;
        (int exitCode, string stdout, string stderr) = await RunDirectAsync(response);

        Assert.Equal(0, exitCode);
        Assert.Contains("FirstRetailPublishWithoutEffectiveEmbargo", stdout, StringComparison.Ordinal);
        Assert.Equal(
            [
                "XBOX services warning (FirstRetailPublishWithoutEffectiveEmbargo): This is the first RETAIL publish, and no embargo is set.",
                "XBOX services information in achievements2017.xml: Lineone",
                "XBOX services diagnostic (NoSeverity)",
            ],
            Lines(stderr));
    }

    [Theory]
    [InlineData("""{"Status":"Running"}""")]
    [InlineData("""{"Status":"Running","ValidationResult":null}""")]
    [InlineData("""{"Status":"Running","ValidationResult":[]}""")]
    public async Task RunAsync_WithoutDiagnostics_WritesNothingToStderr(string response)
    {
        (int exitCode, _, string stderr) = await RunDirectAsync(response);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task RunAsync_AlreadyPublished_SaysNoPublishStarted()
    {
        (int exitCode, string stdout, string stderr) = await RunDirectAsync(
            """{"Status":"AlreadyPublished","SourceVersion":"52"}""");

        Assert.Equal(0, exitCode);
        Assert.Contains("AlreadyPublished", stdout, StringComparison.Ordinal);
        Assert.Equal(
            "Nothing changed in XDKS.1 since its last publish to XDKS.1, so no publish started. " +
            "Use --republish to publish it again.",
            Assert.Single(Lines(stderr)));
    }

    [Fact]
    public async Task RunAsync_RetailLocked_ReportsTheGatewayError()
    {
        const string problem = """
            {"status":409,"code":"retail_locked","title":"RETAIL is locked for this product.","detail":"XBOX services lock RETAIL for a product after a migration, so they didn't publish. Republish to force the selected RETAIL configuration, by default its latest working one, onto RETAIL. Publishing to RETAIL from another sandbox, such as CERT, also clears the lock, but replaces RETAIL's working documents with that sandbox's. XBOX services: Product is locked. Use force flag to force publish of retail"}
            """;
        (int exitCode, string stdout, string stderr) = await RunDirectAsync(problem, HttpStatusCode.Conflict);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("retail_locked", stderr, StringComparison.Ordinal);
        Assert.Contains("Republish to force the selected RETAIL configuration", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CanceledAfterSending_SaysTheOutcomeIsUnknown()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RespondingHandler(() =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        (int exitCode, string stdout, string stderr) = await RunDirectAsync(handler, cancellationToken: cancellation.Token);

        Assert.Equal(PublishCommandFactory.OutcomeUnknownExitCode, exitCode);
        Assert.Empty(stdout);
        Assert.Collection(
            Lines(stderr),
            line => Assert.Matches("^x-ms-correlation-id: [0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", line),
            line => Assert.Equal("It is unknown whether the publish to XDKS.1 started. Don't resubmit it.", line),
            line => Assert.Equal($"Check with: xbox xbl config status --scid {Scid} --seller-id 12345 --sandbox-id XDKS.1", line));
    }

    [Fact]
    public async Task RunAsync_TimedOutAfterSending_SaysTheOutcomeIsUnknown()
    {
        var handler = new RespondingHandler(() => throw new TaskCanceledException("timed out"));

        (int exitCode, _, string stderr) = await RunDirectAsync(handler);

        Assert.Equal(PublishCommandFactory.OutcomeUnknownExitCode, exitCode);
        Assert.Contains("Request timed out." + Environment.NewLine, stderr, StringComparison.Ordinal);
        Assert.Contains("It is unknown whether the publish to XDKS.1 started.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ValidateOnlyCanceled_StopsWithoutAnUnknownOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RespondingHandler(() =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        using var client = CreateClient(handler);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PublishCommandFactory.RunAsync(
            client, 12345, Request("XDKS.1", validateOnly: true), OutputOptions.Json, stdout, stderr, cancellation.Token));

        Assert.Empty(stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task Command_CanceledAfterSending_ExitsWithTheUnknownOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RespondingHandler(() =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Program.XboxPartnerClientFactory = _ => CreateClient(handler);
        try
        {
            int exitCode = await Program.BuildRootCommand()
                .Parse(["xbl", "config", "publish", "--scid", Scid, "--seller-id", "1234567", "--sandbox-id", "XDKS.1"],
                    Program.NoResponseFileConfig)
                .InvokeAsync(cancellationToken: cancellation.Token);

            Assert.Equal(PublishCommandFactory.OutcomeUnknownExitCode, exitCode);
            Assert.Contains("It is unknown whether the publish to XDKS.1 started.", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static XboxLivePublishRequest Request(
        string sandbox,
        string? source = null,
        bool validateOnly = false,
        ulong? configSet = null,
        bool republish = false)
    {
        return new XboxLivePublishRequest(Guid.Parse(Scid), sandbox, source ?? sandbox, validateOnly, configSet, republish);
    }

    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static string[] Lines(string text) =>
        text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDirectAsync(
        string response,
        HttpStatusCode status = HttpStatusCode.OK,
        string sandbox = "XDKS.1",
        bool validateOnly = false)
    {
        using var client = CreateClient(new CapturingHandler(status, response));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = await PublishCommandFactory.RunAsync(
            client,
            12345,
            Request(sandbox, "XDKS.1", validateOnly: validateOnly),
            OutputOptions.Json,
            stdout,
            stderr,
            CancellationToken.None);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDirectAsync(
        HttpMessageHandler handler,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(handler);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = await PublishCommandFactory.RunAsync(
            client, 12345, Request("XDKS.1"), OutputOptions.Json, stdout, stderr, cancellationToken);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static Task<CommandRun> RunAsync(Prompt prompt, params string[] options) =>
        RunAsync(prompt, [], options);

    private static Task<CommandRun> RunAsync(Prompt prompt, string[] globalOptions, params string[] options) =>
        RunAsync(prompt, globalOptions, CancellationToken.None, options);

    private static Task<CommandRun> RunAsync(
        Prompt prompt,
        string[] globalOptions,
        CancellationToken cancellationToken,
        params string[] options) =>
        RunAsync(prompt, new CapturingHandler(HttpStatusCode.OK, """{"Status":"Running"}"""), globalOptions, cancellationToken, options);

    private static Task<CommandRun> RunAsync(Prompt prompt, CapturingHandler handler, params string[] options) =>
        RunAsync(prompt, handler, [], CancellationToken.None, options);

    private static async Task<CommandRun> RunAsync(
        Prompt prompt,
        CapturingHandler handler,
        string[] globalOptions,
        CancellationToken cancellationToken,
        string[] options)
    {
        RootCommand root = Program.BuildRootCommand();
        Command config = root.Subcommands.Single(command => command.Name == "xbl")
            .Subcommands.Single(command => command.Name == "config");
        config.Subcommands.Remove(config.Subcommands.Single(command => command.Name == "publish"));
        config.Subcommands.Add(PublishCommandFactory.Create(prompt.Dependencies));
        CommandErrorBoundary.Install(root);

        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Program.XboxPartnerClientFactory = configuration => CreateClient(handler);
        try
        {
            string[] args =
            [
                .. globalOptions,
                "xbl", "config", "publish", "--scid", Scid, "--seller-id", "1234567",
                .. options,
            ];
            int exitCode = await root.Parse(args, Program.NoResponseFileConfig)
                .InvokeAsync(cancellationToken: cancellationToken);
            return new CommandRun(exitCode, stdout.ToString(), stderr.ToString(), handler);
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static XboxPartnerClient CreateClient(HttpMessageHandler handler) =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["XboxPartner:BaseUrl"] = "http://localhost:8080",
                    ["XboxPartner:TimeoutSeconds"] = "10",
                })
                .Build(),
            new HttpClient(handler),
            () => Task.FromResult<string?>("caller-token"));

    private sealed record CommandRun(int ExitCode, string Stdout, string Stderr, CapturingHandler Handler);

    private sealed class Prompt(bool interactive, string? answer = null, bool blocks = false)
    {
        public int Reads { get; private set; }

        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PublishConfirmationDependencies Dependencies => new(
            () => interactive,
            cancellationToken =>
            {
                Reads++;
                Asked.TrySetResult();
                return blocks
                    ? new TaskCompletionSource<string?>().Task.WaitAsync(cancellationToken)
                    : Task.FromResult(answer);
            });
    }

    private sealed class RespondingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond());
    }

    /// <summary>Answers each request with the next response, repeating the last one.</summary>
    private sealed class CapturingHandler(params (HttpStatusCode Status, string Response)[] responses)
        : HttpMessageHandler
    {
        public CapturingHandler(HttpStatusCode status, string response)
            : this((status, response))
        {
        }

        public List<string> Bodies { get; } = [];

        public string? RequestBody => Bodies.Count == 0 ? null : Bodies[^1];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            (HttpStatusCode status, string response) = responses[Math.Min(Bodies.Count, responses.Length) - 1];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
