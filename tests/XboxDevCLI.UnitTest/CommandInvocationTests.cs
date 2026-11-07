// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Telemetry;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// How a command runs: each declares what it needs prepared, preparation happens before its
/// action, and expected failures from either are reported once, on stderr, with exit code 1.
/// </summary>
/// <remarks>
/// Environment variables and console streams are process-global, so this shares the collection
/// that serializes every test that sets or redirects them.
/// </remarks>
[Collection("ConsoleRedirect")]
public sealed class CommandInvocationTests : IDisposable
{
    public void Dispose() => TestEnvironment.Reset();

    /// <summary>
    /// Every command that runs something declares what it needs, on itself or a parent, so none
    /// relies on the default by accident.
    /// </summary>
    [Fact]
    public void EveryCommandWithAnAction_DeclaresARequirement()
    {
        var undeclared = AllCommands(Program.BuildRootCommand())
            .Where(command => command.Action is not null and not (HelpAction or CommandHelpAction or HelpRenderingAction))
            .Where(command => CommandRequirements.FindDeclared(command) is null)
            .Select(Path)
            .ToList();

        Assert.Empty(undeclared);
    }

    [Theory]
    [InlineData("info", "None")]
    [InlineData("upgrade", "None")]
    [InlineData("feedback", "None")]
    [InlineData("context key list", "None")]
    [InlineData("pc", "Configuration")]
    [InlineData("auth status", "Authentication")]
    [InlineData("context get", "Authentication")]
    [InlineData("playfab title-data get", "Authentication")]
    public void Commands_DeclareWhatTheyNeed(string command, string expected)
    {
        var parseResult = Program.BuildRootCommand().Parse(command, Program.NoResponseFileConfig);

        Assert.Empty(parseResult.Errors);
        Assert.Equal(Enum.Parse<CommandRequirement>(expected), CommandRequirements.For(parseResult.CommandResult.Command));
    }

    [Fact]
    public void UndeclaredCommand_RequiresAuthentication() =>
        Assert.Equal(CommandRequirement.Authentication, CommandRequirements.For(new Command("probe")));

