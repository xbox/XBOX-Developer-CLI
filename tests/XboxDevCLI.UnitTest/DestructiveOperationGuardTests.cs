// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text.Json;
using XboxDevCLI.Safety;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class DestructiveOperationGuardTests
{
    [Fact]
    public async Task RawApi_DestructiveOperation_WithoutConfirm_IsRefused()
    {
        // Without --confirm, a destructive operation must not proceed. With no input
        // available to confirm interactively, the guard returns a non-zero exit code
        // before any network call is attempted.
        var originalError = Console.Error;
        var originalIn = Console.In;
        var errorWriter = new StringWriter();
        Console.SetError(errorWriter);
        Console.SetIn(new StringReader(string.Empty));
        try
        {
            var result = await Program.Main(new[]
            {
                "--playfab-title-id", "ABC123",
                "playfab", "api", "/Admin/DeleteTitle",
            });

            Assert.Equal(1, result);
            var stderr = errorWriter.ToString();
            Assert.Contains("destructive", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public async Task RawApi_ObfuscatedDestructivePath_WithoutConfirm_IsRefused()
    {
        // A destructive op disguised with a backslash
        // (System.Uri normalizes "/Admin\DeleteTitle" -> "/Admin/DeleteTitle" on the wire)
        // must still be caught by the guard, not slip past it unconfirmed.
        var originalError = Console.Error;
        var originalIn = Console.In;
        var errorWriter = new StringWriter();
        Console.SetError(errorWriter);
        Console.SetIn(new StringReader(string.Empty));
        try
        {
            var result = await Program.Main(new[]
            {
                "--playfab-title-id", "ABC123",
                "playfab", "api", "/Admin\\DeleteTitle",
            });

            Assert.Equal(1, result);
            var stderr = errorWriter.ToString();
            Assert.Contains("destructive", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetIn(originalIn);
        }
    }

    [Theory]
    [InlineData("--confirm")]
    [InlineData("--yes")]
    [InlineData("-y")]
    public async Task RawApi_DestructiveOperation_WithConfirm_PassesGuard(string confirmFlag)
    {
        // With --confirm (or its --yes/-y aliases), the destructive guard must NOT block
        // the call. We override --client-id to a zero GUID so MSAL silent acquisition finds
        // no matching cached accounts (regardless of whether the developer running the test
        // is logged in against the real client-id), forcing a deterministic downstream
        // auth failure. The assertion is that the failure message is NOT the
        // destructive-guard message — proving the guard was bypassed cleanly.
        var originalError = Console.Error;
        var originalIn = Console.In;
        var errorWriter = new StringWriter();
        Console.SetError(errorWriter);
        Console.SetIn(new StringReader(string.Empty));
        try
        {
            var result = await Program.Main(new[]
            {
                "--playfab-title-id", "ABC123",
                "--client-id", "00000000-0000-0000-0000-000000000000",
                "playfab", "api", "/Admin/DeleteTitle",
                confirmFlag,
            });

            // The call still fails (no MSAL-cached token), but for a different reason
            // than the destructive guard — proving the confirm flag passed through correctly.
            Assert.Equal(1, result);
            var stderr = errorWriter.ToString();
            Assert.DoesNotContain("destructive", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--confirm", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Request failed", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public async Task Mps_BuildDelete_WithoutConfirm_IsRefused()
    {
        // The mps commands route through the shared Program.InvokeApiAsync path, so the
        // destructive guard must fire for `mps build delete` (-> /MultiplayerServer/DeleteBuild)
        // just as it does for `playfab api`. This proves the guard lives at the shared chokepoint
        // and not only at the api-call call site.
        var originalError = Console.Error;
        var originalIn = Console.In;
        var errorWriter = new StringWriter();
        Console.SetError(errorWriter);
        Console.SetIn(new StringReader(string.Empty));
        try
        {
            var result = await Program.Main(new[]
            {
                "--playfab-title-id", "ABC123",
                "playfab", "mps", "build", "delete",
                "--body", "{\"BuildId\":\"00000000-0000-0000-0000-000000000000\"}",
            });

            Assert.Equal(1, result);
            var stderr = errorWriter.ToString();
            Assert.Contains("destructive", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public async Task Mps_ServerShutdown_WithoutConfirm_IsRefused()
    {
        // `mps server shutdown` maps to /MultiplayerServer/ShutdownMultiplayerServer,
        // which is irreversible but is NOT a Delete* operation. It must still be gated, proving
        // the classifier recognizes the Shutdown* prefix.
        var originalError = Console.Error;
        var originalIn = Console.In;
        var errorWriter = new StringWriter();
        Console.SetError(errorWriter);
        Console.SetIn(new StringReader(string.Empty));
        try
        {
            var result = await Program.Main(new[]
            {
                "--playfab-title-id", "ABC123",
                "playfab", "mps", "server", "shutdown",
                "--body", "{\"SessionId\":\"00000000-0000-0000-0000-000000000000\"}",
            });

            Assert.Equal(1, result);
            var stderr = errorWriter.ToString();
            Assert.Contains("destructive", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public void Root_HasRecursiveConfirmOption()
    {
        // --confirm is declared once as a recursive option on the root command so the
        // shared guard can read it for playfab api AND every mps/file subcommand.
        var command = Program.BuildRootCommand();
        var confirm = Assert.Single(command.Options, o => o.Name == "--confirm");

        Assert.True(confirm.Recursive);
        // --yes / -y are the Azure-standard aliases for the same option.
        Assert.Contains("--yes", confirm.Aliases);
        Assert.Contains("-y", confirm.Aliases);
    }

    /// <summary>
    /// Every command path that can delete production data: the raw API call, and the commands that
    /// confirm on their own rather than through it.
    /// </summary>
    public static TheoryData<string[], string> DestructiveCommands => new()
    {
        { ["playfab", "api", "/Admin/DeleteTitle"], "/Admin/DeleteTitle" },
        { ["playfab", "group", "delete", "--group-id", "G1"], "/Group/DeleteGroup" },
        { ["playfab", "group", "role", "delete", "--group-id", "G1", "--role-id", "R1"], "/Group/DeleteRole" },
        {
            ["playfab", "file", "delete", "--entity-id", "E1", "--entity-type", "title_player_account", "save.dat"],
            "/File/DeleteFiles"
        },
    };

    /// <summary>
    /// A missing title is reported before the command runs, so nobody is asked to confirm a
    /// deletion with no target.
    /// </summary>
    [Theory]
    [MemberData(nameof(DestructiveCommands))]
    public async Task MissingTitle_FailsBeforeAnyConfirmationPrompt(string[] command, string apiPath)
    {
        var handler = SuccessHandler();

        var result = await InvokeWithClientSeamAsync(handler, command);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("No playfab-title-id is set. Pass --playfab-title-id, set XBOX_PLAYFAB_TITLE_ID, or run 'xbox context set playfab-title-id <value>'.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING:", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(apiPath, result.Stderr, StringComparison.Ordinal);
        Assert.Null(handler.LastRequest);
    }

    /// <summary>
    /// <c>/Admin/DeleteTitle</c> deletes the title it's sent to, so that title has to be on the
    /// command line: one from saved context can change without anyone seeing it. The refusal comes
    /// before any prompt, even with <c>--confirm</c>, and spells out the command to run instead,
    /// with the title right after <c>xbox</c>, where it still parses when the command has a
    /// <c>--</c>.
    /// </summary>
    [Theory]
    [InlineData(
        new[] { "playfab", "api", "/Admin/DeleteTitle", "--confirm" },
        "xbox --playfab-title-id ABC12 playfab api /Admin/DeleteTitle --confirm")]
    [InlineData(
        new[] { "playfab", "api", "--confirm", "--", "/Admin/DeleteTitle" },
        "xbox --playfab-title-id ABC12 playfab api --confirm -- /Admin/DeleteTitle")]
    public async Task DeleteTitle_RefusesASavedTitleAndSaysWhatToRun(string[] command, string suggestion)
    {
        var handler = SuccessHandler();

        var result = await InvokeWithClientSeamAsync(handler, command, savedTitle: "ABC12");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            [
                "Using --playfab-title-id ABC12 from saved context.",
                "'/Admin/DeleteTitle' acts on playfab-title-id ABC12, which comes from saved context.",
                "The target of a destructive operation must be named on the command line. To proceed, run:",
                "  " + suggestion,
            ],
            result.Stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty(result.Stdout);
        Assert.Null(handler.LastRequest);

        // The suggestion runs as printed: the title comes from its flag, and the path is still
        // the path.
        var retry = Program.BuildRootCommand().Parse(suggestion.Split(' ')[1..], Program.NoResponseFileConfig);
        Assert.Empty(retry.Errors);
        Assert.Equal("ABC12", retry.GetValue(Program.PlayFabTitleIdOption));
        Assert.Equal(
            "/Admin/DeleteTitle",
            Assert.Single(retry.GetResult(retry.CommandResult.Command.Arguments[0])!.Tokens).Value);
    }

    /// <summary>
    /// A title from <c>XBOX_PLAYFAB_TITLE_ID</c> isn't on the command line either: a shell profile
    /// or a parent process can set it unseen, so it's named and then refused the same way.
    /// </summary>
    [Fact]
    public async Task DeleteTitle_RefusesATitleFromTheEnvironmentAndSaysWhatToRun()
    {
        var handler = SuccessHandler();

        var result = await InvokeWithClientSeamAsync(
            handler,
            ["playfab", "api", "/Admin/DeleteTitle", "--confirm"],
            environmentTitle: "ABC12");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            [
                "Using --playfab-title-id ABC12 from XBOX_PLAYFAB_TITLE_ID.",
                "'/Admin/DeleteTitle' acts on playfab-title-id ABC12, which comes from XBOX_PLAYFAB_TITLE_ID.",
                "The target of a destructive operation must be named on the command line. To proceed, run:",
                "  xbox --playfab-title-id ABC12 playfab api /Admin/DeleteTitle --confirm",
            ],
            result.Stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty(result.Stdout);
        Assert.Null(handler.LastRequest);
    }

    /// <summary>
    /// The suggested command can be pasted back into a shell: arguments a shell would split or
    /// interpret are quoted, and the rest are left alone. An inline <c>--body</c> goes to stderr,
    /// where CI logs keep it, so its value isn't repeated, in either form the option takes; the
    /// refusal says where to put it back.
    /// </summary>
    [Theory]
    [InlineData("--body", """{"Note": "a b", "Secret": "s3cret"}""")]
    [InlineData("--body={\"Secret\":\"s3cret\"}", null)]
    public async Task DeleteTitle_RefusalQuotesArgumentsAndLeavesOutAnInlineBody(string bodyOption, string? bodyValue)
    {
        var handler = SuccessHandler();
        string[] body = bodyValue is null ? [bodyOption] : [bodyOption, bodyValue];

        var result = await InvokeWithClientSeamAsync(
            handler,
            ["playfab", "api", "/Admin\\DeleteTitle", .. body, "-y"],
            savedTitle: "ABC12");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            Environment.NewLine
            + """  xbox --playfab-title-id ABC12 playfab api '/Admin\DeleteTitle' --body <body> -y"""
            + Environment.NewLine
            + "The --body value isn't repeated, since a request body can hold secrets; put it in place "
            + "of <body>." + Environment.NewLine,
            result.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", result.Stderr, StringComparison.Ordinal);
        Assert.Null(handler.LastRequest);
    }

    /// <summary>
    /// A file path is repeated as a literal argument for the shell, PowerShell on Windows and a
    /// POSIX shell elsewhere: a <c>$</c> or a backtick in it isn't expanded, and an apostrophe is
    /// escaped, so it can't end the quoted argument early and leave the rest to be run. A
    /// <c>--body-file</c> path isn't a secret, so it's repeated, and without a <c>--body</c>
    /// there's nothing to put back.
    /// </summary>
    [Fact]
    public async Task DeleteTitle_RefusalKeepsAFilePathLiteral()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"xbox-refusal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var bodyFile = Path.Combine(directory, "don't $HOME `whoami`; rm -rf.json");
        await File.WriteAllTextAsync(bodyFile, "{}");
        try
        {
            var handler = SuccessHandler();

            var result = await InvokeWithClientSeamAsync(
                handler,
                ["playfab", "api", "/Admin/DeleteTitle", "--body-file", bodyFile, "--confirm"],
                savedTitle: "ABC12");

            var quoted = OperatingSystem.IsWindows()
                ? $"'{bodyFile.Replace("'", "''", StringComparison.Ordinal)}'"
                : $"'{bodyFile.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
            Assert.Equal(1, result.ExitCode);
            Assert.Contains(
                $"  xbox --playfab-title-id ABC12 playfab api /Admin/DeleteTitle --body-file {quoted} --confirm"
                + Environment.NewLine,
                result.Stderr,
                StringComparison.Ordinal);
            Assert.DoesNotContain("<body>", result.Stderr, StringComparison.Ordinal);
            Assert.Null(handler.LastRequest);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Named on the command line, the title is deleted as confirmed, whatever the environment
    /// says.
    /// </summary>
    [Fact]
    public async Task DeleteTitle_ProceedsWhenTheTitleIsOnTheCommandLine()
    {
        var handler = SuccessHandler();

        var result = await InvokeWithClientSeamAsync(
            handler,
            ["playfab", "api", "/Admin/DeleteTitle", "--confirm", "--playfab-title-id", "ABC12"],
            environmentTitle: "ENV12");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.Equal("/Admin/DeleteTitle", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// Other destructive operations act on something their request names, inside the title, so an
    /// implicit title is allowed once confirmed, and named on stderr.
    /// </summary>
    [Theory]
    [InlineData(new[] { "playfab", "api", "/Admin/DeletePlayer" }, "/Admin/DeletePlayer", false)]
    [InlineData(new[] { "playfab", "mps", "build", "delete", "--body", "{\"BuildId\":\"b1\"}" }, "/MultiplayerServer/DeleteBuild", false)]
    [InlineData(new[] { "playfab", "api", "/Admin/DeletePlayer" }, "/Admin/DeletePlayer", true)]
    public async Task OtherDestructiveOperations_AcceptAnImplicitTitleAndNameIt(string[] command, string apiPath, bool fromVariable)
    {
        var handler = SuccessHandler();

        var result = await InvokeWithClientSeamAsync(
            handler,
            [.. command, "--confirm"],
            savedTitle: fromVariable ? null : "ABC12",
            environmentTitle: fromVariable ? "ABC12" : null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            $"Using --playfab-title-id ABC12 from {(fromVariable ? "XBOX_PLAYFAB_TITLE_ID" : "saved context")}."
            + Environment.NewLine,
            result.Stderr);
        Assert.Equal(apiPath, handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// <c>api-policy set --overwrite</c> replaces the title's whole API access policy, so the
    /// title has to be on the command line, just as for <c>/Admin/DeleteTitle</c>. The suggested
    /// command doesn't repeat the inline statements, which would land in logs.
    /// </summary>
    [Theory]
    [InlineData(false, "saved context")]
    [InlineData(true, "XBOX_PLAYFAB_TITLE_ID")]
    public async Task ApiPolicyOverwrite_RefusesAnImplicitTitleAndSaysWhatToRun(bool fromVariable, string origin)
    {
        var handler = SuccessHandler();

        var result = await InvokeWithClientSeamAsync(
            handler,
            ["playfab", "api-policy", "set", "--policy-version", "3", "--statements", PolicyStatements, "--overwrite", "--confirm"],
            savedTitle: fromVariable ? null : "ABC12",
            environmentTitle: fromVariable ? "ABC12" : null);

        const string suggestion =
            "xbox --playfab-title-id ABC12 playfab api-policy set --policy-version 3 --statements <statements> --overwrite --confirm";
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            [
                $"Using --playfab-title-id ABC12 from {origin}.",
                $"'/Admin/UpdatePolicy' with --overwrite acts on playfab-title-id ABC12, which comes from {origin}.",
                "The target of a destructive operation must be named on the command line. To proceed, run:",
                "  " + suggestion,
                "The --statements value isn't repeated, since a request body can hold secrets; put it in place of <statements>.",
            ],
            result.Stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain("pfrn:", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(result.Stdout);
        Assert.Null(handler.LastRequest);

        var retry = Program.BuildRootCommand().Parse(
            suggestion.Replace("<statements>", "[]", StringComparison.Ordinal).Split(' ')[1..],
            Program.NoResponseFileConfig);
        Assert.Empty(retry.Errors);
        Assert.Equal("ABC12", retry.GetValue(Program.PlayFabTitleIdOption));
    }

    /// <summary>
    /// Named on the command line, the title's policy is overwritten as confirmed, whatever the
    /// environment says.
    /// </summary>
    [Fact]
    public async Task ApiPolicyOverwrite_ProceedsWhenTheTitleIsOnTheCommandLine()
    {
        var handler = SuccessHandler();

        var result = await InvokeWithClientSeamAsync(
            handler,
            ["--playfab-title-id", "ABC12", "playfab", "api-policy", "set", "--policy-version", "3",
             "--statements", PolicyStatements, "--overwrite", "--confirm"],
            environmentTitle: "ENV12");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.Equal("/Admin/UpdatePolicy", handler.LastRequest!.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.True(body.RootElement.GetProperty("OverwritePolicy").GetBoolean());
    }

    /// <summary>
    /// Appending leaves the rest of the policy in place, so an implicit title is still allowed,
    /// and named on stderr.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApiPolicyAppend_AcceptsAnImplicitTitleAndNamesIt(bool fromVariable)
    {
        var handler = SuccessHandler();

        var result = await InvokeWithClientSeamAsync(
            handler,
            ["playfab", "api-policy", "set", "--policy-version", "3", "--statements", PolicyStatements],
            savedTitle: fromVariable ? null : "ABC12",
            environmentTitle: fromVariable ? "ABC12" : null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            $"Using --playfab-title-id ABC12 from {(fromVariable ? "XBOX_PLAYFAB_TITLE_ID" : "saved context")}."
            + Environment.NewLine,
            result.Stderr);
        Assert.Equal("/Admin/UpdatePolicy", handler.LastRequest!.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(body.RootElement.GetProperty("OverwritePolicy").GetBoolean());
    }

    private const string PolicyStatements =
        """[{"Resource":"pfrn:api--*","Action":"*","Effect":"Allow","Principal":"*"}]""";

    /// <summary>
    /// A file operation isn't a data command: its success message goes to stderr and stdout stays
    /// empty, so a script capturing stdout can't mistake the message for data.
    /// </summary>
    [Fact]
    public async Task FileDelete_ReportsOnStderrAndWritesNothingToStdout()
    {
        var result = await InvokeWithClientSeamAsync(
            SuccessHandler(),
            ["--playfab-title-id", "ABC12", "playfab", "file", "delete", "save.json",
             "--entity-id", "E1", "--entity-type", "title_player_account", "--confirm", "--output", "json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal("Deleted file: 'save.json'" + Environment.NewLine, result.Stderr);
    }

    private static MockHttpMessageHandler SuccessHandler() =>
        new(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"code":200,"status":"OK","data":{}}"""),
        });

    /// <summary>
    /// Runs a command as Main does, filling in context values first, with
    /// <see cref="Program.ApiClientFactory"/> building the client over <paramref name="handler"/>,
    /// so no real sign-in or PlayFab call is involved. An account is signed in, with saved context
    /// in a temporary state directory. The title comes from the arguments, or from
    /// <c>XBOX_PLAYFAB_TITLE_ID</c> when <paramref name="environmentTitle"/> is set, or else from
    /// <paramref name="savedTitle"/>, if saved; the developer's own variables are cleared.
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeWithClientSeamAsync(
        MockHttpMessageHandler handler,
        string[] args,
        string? savedTitle = null,
        string? environmentTitle = null)
    {
        string[] variables =
        [
            "XBOX_PLAYFAB_TITLE_ID",
            ContextProfiles.EnvironmentVariable,
            "XBOX_PLAYFAB__BASEURLTEMPLATE",
        ];
        var originalVariables = variables.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalIn = Console.In;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var httpClient = new HttpClient(handler);
        var stateDirectory = Path.Combine(Path.GetTempPath(), $"xbox-guard-{Guid.NewGuid():N}");
        Program.ApiClientFactory = configuration => new PlayFabApiClient(
            configuration,
            new FakeAuthenticationService(FakeAuthenticationService.CreateToken()),
            httpClient);

        try
        {
            foreach (var name in variables)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            Environment.SetEnvironmentVariable("XBOX_PLAYFAB_TITLE_ID", environmentTitle);
            var identity = new SignedInIdentity("tenant", "user");
            ContextStore.DefaultDirectory = () => stateDirectory;
            ContextResolver.ReadIdentityAsync = _ => Task.FromResult<SignedInIdentity?>(identity);
            if (savedTitle is not null)
            {
                await new ContextStore().UpdateAsync(
                    identity,
                    [new ContextChange("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, savedTitle)]);
            }

            Console.SetOut(stdout);
            Console.SetError(stderr);
            Console.SetIn(new StringReader(string.Empty));
            var parseResult = await Program.ResolveContextAsync(
                Program.BuildRootCommand().Parse(args, Program.NoResponseFileConfig),
                args);
            var exitCode = parseResult is null ? 1 : await parseResult.InvokeAsync();
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Program.ApiClientFactory = null;
            TestEnvironment.Reset();
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }

            foreach (var (name, value) in originalVariables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Console.SetIn(originalIn);
        }
    }
}
