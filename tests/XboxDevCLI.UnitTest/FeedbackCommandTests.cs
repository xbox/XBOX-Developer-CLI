// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class FeedbackCommandTests
{
    private const string EnvironmentSummary =
        "XBOX CLI: 1.2.3-preview\nOS: Test OS & edition + unicode \u00e9\nOS architecture: Arm64\nProcess architecture: X64\nRuntime: .NET 10.0.1";

    [Theory]
    [InlineData("feedback", "y")]
    [InlineData("feedback", " YES ")]
    [InlineData("report-issue", "Y")]
    public async Task Consent_OpensOnlyTheEncodedDraftAndDisplaysEnvironmentFirst(string command, string answer)
    {
        ProcessStartInfo? opened = null;
        var environmentCollected = false;
        var dependencies = new FeedbackDependencies(
            () => true,
            _ =>
            {
                Assert.True(environmentCollected);
                Assert.Null(opened);
                return Task.FromResult<string?>(answer);
            },
            startInfo => opened = startInfo,
            () =>
            {
                environmentCollected = true;
                return EnvironmentSummary;
            });

        var result = await InvokeAsync([command], dependencies);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(opened);
        Assert.True(opened.UseShellExecute);
        Assert.Empty(opened.Arguments);
        Assert.Empty(opened.ArgumentList);
        var url = new Uri(opened.FileName);
        Assert.Equal(ProjectLinks.NewIssue, url.GetLeftPart(UriPartial.Path));
        Assert.Equal(EnvironmentSummary, ReadQuery(url)["environment"]);
        Assert.Contains(opened.FileName, result.Stdout);
        Assert.Contains("If it does not open", result.Stderr);
        Assert.DoesNotContain("GitHub issues are public", result.Stdout);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("n")]
    [InlineData("NO")]
    [InlineData("q")]
    public async Task DecliningOrEndOfInput_DoesNotOpenBrowser(string? answer)
    {
        var result = await InvokeAsync(["feedback"], new FeedbackDependencies(
            () => true,
            _ => Task.FromResult(answer),
            _ => Assert.Fail("A declined prompt must not open a browser."),
            () => EnvironmentSummary));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Feedback cancelled", result.Stderr);
        Assert.DoesNotContain("Create an issue at:", result.Stdout);
    }

    [Fact]
    public async Task InvalidAnswer_ExplainsExpectedInputAndPromptsAgain()
    {
        var answers = new Queue<string?>(["maybe", "yes"]);
        var browserCalls = 0;
        var result = await InvokeAsync(["feedback"], new FeedbackDependencies(
            () => true,
            _ => Task.FromResult(answers.Dequeue()),
            _ => browserCalls++,
            () => EnvironmentSummary));

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(answers);
        Assert.Equal(1, browserCalls);
        Assert.Contains("Enter Y to open the draft, or N to cancel.", result.Stderr);
    }

    [Theory]
    [InlineData("feedback")]
    [InlineData("report-issue")]
    public async Task NoBrowser_PrintsDraftWithoutConsultingTerminalOrOpeningBrowser(string command)
    {
        var result = await InvokeAsync([command, "--no-browser"], new FeedbackDependencies(
            () => throw new InvalidOperationException("Must not require a terminal."),
            _ => throw new InvalidOperationException("Must not read input."),
            _ => Assert.Fail("Must not launch a browser."),
            () => EnvironmentSummary));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(EnvironmentSummary, result.Stdout);
        Assert.Contains(FeedbackCommandFactory.BuildIssueUrl(EnvironmentSummary), result.Stdout);
        Assert.DoesNotContain("[y/N]", result.Stderr);
        Assert.Contains("No issue is submitted automatically", result.Stderr);
    }

    [Fact]
    public async Task NoTerminal_FailsWithGuidanceBeforeReadingEnvironmentOrInput()
    {
        var result = await InvokeAsync(["feedback"], new FeedbackDependencies(
            () => false,
            _ => throw new InvalidOperationException("Must not read input."),
            _ => Assert.Fail("Must not launch a browser."),
            () => throw new InvalidOperationException("Must not collect environment.")));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("xbox feedback --no-browser", result.Stderr);
    }

    [Theory]
    [InlineData("missing-handler")]
    [InlineData("invalid-operation")]
    [InlineData("io")]
    [InlineData("permission")]
    [InlineData("unsupported")]
    public async Task BrowserLaunchFailure_WarnsAndLeavesUsableDraftUrl(string failure)
    {
        var result = await InvokeAsync(["feedback"], new FeedbackDependencies(
            () => true,
            _ => Task.FromResult<string?>("y"),
            _ => throw (failure switch
            {
                "missing-handler" => new Win32Exception("No browser installed."),
                "invalid-operation" => new InvalidOperationException("No URL handler."),
                "io" => new IOException("Browser unavailable."),
                "permission" => new UnauthorizedAccessException("Opening URLs is blocked."),
                _ => new NotSupportedException("No shell support."),
            }),
            () => EnvironmentSummary));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(FeedbackCommandFactory.BuildIssueUrl(EnvironmentSummary), result.Stdout);
        Assert.Contains("Could not open a browser:", result.Stderr);
        Assert.Contains("Copy the URL above", result.Stderr);
        Assert.DoesNotContain("Requested browser opening", result.Stderr);
    }

    [Fact]
    public async Task CancellationWhilePrompting_StopsWithoutOpeningBrowser()
    {
        using var cancellation = new CancellationTokenSource();
        var result = await InvokeAsync(["feedback"], new FeedbackDependencies(
            () => true,
            token =>
            {
                Assert.Equal(cancellation.Token, token);
                cancellation.Cancel();
                return Task.FromCanceled<string?>(token);
            },
            _ => Assert.Fail("Cancellation must not open a browser."),
            () => EnvironmentSummary), cancellation.Token);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("Create an issue at:", result.Stdout);
    }

    [Fact]
    public async Task CancellationWhileReaderIsBlocked_EndsThePromptPromptly()
    {
        using var readerStarted = new ManualResetEventSlim();
        using var releaseReader = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var read = FeedbackDependencies.CancelableReadLine(() =>
        {
            readerStarted.Set();
            releaseReader.Wait(TimeSpan.FromSeconds(30));
            return "y";
        });

        try
        {
            var pending = read(cancellation.Token);
            Assert.True(readerStarted.Wait(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();

            var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(pending, completed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally
        {
            releaseReader.Set();
        }
    }

    [Fact]
    public async Task CancelableReadLine_ReturnsTheLineRead()
    {
        var read = FeedbackDependencies.CancelableReadLine(() => "yes");

        Assert.Equal("yes", await read(CancellationToken.None));
    }

    [Fact]
    public async Task InputFailure_IsReportedWithoutOpeningBrowser()
    {
        var result = await InvokeAsync(["feedback"], new FeedbackDependencies(
            () => true,
            _ => throw new IOException("Input stream closed."),
            _ => Assert.Fail("Failed input must not open a browser."),
            () => EnvironmentSummary));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Feedback failed: Input stream closed.", result.Stderr);
        Assert.DoesNotContain("Create an issue at:", result.Stdout);
    }

    [Fact]
    public async Task TableOutput_IsRejectedBeforeCollectingEnvironment()
    {
        var result = await InvokeAsync(["feedback", "--no-browser", "--output", "table"], new FeedbackDependencies(
            () => throw new InvalidOperationException("Must not consult terminal."),
            _ => throw new InvalidOperationException("Must not read input."),
            _ => Assert.Fail("Must not launch a browser."),
            () => throw new InvalidOperationException("Must not collect environment.")));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(Program.TableUnavailableMessage, result.Stderr);
    }

    [Fact]
    public void RootRegistration_HasOneCanonicalCommandAndAHiddenAlias()
    {
        var root = Program.BuildRootCommand();
        var canonical = root.Parse(["feedback"], Program.NoResponseFileConfig);
        var alias = root.Parse(["report-issue"], Program.NoResponseFileConfig);
        using var help = new StringWriter();
        CommandHelpWriter.WriteRoot(help, root, CliIdentity.CommandName);

        Assert.Empty(canonical.Errors);
        Assert.Empty(alias.Errors);
        Assert.Same(canonical.CommandResult.Command, alias.CommandResult.Command);
        Assert.Equal("feedback", alias.CommandResult.Command.Name);
        Assert.Contains("feedback:", help.ToString());
        Assert.DoesNotContain("report-issue", help.ToString());
        Assert.DoesNotContain(root.Subcommands, command => command.Name == "report-issue");
    }

    [Theory]
    [InlineData("feedback", "--help")]
    [InlineData("report-issue", "--help")]
    [InlineData("help", "feedback")]
    [InlineData("help", "report-issue")]
    public async Task Help_DocumentsPrivacyAndNoninteractiveAlternativeWithoutSideEffects(string first, string second)
    {
        var dependencies = new FeedbackDependencies(
            () => throw new InvalidOperationException("Help must not consult terminal."),
            _ => throw new InvalidOperationException("Help must not read input."),
            _ => Assert.Fail("Help must not launch a browser."),
            () => throw new InvalidOperationException("Help must not collect environment."));
        var result = await InvokeAsync([first, second], dependencies);
        var canonical = await InvokeAsync(["feedback", "--help"], dependencies);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.Equal(canonical.Stdout, result.Stdout);
        Assert.Contains("xbox feedback", result.Stdout);
        Assert.Contains("--no-browser", result.Stdout);
        Assert.Contains("Never submits an issue", result.Stdout);
        Assert.DoesNotContain("report-issue", result.Stdout);
    }

    [Fact]
    public void DraftUrl_PrefillsAnExistingGitHubFormFieldAndDoesNotInjectQueryParameters()
    {
        var url = new Uri(FeedbackCommandFactory.BuildIssueUrl(EnvironmentSummary + "\n&title=not-a-title#fragment"));
        var query = ReadQuery(url);
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "IssueTemplates", query["template"]));

        Assert.Equal(2, query.Count);
        Assert.Empty(url.Fragment);
        Assert.Equal(EnvironmentSummary + "\n&title=not-a-title#fragment", query["environment"]);
        Assert.Contains("    id: environment\n", template.ReplaceLineEndings("\n"));
        Assert.Contains("      label: Environment Summary", template);

        // The form has to live in the repository that receives its issues. An aka.ms link's
        // target is set outside the code, so only a direct GitHub link can be checked here.
        var newIssue = new Uri(ProjectLinks.NewIssue);
        if (newIssue.Host == "github.com")
        {
            var issues = newIssue.GetLeftPart(UriPartial.Path)[..^"/new".Length];
            Assert.Contains(issues, template);
        }
    }

    [Fact]
    public void EnvironmentSummary_IsRestrictedToVersionAndRuntimeMetadata()
    {
        var summary = FeedbackCommandFactory.GetEnvironmentSummary();

        Assert.Equal(
            $"XBOX CLI: {CliIdentity.Version}\n"
            + $"OS: {RuntimeInformation.OSDescription}\n"
            + $"OS architecture: {RuntimeInformation.OSArchitecture}\n"
            + $"Process architecture: {RuntimeInformation.ProcessArchitecture}\n"
            + $"Runtime: {RuntimeInformation.FrameworkDescription}",
            summary);
    }

    private static Dictionary<string, string> ReadQuery(Uri url) =>
        url.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
            .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));

    private static async Task<CapturedInvocation> InvokeAsync(
        string[] arguments,
        FeedbackDependencies dependencies,
        CancellationToken cancellationToken = default)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var root = new RootCommand();
            root.Options.Add(Program.OutputOption);
            root.Subcommands.Add(FeedbackCommandFactory.Create(dependencies));
            CommandLineExperience.ConfigureHelp(root, CliIdentity.CommandName);
            var exitCode = await root.Parse(arguments, Program.NoResponseFileConfig)
                .InvokeAsync(cancellationToken: cancellationToken);
            return new CapturedInvocation(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record CapturedInvocation(int ExitCode, string Stdout, string Stderr);
}
