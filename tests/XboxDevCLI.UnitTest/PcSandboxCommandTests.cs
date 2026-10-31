// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class PcSandboxCommandTests
{
    private const string ToolDirectory = @"C:\tools";

    // Built the same way LocateTool builds it, so the fake matches on Linux CI too.
    private static readonly string ToolPath = Path.Combine(ToolDirectory, PcSandbox.ToolFileName);

    [Fact]
    public async Task Get_ReportsTheSandboxTheToolReports()
    {
        var fake = new FakePc("XDKS.1");

        var result = await fake.InvokeAsync("pc", "sandbox", "get", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("""{"sandbox":"XDKS.1"}""", result.Stdout.Trim());
        Assert.Empty(result.Stderr);
        var run = Assert.Single(fake.ToolRuns);
        Assert.Equal(ToolPath, run.Tool);
        Assert.Equal(["/get"], run.Arguments);
    }

    [Fact]
    public async Task Get_TableOutput_HasOnlyASandboxColumn()
    {
        var fake = new FakePc(PcSandbox.Retail);

        var result = await fake.InvokeAsync("pc", "sandbox", "get", "--output", "table");

        Assert.Equal(0, result.ExitCode);
        var lines = result.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("SANDBOX", lines[0].Trim());
        Assert.Equal("RETAIL", lines[^1].Trim());
    }

    [Fact]
    public async Task Get_ToolFails_ShowsTheToolOutputAndWhereToGetHelp()
    {
        var fake = new FakePc("XDKS.1")
        {
            GetExitCode = PcSandbox.GamingServicesFailureExitCode,
            GetOutput = ["Error: Gaming Services is not installed."],
        };

        var result = await fake.InvokeAsync("pc", "sandbox", "get");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Gaming Services is not installed.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Gaming Services Repair Tool", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(PcSandbox.TroubleshootingUrl, result.Stderr, StringComparison.Ordinal);
        Assert.Contains("XblPCSandbox /feedback", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ToolSucceedsWithoutNamingASandbox_Fails()
    {
        // XblPCSandbox /get prints a COM failure and still exits with 0.
        var fake = new FakePc("XDKS.1") { GetOutput = ["Failed to get sandbox: Class not registered"] };

        var result = await fake.InvokeAsync("pc", "sandbox", "get");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Class not registered", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("/get did not report a sandbox", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set", "XDKS.1", "--confirm")]
    [InlineData("launch-apps")]
    public async Task ToolNotFound_ExplainsHowToInstallIt(params string[] action)
    {
        var fake = new FakePc(PcSandbox.Retail) { ToolInstalled = false };

        var result = await fake.InvokeAsync(["pc", "sandbox", .. action]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("was not found next to the CLI or on PATH", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("XBOX_XBLPCSANDBOX__PATH", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(fake.ToolRuns);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("get", "--output", "table")]
    [InlineData("set", "XDKS.1", "--confirm")]
    [InlineData("set", "XDKS.1")]
    [InlineData("set", "RETAIL", "--yes", "--output", "table")]
    [InlineData("set", "not a sandbox!", "--confirm")]
    [InlineData("launch-apps")]
    public async Task OffWindows_FailsWithoutStdout(params string[] action)
    {
        var fake = new FakePc(PcSandbox.Retail) { IsWindows = false };

        var result = await fake.InvokeAsync(["pc", "sandbox", .. action]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("only be read or changed on Windows", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(fake.ToolRuns);
    }

    [Fact]
    public async Task Set_Confirmed_RunsSetLikeTheToolAndReportsTheSandboxInUse()
    {
        var fake = new FakePc(PcSandbox.Retail);

        var result = await fake.InvokeAsync(
            "pc", "sandbox", "set", "xdks.1", "--confirm", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [["/get"], ["/set", "XDKS.1"]],
            fake.ToolRuns.Select(run => run.Arguments));
        Assert.Equal("""{"sandbox":"XDKS.1","previousSandbox":"RETAIL"}""", result.Stdout.Trim());
        Assert.Contains("Switching this PC from RETAIL to XDKS.1.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("  Sandbox is now: XDKS.1", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RETAIL", false, new[] { "/retail" })]
    [InlineData("retail", true, new[] { "/retail", "/noApps" })]
    [InlineData("XDKS.1", true, new[] { "/set", "XDKS.1", "/noApps" })]
    public async Task Set_MapsToTheToolArguments(string sandbox, bool noApps, string[] expected)
    {
        var fake = new FakePc("ABCDEF.1");
        string[] args = ["pc", "sandbox", "set", sandbox, "--yes"];

        var result = await fake.InvokeAsync(noApps ? [.. args, "--no-apps"] : args);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, Assert.Single(fake.SwitchRuns).Arguments);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(sandbox.ToUpperInvariant(), json.RootElement.GetProperty("sandbox").GetString());
        Assert.Equal("ABCDEF.1", json.RootElement.GetProperty("previousSandbox").GetString());
    }

    [Fact]
    public async Task Set_SameSandbox_SwitchesAgainAfterSayingWhy()
    {
        var fake = new FakePc("XDKS.1") { IsInteractive = true, Answer = "y" };

        var result = await fake.InvokeAsync(
            "pc", "sandbox", "set", "xdks.1", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["/set", "XDKS.1"], Assert.Single(fake.SwitchRuns).Arguments);
        Assert.Contains("This PC already uses XDKS.1. Switching again restarts", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Switching this PC to XDKS.1 again.", result.Stderr, StringComparison.Ordinal);
        Assert.Equal("""{"sandbox":"XDKS.1","previousSandbox":"XDKS.1"}""", result.Stdout.Trim());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Set_PromptSaysWhetherAppsReopen(bool noApps, bool mentionsApps)
    {
        var fake = new FakePc(PcSandbox.Retail) { IsInteractive = true, Answer = "n" };
        string[] args = ["pc", "sandbox", "set", "XDKS.1"];

        var result = await fake.InvokeAsync(noApps ? [.. args, "--no-apps"] : args);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(fake.SwitchRuns);
        Assert.Contains("restarts XBOX services (XblAuthManager, GamingServices, and DiagTrack)", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(
            mentionsApps,
            result.Stderr.Contains("reopens the Microsoft Store and XBOX app", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Set_WhenGetFails_StopsBeforeThePrompt()
    {
        var fake = new FakePc(PcSandbox.Retail)
        {
            IsInteractive = true,
            Answer = "y",
            GetExitCode = PcSandbox.GamingServicesFailureExitCode,
        };

        var result = await fake.InvokeAsync("pc", "sandbox", "set", "XDKS.1");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Empty(fake.SwitchRuns);
        Assert.DoesNotContain("[y/N]", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Gaming Services is missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_CancelledDuringThePrompt_DoesNotRunTheSwitch()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakePc(PcSandbox.Retail)
        {
            IsInteractive = true,
            Answer = "y",
            OnPrompt = cancellation.Cancel,
            Cancellation = cancellation.Token,
        };

        var result = await fake.InvokeAsync("pc", "sandbox", "set", "XDKS.1");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(fake.SwitchRuns);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task Set_CancelledWhileTheToolRuns_WarnsTheSwitchMayStillFinish()
    {
        var fake = new FakePc(PcSandbox.Retail) { SwitchIsCancelled = true };

        var result = await fake.InvokeAsync("pc", "sandbox", "set", "XDKS.1", "--confirm");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("the switch may still complete", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/elevated-set")]
    [InlineData("XDKS.1 /noApps")]
    [InlineData("..")]
    [InlineData("XDKS_1")]
    [InlineData("XDKS-1")]
    public async Task Set_RejectsAnImplausibleIdBeforeRunningTheTool(string sandbox)
    {
        var fake = new FakePc(PcSandbox.Retail);

        var result = await fake.InvokeAsync("pc", "sandbox", "set", sandbox, "--confirm");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("is not a valid sandbox ID", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(fake.ToolRuns);
    }

    [Fact]
    public async Task Set_WithoutConfirmationOutsideATerminal_FailsClosed()
    {
        var fake = new FakePc(PcSandbox.Retail) { IsInteractive = false };

        var result = await fake.InvokeAsync("pc", "sandbox", "set", "XDKS.1");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Re-run with --confirm", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(fake.SwitchRuns);
    }

    [Theory]
    [InlineData("y", 0, 1)]
    [InlineData("n", 1, 0)]
    public async Task Set_InATerminal_FollowsThePromptAnswer(string answer, int exitCode, int runs)
    {
        var fake = new FakePc(PcSandbox.Retail) { IsInteractive = true, Answer = answer };

        var result = await fake.InvokeAsync("pc", "sandbox", "set", "XDKS.1");

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(runs, fake.SwitchRuns.Count);
        Assert.Contains("Switch the sandbox? [y/N]", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1223, "administrator prompt was declined")]
    [InlineData(unchecked((int)0x80131509), "Gaming Services is missing, out of date, or not working")]
    [InlineData(5, "failed with exit code 0x00000005")]
    public async Task Set_ToolFailure_ExplainsTheExitCodeAndWhereToGetHelp(int toolExitCode, string expected)
    {
        var fake = new FakePc(PcSandbox.Retail) { SwitchExitCode = toolExitCode };

        var result = await fake.InvokeAsync("pc", "sandbox", "set", "XDKS.1", "--confirm");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
        // The tool already printed the troubleshooting link, so the CLI adds only the feedback hint.
        Assert.Equal(2, result.Stderr.Split(PcSandbox.TroubleshootingUrl).Length);
        Assert.Contains("XblPCSandbox /feedback", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_ToolSucceedsButTheSandboxDidNotChange_Fails()
    {
        var fake = new FakePc(PcSandbox.Retail) { SwitchApplies = false };

        var result = await fake.InvokeAsync("pc", "sandbox", "set", "XDKS.1", "--confirm");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("this PC uses RETAIL rather than XDKS.1", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Run the switch again", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_ToolDoesNotReportTheNewSandbox_AsksAgain()
    {
        var fake = new FakePc(PcSandbox.Retail) { SwitchReportsResult = false };

        var result = await fake.InvokeAsync(
            "pc", "sandbox", "set", "XDKS.1", "--confirm", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [["/get"], ["/set", "XDKS.1"], ["/get"]],
            fake.ToolRuns.Select(run => run.Arguments));
        Assert.Equal("""{"sandbox":"XDKS.1","previousSandbox":"RETAIL"}""", result.Stdout.Trim());
    }

    [Fact]
    public async Task Set_TableOutput_RendersThePreviousSandbox()
    {
        var fake = new FakePc(PcSandbox.Retail);

        var result = await fake.InvokeAsync(
            "pc", "sandbox", "set", "XDKS.1", "--confirm", "--output", "table");

        Assert.Equal(0, result.ExitCode);
        var lines = result.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("PREVIOUS SANDBOX", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, line => line.Contains("XDKS.1", StringComparison.Ordinal)
            && line.Contains("RETAIL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaunchApps_RunsLaunchAppsAndReportsTheSandbox()
    {
        var fake = new FakePc("XDKS.1");

        var result = await fake.InvokeAsync(
            "pc", "sandbox", "launch-apps", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([["/get"], ["/launchApps"]], fake.ToolRuns.Select(run => run.Arguments));
        Assert.Equal("""{"sandbox":"XDKS.1"}""", result.Stdout.Trim());
        Assert.Contains("Reopening the Microsoft Store and XBOX app in XDKS.1.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchApps_ToolFails_ExplainsTheExitCode()
    {
        var fake = new FakePc("XDKS.1") { LaunchAppsExitCode = 5 };

        var result = await fake.InvokeAsync("pc", "sandbox", "launch-apps");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("failed with exit code 0x00000005", result.Stderr, StringComparison.Ordinal);
        // The tool printed no link this time, so the CLI adds it.
        Assert.Contains(PcSandbox.TroubleshootingUrl, result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "Current Sandbox: XDKS.1" }, "XDKS.1")]
    [InlineData(new[] { "  current sandbox:   RETAIL  " }, "RETAIL")]
    [InlineData(new[] { "Current Sandbox: RETAIL", "Switching to sandbox XDKS.1", "Current Sandbox: XDKS.1" }, "XDKS.1")]
    [InlineData(new[] { "Current Sandbox:" }, null)]
    [InlineData(new[] { "Failed to get sandbox: boom" }, null)]
    [InlineData(new string[0], null)]
    public void ParseSandbox_ReadsTheLastLineWithTheValue(string[] lines, string? expected)
    {
        Assert.Equal(expected, PcSandbox.ParseSandbox(lines, PcSandbox.CurrentSandboxPrefix));
    }

    [Fact]
    public async Task PcRegistration_IsExperimentalAndHelpStatesTheSafety()
    {
        var root = Program.BuildRootCommand();
        var pc = root.Subcommands.Single(command => command.Name == "pc");
        Assert.Equal(CommandStage.Experimental, CommandMetadataRegistry.GetMetadata(pc).Stage);
        using var rootHelp = new StringWriter();
        CommandHelpWriter.WriteRoot(rootHelp, root, "xbox", width: 200);
        Assert.Contains("Inspect and change settings on this PC [Experimental]", rootHelp.ToString());

        foreach (var path in new[] { "", "sandbox", "sandbox get", "sandbox set", "sandbox launch-apps" })
        {
            var segments = new[] { "pc" }.Concat(path.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            using var output = new StringWriter();
            using var error = new StringWriter();
            var parsed = root.Parse([.. segments, "--help"], Program.NoResponseFileConfig);
            Assert.Equal(0, await parsed.InvokeAsync(new() { Output = output, Error = error }));
            Assert.Contains("EXPERIMENTAL" + Environment.NewLine, output.ToString());
            Assert.Empty(error.ToString());
            if (path is "sandbox set" or "sandbox launch-apps")
            {
                var unwrapped = string.Join(' ', output.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                Assert.Contains(
                    "Changes this PC, such as its XBOX sandbox or XBOX apps. Does not modify remote state.",
                    unwrapped);
            }
        }
    }

    [Fact]
    public async Task RunProcess_OnCancel_StopsTheTool()
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.SystemDirectory, "ping.exe"), new[] { "-n", "60", "127.0.0.1" })
            : ("/bin/sleep", new[] { "60" });
        using var diagnostics = new StringWriter();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var started = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PcSandbox.RunProcessAsync(fileName, arguments, diagnostics, cancellation.Token));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), "The tool was not stopped.");
    }

    [Fact]
    public void LocateTool_PrefersAConfiguredPathAndRejectsAMissingOne()
    {
        var fake = new FakePc(PcSandbox.Retail);

        Assert.Equal(ToolPath, PcSandbox.LocateTool($" {ToolPath} ", fake.Dependencies));
        var missing = Assert.Throws<FileNotFoundException>(
            () => PcSandbox.LocateTool(@"D:\missing\XblPCSandbox.exe", fake.Dependencies));
        Assert.Contains("XblPCSandbox:Path is set to", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stands in for XblPCSandbox.exe and the OS. Prints what the real tool prints, so the commands
    /// parse the same lines they parse on a PC, without touching the machine.
    /// </summary>
    private sealed class FakePc(string sandbox)
    {
        private string inUse = sandbox;

        public bool IsWindows { get; init; } = true;
        public bool IsInteractive { get; init; }
        public string? Answer { get; init; }
        public bool ToolInstalled { get; init; } = true;
        public int GetExitCode { get; init; }
        public string[]? GetOutput { get; init; }
        public int SwitchExitCode { get; init; }
        public bool SwitchApplies { get; init; } = true;
        public bool SwitchReportsResult { get; init; } = true;
        public bool SwitchIsCancelled { get; init; }
        public int LaunchAppsExitCode { get; init; }
        public Action? OnPrompt { get; init; }
        public CancellationToken Cancellation { get; init; }
        public List<(string Tool, IReadOnlyList<string> Arguments)> ToolRuns { get; } = [];

        public List<(string Tool, IReadOnlyList<string> Arguments)> SwitchRuns =>
            ToolRuns.Where(run => run.Arguments[0] is "/set" or "/retail").ToList();

        public PcSandboxDependencies Dependencies => new(
            () => IsWindows,
            path => ToolInstalled && string.Equals(path, ToolPath, StringComparison.OrdinalIgnoreCase),
            () => [@"C:\elsewhere", ToolDirectory],
            (tool, arguments, diagnostics, _) => Task.FromResult(RunTool(tool, arguments, diagnostics)),
            () => IsInteractive,
            () =>
            {
                OnPrompt?.Invoke();
                return Answer;
            });

        private int RunTool(string tool, IReadOnlyList<string> arguments, TextWriter diagnostics)
        {
            ToolRuns.Add((tool, arguments));
            switch (arguments[0])
            {
                case "/get":
                    foreach (var line in GetOutput ?? [$"Current Sandbox: {inUse}"])
                    {
                        diagnostics.WriteLine($"  {line}");
                    }

                    return GetExitCode;

                case "/launchApps":
                    return LaunchAppsExitCode;

                default:
                    var target = arguments[0] == "/retail" ? PcSandbox.Retail : arguments[1];
                    diagnostics.WriteLine($"  Current Sandbox: {inUse}");
                    diagnostics.WriteLine($"  Switching to sandbox {target}");
                    if (SwitchIsCancelled)
                    {
                        throw new OperationCanceledException();
                    }

                    if (SwitchExitCode != 0)
                    {
                        // As the real tool does on any failure.
                        diagnostics.WriteLine(FormattableString.Invariant(
                            $"  Failed to switch sandbox and restart apps: 0x{SwitchExitCode:X8}"));
                        diagnostics.WriteLine($"  For troubleshooting help, visit {PcSandbox.TroubleshootingUrl}");
                        return SwitchExitCode;
                    }

                    if (SwitchApplies)
                    {
                        inUse = target;
                    }

                    if (SwitchReportsResult)
                    {
                        diagnostics.WriteLine($"  Sandbox is now: {inUse}");
                    }

                    return 0;
            }
        }

        public async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(params string[] args)
        {
            var root = Program.BuildRootCommand(
                pcCommandFactory: () => PcCommandFactory.Create(Dependencies));
            var oldOut = Console.Out;
            var oldError = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var exitCode = await root.Parse(args, Program.NoResponseFileConfig)
                    .InvokeAsync(cancellationToken: Cancellation);
                return (exitCode, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldError);
            }
        }
    }
}