    /// <summary>
    /// Help, the version, and offline commands never read the sign-in settings, so settings a
    /// command that signs in would refuse don't stop them.
    /// </summary>
    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("auth status --help")]
    [InlineData("--client-id ignored-value info")]
    [InlineData("--profile test context key list")]
    public async Task HelpVersionAndOfflineCommands_IgnoreInvalidSignInSettings(string command)
    {
        // A service-principal mode with none of the settings it needs.
        using var automation = TestEnvironment.UseAuthenticationMode("clientsecret");

        var (exitCode, stdout, stderr) = await MainAsync(command.Split(' '));

        Assert.Equal(0, exitCode);
        Assert.NotEmpty(stdout);
        Assert.DoesNotContain("XBOX_AUTHENTICATION__", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("doesn't apply", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The endpoint warning protects access tokens, so only a prepared command that calls the
    /// endpoint writes it, once. Help, the version, parse errors, and offline commands never send a
    /// token and stay quiet, even though the configuration is loaded and telemetry reads it.
    /// </summary>
    [Theory]
    [InlineData("--help", 0)]
    [InlineData("--disable-telemetry --help", 0)]
    [InlineData("--version", 0)]
    [InlineData("info", 0)]
    [InlineData("context key list", 0)]
    [InlineData("auth status --nope", 0)]
    [InlineData("playfab title-data get", 1)]
    public async Task EndpointWarning_IsWrittenOnlyWhenConfigurationIsPrepared(string command, int expectedWarnings)
    {
        using var automation = TestEnvironment.UseAuthenticationMode("clientsecret");
        var original = Environment.GetEnvironmentVariable("XBOX_PLAYFAB__BASEURLTEMPLATE");
        Environment.SetEnvironmentVariable("XBOX_PLAYFAB__BASEURLTEMPLATE", "https://{0}.nodpublishers.com");
        try
        {
            var (_, _, stderr) = await MainAsync(command.Split(' '));

            Assert.Equal(
                expectedWarnings,
                stderr.Split(Environment.NewLine).Count(line => line.StartsWith("Warning:", StringComparison.Ordinal)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XBOX_PLAYFAB__BASEURLTEMPLATE", original);
        }
    }

    /// <summary>
    /// Every command that prepares configuration says which endpoints it sends tokens to, so none
    /// skips the endpoint warning by omission, and every command that calls PlayFab names it.
    /// </summary>
    [Fact]
    public void EveryPreparedCommand_DeclaresTheEndpointsItCalls()
    {
        var commands = AllCommands(Program.BuildRootCommand())
            .Where(command => command.Action is not null and not (HelpAction or CommandHelpAction or HelpRenderingAction))
            .Where(command => CommandRequirements.For(command) != CommandRequirement.None)
            .ToList();

        Assert.Empty(commands.Where(command => CommandEndpoints.FindDeclared(command) is null).Select(Path));
        Assert.Empty(commands
            .Where(CommandMetadataRegistry.CallsPlayFab)
            .Where(command => !CommandEndpoints.For(command).HasFlag(ServiceEndpoints.PlayFab))
            .Select(Path));
    }

    /// <summary>
    /// Preparation writes the warning for each changed endpoint the command calls, and only those,
    /// once however often it runs.
    /// </summary>
    [Theory]
    [InlineData("auth status", false, false)]
    [InlineData("context get", false, false)]
    [InlineData("playfab title-data get", true, false)]
    [InlineData("seller list", false, true)]
    [InlineData("xbl schema get", false, true)]
    public void EndpointWarnings_AreWrittenForTheEndpointsTheCommandCalls(string command, bool playFab, bool xboxPartner)
    {
        using var automation = TestEnvironment.UseAuthenticationMode("managedidentity");
        var stderr = WithChangedEndpoints(() =>
        {
            var parseResult = Program.BuildRootCommand().Parse(command, Program.NoResponseFileConfig);
            Program.Prepare(parseResult);
            Program.Prepare(parseResult);
        });

        var warnings = stderr.Split(Environment.NewLine).Where(line => line.StartsWith("Warning:", StringComparison.Ordinal)).ToList();
        Assert.Equal(playFab ? 1 : 0, warnings.Count(line => line.Contains("PlayFab API endpoint", StringComparison.Ordinal)));
        Assert.Equal(xboxPartner ? 1 : 0, warnings.Count(line => line.Contains("service endpoint", StringComparison.Ordinal)));
        Assert.Equal((playFab ? 1 : 0) + (xboxPartner ? 1 : 0), warnings.Count);
    }

    /// <summary>A command's <c>--base-url</c> is the endpoint it calls, so it's what the warning names.</summary>
    [Fact]
    public void BaseUrlOption_IsTheEndpointWarnedAbout()
    {
        using var automation = TestEnvironment.UseAuthenticationMode("managedidentity");
        var parseResult = Program.BuildRootCommand().Parse(
            "sandbox list --base-url https://option.nodpublishers.com", Program.NoResponseFileConfig);
        var stderr = WithChangedEndpoints(() => Program.Prepare(parseResult));

        Assert.Contains("overridden to 'https://option.nodpublishers.com'", stderr, StringComparison.Ordinal);
        Assert.Equal("https://option.nodpublishers.com", Program.GetConfiguration(parseResult)["XboxPartner:BaseUrl"]);
    }

    /// <summary>
    /// Loading configuration that can't be read keeps the error for preparation to report, and
    /// writes and throws nothing itself.
    /// </summary>
    [Fact]
    public void LoadingUnreadableConfiguration_KeepsTheErrorQuietly()
    {
        using var unreadable = TestSettings.UseUnreadable();
        var parseResult = Program.BuildRootCommand().Parse(
            ["playfab", "title-data", "get"], Program.NoResponseFileConfig);
        CommandInvocation? invocation = null;

        var stderr = WithChangedEndpoints(() => invocation = Program.GetInvocation(parseResult));

        Assert.Empty(stderr);
        Assert.Null(invocation!.LoadedConfiguration.Configuration);
        Assert.IsType<InvalidDataException>(invocation.LoadedConfiguration.Error);
        Assert.Throws<InvalidDataException>(() => Program.Prepare(parseResult));
    }

    /// <summary>
    /// When configuration can't be loaded, telemetry still reads its environment variables, so a
    /// preparation failure is recorded, and still honors <c>--disable-telemetry</c>.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UnreadableConfiguration_TelemetryFallsBackToEnvironmentVariables(bool disableTelemetry, bool expectedEnabled)
    {
        using var unreadable = TestSettings.UseUnreadable();
        string[] args = ["seller", "list"];
        var parseResult = Program.BuildRootCommand().Parse(
            disableTelemetry ? ["--disable-telemetry", .. args] : args, Program.NoResponseFileConfig);
        var originalSampling = Environment.GetEnvironmentVariable("XBOX_TELEMETRY__DEPENDENCYTRACESAMPLINGRATIO");
        Environment.SetEnvironmentVariable("XBOX_TELEMETRY__DEPENDENCYTRACESAMPLINGRATIO", "0.5");
        try
        {
            var options = TelemetryOptions.FromConfiguration(
                Program.GetTelemetryConfiguration(parseResult), _ => null, "InstrumentationKey=00000000-0000-0000-0000-000000000000");

            Assert.Null(Program.GetInvocation(parseResult).LoadedConfiguration.Configuration);
            Assert.Equal(expectedEnabled, options.Enabled);
            Assert.Equal(0.5, options.DependencyTraceSamplingRatio);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XBOX_TELEMETRY__DEPENDENCYTRACESAMPLINGRATIO", originalSampling);
        }
    }

    /// <summary>A command that signs in reports invalid sign-in settings before its action runs.</summary>
    [Fact]
    public async Task InvalidSignInSettings_AreReportedBeforeTheActionRuns()
    {
        using var automation = TestEnvironment.UseAuthenticationMode("clientsecret");
        var ran = false;
        var root = Root(Probe(CommandRequirement.Authentication, () => ran = true));

        var (exitCode, stdout, stderr) = await InvokeAsync(root, "probe");

        Assert.Equal(1, exitCode);
        Assert.False(ran);
        Assert.Empty(stdout);
        Assert.Contains("XBOX_AUTHENTICATION__", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Configuration that can't be read fails a command that reads it, before its action, and
    /// doesn't affect one that doesn't.
    /// </summary>
    [Theory]
    [InlineData("None", 0)]
    [InlineData("Configuration", 1)]
    [InlineData("Authentication", 1)]
    public async Task UnreadableConfiguration_FailsOnlyCommandsThatReadIt(string requirement, int expectedExit)
    {
        using var unreadable = TestSettings.UseUnreadable();
        var ran = false;
        var root = Root(Probe(Enum.Parse<CommandRequirement>(requirement), () => ran = true));

        var (exitCode, _, stderr) = await InvokeAsync(root, "probe");

        Assert.Equal(expectedExit, exitCode);
        Assert.Equal(expectedExit == 0, ran);
        Assert.Equal(expectedExit == 0, stderr.Length == 0);
    }

    /// <summary>
    /// An expected failure from an action is one message on stderr, with line breaks kept and
    /// other control characters removed, so it can't rewrite the terminal.
    /// </summary>
    [Fact]
    public async Task ExpectedFailure_IsReportedSafelyOnStderr()
    {
        var root = Root(Probe(CommandRequirement.None, () =>
            throw new InvalidOperationException("First line\u001b[2J\r\nSecond\u0007 line")));

        var (exitCode, stdout, stderr) = await InvokeAsync(root, "probe");

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Equal($"First line[2J{Environment.NewLine}Second line{Environment.NewLine}", stderr);
    }

    [Fact]
    public async Task ExpectedFailure_UsesTheFormatTheCommandDeclares()
    {
        var probe = Probe(CommandRequirement.None, () => throw new IOException("disk full"))
            .FormatsErrors((_, message) => $"Probe failed: {message}");

        var (exitCode, _, stderr) = await InvokeAsync(Root(probe), "probe");

        Assert.Equal(1, exitCode);
        Assert.Equal($"Probe failed: disk full{Environment.NewLine}", stderr);
    }

    /// <summary>Cancellation and bugs aren't expected failures, so the boundary lets them through.</summary>
    [Fact]
    public async Task CancellationAndBugs_AreNotReported()
    {
        Assert.False(CommandErrorBoundary.IsExpected(new OperationCanceledException()));
        Assert.False(CommandErrorBoundary.IsExpected(new NullReferenceException()));
        Assert.False(CommandErrorBoundary.IsExpected(new ObjectDisposedException("x")));

        var root = Root(Probe(CommandRequirement.None, () => throw new OperationCanceledException()));
        var parseResult = root.Parse("probe", Program.NoResponseFileConfig);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            parseResult.InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false }));
    }

    private static RootCommand Root(Command probe)
    {
        var root = new RootCommand("test") { probe };
        CommandErrorBoundary.Install(root);
        return root;
    }

    private static Command Probe(CommandRequirement requirement, Action action)
    {
        var command = new Command("probe");
        command.SetAction(_ => action());
        return command.Requires(requirement);
    }

    /// <summary>Runs <paramref name="run"/> with both service endpoints changed, returning its stderr.</summary>
    private static string WithChangedEndpoints(Action run)
    {
        var originalPlayFab = Environment.GetEnvironmentVariable("XBOX_PLAYFAB__BASEURLTEMPLATE");
        var originalXboxPartner = Environment.GetEnvironmentVariable("XBOX_XBOXPARTNER__BASEURL");
        var originalError = Console.Error;
        using var stderr = new StringWriter();
        Environment.SetEnvironmentVariable("XBOX_PLAYFAB__BASEURLTEMPLATE", "https://{0}.nodpublishers.com");
        Environment.SetEnvironmentVariable("XBOX_XBOXPARTNER__BASEURL", "https://xbox.nodpublishers.com");
        Console.SetError(stderr);
        try
        {
            run();
            return stderr.ToString();
        }
        finally
        {
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable("XBOX_PLAYFAB__BASEURLTEMPLATE", originalPlayFab);
            Environment.SetEnvironmentVariable("XBOX_XBOXPARTNER__BASEURL", originalXboxPartner);
        }
    }

    private static IEnumerable<Command> AllCommands(Command command) =>
        [command, .. command.Subcommands.SelectMany(AllCommands)];

    private static string Path(Command command) =>
        string.Join(' ', command.Parents.OfType<Command>().Reverse().Select(parent => parent.Name).Append(command.Name));

    private static Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(RootCommand root, params string[] args) =>
        CaptureAsync(() => root.Parse(args, Program.NoResponseFileConfig).InvokeAsync());

    private static Task<(int ExitCode, string Stdout, string Stderr)> MainAsync(params string[] args) =>
        CaptureAsync(() => Program.Main(args));

    private static async Task<(int ExitCode, string Stdout, string Stderr)> CaptureAsync(Func<Task<int>> run)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exitCode = await run();
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
