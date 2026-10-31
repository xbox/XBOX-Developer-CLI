// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;
using XboxDevCLI.CommandLine;
using XboxDevCLI.PlayFab.Generated;
using XboxDevCLI.Safety;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class HelpCommandTests
{
    [Theory]
    [InlineData("playfab api")]
    [InlineData("playfab mps build get")]
    [InlineData("playfab mps build create")]
    public async Task RequestBodyHelp_ExplainsStandardInput(string path)
    {
        string[] segments = path.Split(' ');
        var flag = await InvokeAsync([.. segments, "--help"]);
        var command = await InvokeAsync(["help", .. segments]);

        Assert.Equal(0, flag.ExitCode);
        Assert.Equal(0, command.ExitCode);
        Assert.Empty(flag.Stderr);
        Assert.Empty(command.Stderr);
        Assert.Equal(flag.Stdout, command.Stdout);
        Assert.Contains("or - to read UTF-8 from stdin until EOF", NormalizeWhitespace(flag.Stdout));
    }

    [Theory]
    [InlineData("playfab mps asset upload", "--file-path")]
    [InlineData("xbl document commit", "--files")]
    public async Task LocalFileInputHelp_UsesCanonicalFileOption(string path, string oldOption)
    {
        string[] segments = path.Split(' ');
        var flag = await InvokeAsync([.. segments, "--help"]);
        var command = await InvokeAsync(["help", .. segments]);

        Assert.Equal(0, flag.ExitCode);
        Assert.Equal(0, command.ExitCode);
        Assert.Empty(flag.Stderr);
        Assert.Empty(command.Stderr);
        Assert.Equal(flag.Stdout, command.Stdout);
        Assert.Contains("--file ", flag.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(oldOption, flag.Stdout, StringComparison.Ordinal);
        if (path == "xbl document commit")
        {
            Assert.Contains("Repeat --file", flag.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RootHelpInvocations_ProduceIdenticalOutput()
    {
        var invocations = new[]
        {
            Array.Empty<string>(),
            new[] { "help" },
            new[] { "--help" },
            new[] { "-h" },
        };

        var results = new List<CapturedInvocation>();
        foreach (var invocation in invocations)
        {
            results.Add(await InvokeAsync(invocation));
        }

        var expectedOutput = results[0].Stdout;
        Assert.False(string.IsNullOrWhiteSpace(expectedOutput));

        foreach (var result in results)
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(expectedOutput, result.Stdout);
            Assert.Equal(string.Empty, result.Stderr);
            Assert.DoesNotContain("\u001b[", result.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RootAndGroupHelp_BoldAuthoredSectionHeadingsWhenAnsiIsEnabled()
    {
        var rootCommand = Program.BuildRootCommand();
        var authCommand = Assert.Single(
            rootCommand.Subcommands,
            command => command.Name == "auth");
        using var rootWriter = new StringWriter();
        using var groupWriter = new StringWriter();

        CommandHelpWriter.WriteRoot(
            rootWriter,
            rootCommand,
            CliIdentity.CommandName,
            useAnsi: true);
        CommandHelpWriter.Write(
            groupWriter,
            rootCommand,
            authCommand,
            CliIdentity.CommandName,
            useAnsi: true);

        Assert.Contains("\u001b[1mUSAGE\u001b[0m", rootWriter.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "\u001b[1mGET STARTED\u001b[0m",
            rootWriter.ToString(),
            StringComparison.Ordinal);
        Assert.StartsWith(
            $"\u001b[1;38;2;139;240;0mXBOX\u001b[0m CLI: {CliIdentity.Tagline}",
            rootWriter.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "\u001b[1mCORE COMMANDS\u001b[0m",
            rootWriter.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "\u001b[1mAVAILABLE COMMANDS\u001b[0m",
            groupWriter.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "\u001b[1mINHERITED FLAGS\u001b[0m",
            groupWriter.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RootHelp_UsesTaskOrientedSections()
    {
        var result = await InvokeAsync([]);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith(
            $"{CliIdentity.DisplayName}: {CliIdentity.Tagline}",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("GET STARTED", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("USAGE", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("CORE COMMANDS", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("SUPPORT COMMANDS", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("auth:", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("playfab:", result.Stdout, StringComparison.Ordinal);
        Assert.Contains($"{Environment.NewLine}FLAGS{Environment.NewLine}", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("TARGET AND OUTPUT FLAGS", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("CONFIGURATION FLAGS", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("SAFETY AND DIAGNOSTIC FLAGS", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            $"{Environment.NewLine}{Environment.NewLine}  --config-env",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{Environment.NewLine}{Environment.NewLine}  --verbose",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{Environment.NewLine}{Environment.NewLine}  -h, --help",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("EXAMPLES", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("$ xbox auth status", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            "$ xbox playfab title-data get --playfab-title-id abc123",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("$ xbox help playfab file list", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--data-only", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--confirm", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("LEARN MORE", result.Stdout, StringComparison.Ordinal);
        Assert.True(
            result.Stdout.IndexOf("GET STARTED", StringComparison.Ordinal)
            < result.Stdout.IndexOf("USAGE", StringComparison.Ordinal));
        Assert.Equal(
            1,
            result.Stdout.Split(
                $"$ {CliIdentity.CommandName} auth login",
                StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task RootHelp_AlignsAllDescriptionsToOneColumn()
    {
        var result = await InvokeAsync([]);
        var lines = result.Stdout.Split(Environment.NewLine);

        var auth = Assert.Single(
            lines,
            line => line.StartsWith("  auth:", StringComparison.Ordinal));
        var feedback = Assert.Single(
            lines,
            line => line.StartsWith("  feedback:", StringComparison.Ordinal));
        var titleId = Assert.Single(
            lines,
            line => line.StartsWith("  --playfab-title-id", StringComparison.Ordinal));
        var configEnv = Assert.Single(
            lines,
            line => line.StartsWith("  --config-env", StringComparison.Ordinal));
        var verbose = Assert.Single(
            lines,
            line => line.StartsWith("  --verbose", StringComparison.Ordinal));
        var help = Assert.Single(
            lines,
            line => line.StartsWith("  -h, --help", StringComparison.Ordinal));

        var expectedDescriptionColumn = auth.IndexOf("Authenticate", StringComparison.Ordinal);
        Assert.Equal(
            expectedDescriptionColumn,
            feedback.IndexOf("Open a CLI", StringComparison.Ordinal));
        Assert.Equal(
            expectedDescriptionColumn,
            titleId.IndexOf("Target this", StringComparison.Ordinal));
        Assert.Equal(
            expectedDescriptionColumn,
            configEnv.IndexOf("Load appsettings", StringComparison.Ordinal));
        Assert.Equal(
            expectedDescriptionColumn,
            verbose.IndexOf("Write diagnostic", StringComparison.Ordinal));
        Assert.Equal(
            expectedDescriptionColumn,
            help.IndexOf("Show help", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GroupHelp_AlignsCommandAndFlagDescriptionsToOneColumn()
    {
        var result = await InvokeAsync(["help", "playfab"]);
        var lines = result.Stdout.Split(Environment.NewLine);

        var api = Assert.Single(
            lines,
            line => line.StartsWith("  api:", StringComparison.Ordinal));
        var configEnv = Assert.Single(
            lines,
            line => line.StartsWith("  --config-env", StringComparison.Ordinal));

        Assert.Equal(
            api.IndexOf("Call any PlayFab API", StringComparison.Ordinal),
            configEnv.IndexOf("Load appsettings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeafHelp_AlignsInputOptionAndFlagDescriptionsToOneColumn()
    {
        var result = await InvokeAsync(["help", "playfab", "file", "list"]);
        var lines = result.Stdout.Split(Environment.NewLine);

        var entityId = Assert.Single(
            lines,
            line => line.StartsWith("  --entity-id", StringComparison.Ordinal));
        var configEnv = Assert.Single(
            lines,
            line => line.StartsWith("  --config-env", StringComparison.Ordinal));

        Assert.Equal(
            entityId.IndexOf("Entity ID", StringComparison.Ordinal),
            configEnv.IndexOf("Load appsettings", StringComparison.Ordinal));
    }

    [Fact]
    public void RootHelp_NarrowWidthUsesStackedRowsAndShowsAdditionalSymbols()
    {
        var rootCommand = Program.BuildRootCommand();
        rootCommand.Subcommands.Add(new Command("completion", "Generate shell completion scripts"));
        rootCommand.Options.Add(new Option<string?>("--theme")
        {
            Description = "Use a named color theme.",
        });
        using var writer = new StringWriter();

        CommandHelpWriter.WriteRoot(writer, rootCommand, CliIdentity.CommandName, width: 40);

        var output = writer.ToString();
        Assert.Contains("  auth:", output, StringComparison.Ordinal);
        Assert.Contains("  --playfab-title-id <id>", output, StringComparison.Ordinal);
        Assert.Contains("    Target this PlayFab title.", output, StringComparison.Ordinal);
        Assert.Contains("  --profile <name>", output, StringComparison.Ordinal);
        Assert.Contains("ADDITIONAL COMMANDS", output, StringComparison.Ordinal);
        Assert.Contains("completion:", output, StringComparison.Ordinal);
        Assert.Contains($"{Environment.NewLine}FLAGS{Environment.NewLine}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ADDITIONAL FLAGS", output, StringComparison.Ordinal);
        Assert.Contains("--theme <theme>", output, StringComparison.Ordinal);
    }

    [Fact]
    public void GameListHelp_NarrowWidthWrapsTableOutputProse()
    {
        var rootCommand = Program.BuildRootCommand();
        var gameCommand = Assert.Single(
            rootCommand.Subcommands,
            command => command.Name == "game");
        var listCommand = Assert.Single(
            gameCommand.Subcommands,
            command => command.Name == "list");
        using var writer = new StringWriter();

        CommandHelpWriter.Write(
            writer,
            rootCommand,
            listCommand,
            CliIdentity.CommandName,
            width: 60);

        var output = writer.ToString();
        Assert.Contains("TABLE OUTPUT", output, StringComparison.Ordinal);
        Assert.Contains("Columns may change between releases", output, StringComparison.Ordinal);
        Assert.Contains("stable shape.", output, StringComparison.Ordinal);
        var tableStart = output.IndexOf("TABLE OUTPUT", StringComparison.Ordinal);
        var tableEnd = output.IndexOf("LEARN MORE", tableStart, StringComparison.Ordinal);
        var tableOutput = output[tableStart..tableEnd];
        Assert.All(
            tableOutput.Split(Environment.NewLine),
            line => Assert.True(line.Length <= 60, $"Help line exceeded 60 columns: '{line}'"));
    }

    [Fact]
    public void RootCommand_UsesOnlyCrossPlatformHelpAliases()
    {
        var rootCommand = Program.BuildRootCommand();
        var helpOption = Assert.Single(rootCommand.Options.OfType<HelpOption>());

        Assert.Equal("--help", helpOption.Name);
        Assert.Equal(["-h"], helpOption.Aliases);

        var helpCommand = Assert.Single(
            rootCommand.Subcommands,
            command => command.Name == "help");
        Assert.False(helpCommand.Hidden);
    }

    [Fact]
    public void RootCommand_UsesAuthenticationGroupWithoutTopLevelAuthVerbs()
    {
        var rootCommand = Program.BuildRootCommand();
        var authCommand = Assert.Single(
            rootCommand.Subcommands,
            command => command.Name == "auth");

        Assert.Equal(
            ["login", "logout", "status"],
            authCommand.Subcommands.Select(command => command.Name));
        Assert.DoesNotContain(
            rootCommand.Subcommands,
            command => command.Name is "login" or "logout" or "status");
    }

    [Fact]
    public async Task AuthHelpInvocations_ProduceIdenticalOutput()
    {
        var invocations = new[]
        {
            new[] { "auth" },
            new[] { "auth", "--help" },
            new[] { "auth", "-h" },
        };

        var results = new List<CapturedInvocation>();
        foreach (var invocation in invocations)
        {
            results.Add(await InvokeAsync(invocation));
        }

        var expectedOutput = results[0].Stdout;
        Assert.False(string.IsNullOrWhiteSpace(expectedOutput));

        foreach (var result in results)
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(expectedOutput, result.Stdout);
            Assert.Equal(string.Empty, result.Stderr);
        }

        Assert.StartsWith(
            $"DESCRIPTION{Environment.NewLine}  Authenticate with Microsoft Entra ID{Environment.NewLine}",
            expectedOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            $"USAGE{Environment.NewLine}  {CliIdentity.CommandName} auth <command> [flags]",
            expectedOutput,
            StringComparison.Ordinal);
        Assert.Contains("AVAILABLE COMMANDS", expectedOutput, StringComparison.Ordinal);
        Assert.Contains("login:", expectedOutput, StringComparison.Ordinal);
        Assert.Contains("logout:", expectedOutput, StringComparison.Ordinal);
        Assert.Contains("status:", expectedOutput, StringComparison.Ordinal);
        Assert.Contains("INHERITED FLAGS", expectedOutput, StringComparison.Ordinal);
        Assert.Contains("-h, --help", expectedOutput, StringComparison.Ordinal);
        // --playfab-title-id is a configuration override consumed by Program.BuildConfiguration, so
        // it changes what 'auth status' reports and is shown everywhere. --confirm only gates
        // PlayFab destructive calls, so it is hidden outside the playfab subtree.
        Assert.Contains("--playfab-title-id", expectedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--confirm", expectedOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-?")]
    [InlineData("/h")]
    [InlineData("/?")]
    public async Task LegacyHelpAliases_AreRejected(string alias)
    {
        var result = await InvokeAsync([alias]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stderr));
    }

    [Fact]
    public async Task UnknownRootCommand_PrintsOnlyContextualUsageToStderr()
    {
        var result = await InvokeAsync(["dsadsa"]);

        var expected =
            $"""
            unknown command "dsadsa" for "{CliIdentity.CommandName}"

            Usage:  {CliIdentity.CommandName} <command> [subcommand...] [flags]

            Available commands:
              auth
              context
              feedback
              game
              help
              info
              pc
              playfab
              sandbox
              seller
              test-account
              upgrade
              xbl
            """ + Environment.NewLine;

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal(NormalizeNewLines(expected), NormalizeNewLines(result.Stderr));
        Assert.DoesNotContain(
            "Required command was not provided.",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Unrecognized command or argument",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownAuthCommand_PrintsAuthenticationCommands()
    {
        var result = await InvokeAsync(["auth", "dsadsa"]);

        var expected =
            $"""
            unknown command "dsadsa" for "{CliIdentity.CommandName} auth"

            Usage:  {CliIdentity.CommandName} auth <command> [flags]

            Available commands:
              login
              logout
              status
            """ + Environment.NewLine;

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal(NormalizeNewLines(expected), NormalizeNewLines(result.Stderr));
        Assert.DoesNotContain("Required command was not provided.", result.Stderr);
        Assert.DoesNotContain("Unrecognized command or argument", result.Stderr);
    }

    [Fact]
    public async Task UnknownNestedCommand_PrintsParentContext()
    {
        var result = await InvokeAsync(["playfab", "dsadsa"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.StartsWith(
            $"unknown command \"dsadsa\" for \"{CliIdentity.CommandName} playfab\"",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Usage:  {CliIdentity.CommandName} playfab <command> [flags]",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Available commands:{Environment.NewLine}  api",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Required command was not provided.", result.Stderr);
        Assert.DoesNotContain("Unrecognized command or argument", result.Stderr);
    }

    [Fact]
    public async Task HelpFlag_WithUnknownNestedPath_PrintsTheSameContextualError()
    {
        var helpCommand = await InvokeAsync(["help", "playfab", "dsadsa"]);
        var helpFlag = await InvokeAsync(["playfab", "dsadsa", "--help"]);

        Assert.Equal(1, helpCommand.ExitCode);
        Assert.Equal(string.Empty, helpCommand.Stdout);
        Assert.Equal(1, helpFlag.ExitCode);
        Assert.Equal(string.Empty, helpFlag.Stdout);
        Assert.Equal(helpCommand.Stderr, helpFlag.Stderr);
        Assert.Contains(
            $"Usage:  {CliIdentity.CommandName} playfab <command> [flags]",
            helpFlag.Stderr,
            StringComparison.Ordinal);
        Assert.Contains("Available commands:", helpFlag.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpFlag_WithExtraSegmentAfterLeaf_PrintsTheSameContextualError()
    {
        var helpCommand = await InvokeAsync(["help", "playfab", "file", "list", "extra"]);
        var helpFlag = await InvokeAsync(["playfab", "file", "list", "extra", "--help"]);

        Assert.Equal(1, helpCommand.ExitCode);
        Assert.Equal(string.Empty, helpCommand.Stdout);
        Assert.Equal(1, helpFlag.ExitCode);
        Assert.Equal(string.Empty, helpFlag.Stdout);
        Assert.Equal(helpCommand.Stderr, helpFlag.Stderr);
        Assert.Contains(
            $"Usage:  {CliIdentity.CommandName} playfab file list " +
            "--entity-id <entity-id> --entity-type <entity-type> [flags]",
            helpFlag.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<command>", helpFlag.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Available commands:", helpFlag.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpFlag_StillClearsUnknownOptionAndItsValue()
    {
        var result = await InvokeAsync(
            ["playfab", "file", "list", "--unknown-option", "value", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Fact]
    public async Task LeafHelp_UsesTheUnifiedRendererRatherThanSystemCommandLine()
    {
        var result = await InvokeAsync(["playfab", "title-data", "get", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.StartsWith("DESCRIPTION", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("USAGE", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("-h, --help", result.Stdout, StringComparison.Ordinal);

        // The stock System.CommandLine renderer's headings must no longer appear anywhere.
        Assert.DoesNotContain("Description:", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage:", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Options:", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("-?", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("/?", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryCommandPath))]
    public async Task HelpCommandAndHelpFlag_AreEquivalentAtEveryDepth(string commandPath)
    {
        var segments = commandPath.Split(' ');

        var viaHelpCommand = await InvokeAsync([.. new[] { "help" }.Concat(segments)]);
        var viaHelpFlag = await InvokeAsync([.. segments.Concat(["--help"])]);
        var viaShortFlag = await InvokeAsync([.. segments.Concat(["-h"])]);

        Assert.Equal(0, viaHelpCommand.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(viaHelpCommand.Stdout));
        Assert.Equal(string.Empty, viaHelpCommand.Stderr);

        Assert.Equal(viaHelpCommand.Stdout, viaHelpFlag.Stdout);
        Assert.Equal(viaHelpCommand.Stdout, viaShortFlag.Stdout);

        // Whether a bare invocation prints help is a property of the command's structure, not of
        // its name: a group has no action of its own, so it must render its own help rather than a
        // parse error. Resolving the command answers that directly instead of guessing from the
        // last segment.
        Assert.True(CommandPath.TryResolve(
            Program.BuildRootCommand(), segments, out var target, out _, out _));

        if (target!.Subcommands.Count > 0)
        {
            var viaBareInvocation = await InvokeAsync(segments);
            Assert.Equal(0, viaBareInvocation.ExitCode);
            Assert.Equal(viaHelpCommand.Stdout, viaBareInvocation.Stdout);
        }
    }

    /// <summary>
    /// Every command path in the tree, at every depth. Enumerating the real tree rather than
    /// hand-picking a handful of paths turns "help is equivalent at any depth" from an anecdote
    /// into a proof, and means a newly registered command is covered the moment it is added.
    /// </summary>
    public static TheoryData<string> EveryCommandPath()
    {
        var data = new TheoryData<string>();
        foreach (var path in EnumeratePaths(Program.BuildRootCommand(), []))
        {
            data.Add(path);
        }

        return data;
    }

    private static IEnumerable<string> EnumeratePaths(Command command, string[] prefix)
    {
        foreach (var subcommand in command.Subcommands)
        {
            // 'help' takes a command path of its own; invoking 'help help' is not a depth test.
            if (subcommand.Hidden || subcommand.Name == "help")
            {
                continue;
            }

            string[] path = [.. prefix, subcommand.Name];
            yield return string.Join(' ', path);

            foreach (var nested in EnumeratePaths(subcommand, path))
            {
                yield return nested;
            }
        }
    }

    [Theory]
    [InlineData("playfab", "xbox playfab <command> [flags]")]
    [InlineData("playfab file", "xbox playfab file <command> [flags]")]
    [InlineData("playfab economy inventory collection", "xbox playfab economy inventory collection <command> [flags]")]
    public async Task GroupHelp_GeneratesUsageFromTheRealCommandDepth(string commandPath, string expectedUsage)
    {
        var result = await InvokeAsync([.. commandPath.Split(' '), "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            $"USAGE{Environment.NewLine}  {expectedUsage}",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeafHelp_UsageIncludesArgumentsAndRequiredOptions()
    {
        var result = await InvokeAsync(["playfab", "api", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            $"USAGE{Environment.NewLine}  {CliIdentity.CommandName} playfab api <api-path>",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("REQUIRED INPUT", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("<api-path>", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeafHelp_DocumentsOptionalPositionalInput()
    {
        var result = await InvokeAsync(["playfab", "title-data", "get", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            $"USAGE{Environment.NewLine}  {CliIdentity.CommandName} playfab title-data get [key] [flags]",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("OPTIONAL INPUT", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("[key]", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeafHelp_DocumentsOptionalVariadicInput()
    {
        var result = await InvokeAsync(["help", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            $"USAGE{Environment.NewLine}  {CliIdentity.CommandName} help [command...] [flags]",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("[command...]", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeafHelp_DocumentsRequiredVariadicInput()
    {
        var result = await InvokeAsync(["playfab", "file", "delete", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            $"USAGE{Environment.NewLine}  {CliIdentity.CommandName} playfab file delete <names...>",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("<names...>", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void LeafHelp_WrapsLongDescriptionToConfiguredWidth()
    {
        const int width = 40;
        var rootCommand = Program.BuildRootCommand();
        var target = ResolveCommand(rootCommand, "playfab", "player", "account", "get");
        using var writer = new StringWriter();

        CommandHelpWriter.Write(
            writer,
            rootCommand,
            target,
            CliIdentity.CommandName,
            width);

        var lines = writer.ToString().Split(Environment.NewLine);
        var descriptionStart = Array.IndexOf(lines, "DESCRIPTION");
        var descriptionEnd = Array.IndexOf(lines, string.Empty, descriptionStart + 1);
        var descriptionLines = lines[(descriptionStart + 1)..descriptionEnd];

        Assert.NotEmpty(descriptionLines);
        Assert.All(descriptionLines, line => Assert.True(
            line.Length <= width,
            $"Description line exceeded {width} columns: '{line}'"));
        Assert.Contains("linked Steam", string.Join(' ', descriptionLines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeafHelp_DescribesSafetyFromTheApiPathNotTheCommandName()
    {
        var destructive = await InvokeAsync(["playfab", "mps", "build", "delete", "--help"]);
        var readOnly = await InvokeAsync(["playfab", "mps", "title", "status", "--help"]);
        var mutating = await InvokeAsync(["playfab", "mps", "title", "enable", "--help"]);

        Assert.Contains("SAFETY", destructive.Stdout, StringComparison.Ordinal);
        Assert.Contains("Destructive and irreversible", destructive.Stdout, StringComparison.Ordinal);
        Assert.Contains("--confirm", destructive.Stdout, StringComparison.Ordinal);

        // "status" reads like a mutation-free verb and "enable" like a mutation, but the
        // classification must come from GetTitleEnabledFor.../EnableMultiplayerServers..., not
        // from the friendly name.
        Assert.Contains("Read-only.", readOnly.Stdout, StringComparison.Ordinal);
        Assert.Contains("Mutates production title state", mutating.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task XboxPartnerLeafHelp_UsesExplicitNonPlayFabSafety()
    {
        var gameList = await InvokeAsync(["game", "list", "--help"]);
        var gameGet = await InvokeAsync(["game", "get", "--help"]);
        var sellerList = await InvokeAsync(["seller", "list", "--help"]);

        Assert.Contains("Read-only. Does not modify remote state.", sellerList.Stdout, StringComparison.Ordinal);
        Assert.Contains("Read-only. Does not modify remote state.", gameList.Stdout, StringComparison.Ordinal);
        Assert.Contains("Read-only. Does not modify remote state.", gameGet.Stdout, StringComparison.Ordinal);
        Assert.Contains("All games belonging to the seller", gameList.Stdout, StringComparison.Ordinal);
        Assert.Contains("At least one of title-id, --store-id, or --keyword", gameGet.Stdout, StringComparison.Ordinal);
        Assert.Contains("--keyword cannot be combined", gameGet.Stdout, StringComparison.Ordinal);
        Assert.Contains("[title-id]", gameGet.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--title-id", gameGet.Stdout, StringComparison.Ordinal);
        Assert.Contains("--details", gameList.Stdout, StringComparison.Ordinal);
        Assert.Contains("-d", gameGet.Stdout, StringComparison.Ordinal);
        Assert.Contains("sellers associated with the signed-in", sellerList.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Calls the PlayFab API", sellerList.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextLeafHelp_UsesExplicitLocalSafety()
    {
        var get = await InvokeAsync(["context", "get", "--help"]);
        var set = await InvokeAsync(["context", "set", "--help"]);
        var unset = await InvokeAsync(["context", "unset", "--help"]);
        var normalizedGet = NormalizeWhitespace(get.Stdout);
        var normalizedSet = NormalizeWhitespace(set.Stdout);

        // Context is local state: no command reaches PlayFab, the gateway, or anything else.
        Assert.Contains("Read-only. Does not modify remote state.", get.Stdout, StringComparison.Ordinal);
        Assert.Contains("Makes no network request.", normalizedGet, StringComparison.Ordinal);
        Assert.Contains("Changes local CLI state only.", set.Stdout, StringComparison.Ordinal);
        Assert.Contains("Makes no network request", normalizedSet, StringComparison.Ordinal);
        Assert.Contains("Changes local CLI state only.", unset.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Calls the PlayFab API", set.Stdout, StringComparison.Ordinal);

        // Like 'git config set', the key and value are positional, so 'set' takes no --seller-id,
        // and its Help says where to find the keys it can save.
        Assert.Contains("xbox context set <key> <value> [flags]", set.Stdout, StringComparison.Ordinal);
        Assert.Contains("$ xbox context set seller-id 1234567", set.Stdout, StringComparison.Ordinal);
        Assert.Contains("$ xbox context set playfab-title-id ABC12", set.Stdout, StringComparison.Ordinal);
        Assert.Contains("'xbox context key list' lists them", normalizedSet, StringComparison.Ordinal);
        Assert.DoesNotContain("--seller-id", set.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("$ xbox context set", get.Stdout, StringComparison.Ordinal);
        Assert.Contains("--all", unset.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("schema", "get")]
    [InlineData("document", "download")]
    [InlineData("document", "commit")]
    public async Task XboxLiveSingularResourceHelp_UsesCanonicalPaths(string resource, string action)
    {
        var helpCommand = await InvokeAsync(["help", "xbl", resource, action]);
        var helpOption = await InvokeAsync(["xbl", resource, action, "--help"]);

        Assert.Equal(0, helpCommand.ExitCode);
        Assert.Equal(0, helpOption.ExitCode);
        Assert.Empty(helpCommand.Stderr);
        Assert.Empty(helpOption.Stderr);
        Assert.Equal(helpCommand.Stdout, helpOption.Stdout);
        Assert.Contains(
            $"{CliIdentity.CommandName} xbl {resource} {action}",
            NormalizeWhitespace(helpCommand.Stdout),
            StringComparison.Ordinal);
        Assert.DoesNotContain("xbl schemas", helpCommand.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("xbl documents", helpCommand.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task XboxLiveGroupHelp_ListsSingularResources()
    {
        var result = await InvokeAsync(["help", "xbl"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.Contains("schema:", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("document:", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("schemas:", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("documents:", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task XboxLivePhaseTwoHelp_StatesDefaultsAndTableSupport()
    {
        var schemas = await InvokeAsync(["xbl", "schema", "get", "--help"]);
        var documents = await InvokeAsync(["xbl", "document", "download", "--help"]);
        string normalizedSchemas = NormalizeWhitespace(schemas.Stdout);
        string normalizedDocuments = NormalizeWhitespace(documents.Stdout);

        Assert.Equal(0, schemas.ExitCode);
        Assert.Contains(
            "Default: omitted, which lists all types.",
            normalizedSchemas,
            StringComparison.Ordinal);
        Assert.Contains(
            "Default: omitted, which lists versions, or with --dest saves every version.",
            normalizedSchemas,
            StringComparison.Ordinal);
        Assert.Contains("table", normalizedSchemas, StringComparison.Ordinal);

        Assert.Equal(0, documents.ExitCode);
        Assert.Contains("Default: sandbox.", normalizedDocuments, StringComparison.Ordinal);
        Assert.Contains("Default: working.", normalizedDocuments, StringComparison.Ordinal);
        Assert.Contains("Default: latest version.", normalizedDocuments, StringComparison.Ordinal);
        Assert.Contains("Default: all documents.", normalizedDocuments, StringComparison.Ordinal);
        Assert.Contains("table", normalizedDocuments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task XboxLivePhaseThreeHelp_StatesDefaultsAndTableSupport()
    {
        var commit = await InvokeAsync(["xbl", "document", "commit", "--help"]);
        var configurations =
            await InvokeAsync(["xbl", "configuration", "list", "--help"]);
        var configuration =
            await InvokeAsync(["xbl", "configuration", "get", "--help"]);
        var publish = await InvokeAsync(["xbl", "publish", "start", "--help"]);
        var status = await InvokeAsync(["xbl", "publish", "status", "--help"]);
        string normalizedCommit = NormalizeWhitespace(commit.Stdout);
        string normalizedConfigurations = NormalizeWhitespace(configurations.Stdout);
        string normalizedConfiguration = NormalizeWhitespace(configuration.Stdout);
        string normalizedPublish = NormalizeWhitespace(publish.Stdout);
        string normalizedStatus = NormalizeWhitespace(status.Stdout);

        Assert.Equal(0, commit.ExitCode);
        Assert.Contains("Default: sandbox.", normalizedCommit, StringComparison.Ordinal);
        Assert.Contains(
            "Default: false, which commits documents.",
            normalizedCommit,
            StringComparison.Ordinal);
        Assert.Contains("Default: the saved ETag.", normalizedCommit, StringComparison.Ordinal);
        Assert.Contains("TABLE OUTPUT", normalizedCommit, StringComparison.Ordinal);

        Assert.Equal(0, configurations.ExitCode);
        Assert.Contains("No default.", normalizedConfigurations, StringComparison.Ordinal);
        Assert.Contains("working and published", normalizedConfigurations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TABLE OUTPUT", normalizedConfigurations, StringComparison.Ordinal);
        Assert.Equal(0, configuration.ExitCode);
        Assert.Contains("--sandbox", normalizedConfiguration, StringComparison.Ordinal);
        Assert.Contains("No default.", normalizedConfiguration, StringComparison.Ordinal);
        Assert.Contains("TABLE OUTPUT", normalizedConfiguration, StringComparison.Ordinal);

        Assert.Equal(0, publish.ExitCode);
        Assert.Contains(
            "Default: false, which starts publishing.",
            normalizedPublish,
            StringComparison.Ordinal);
        Assert.Contains(
            "Default: its latest working version.",
            normalizedPublish,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--source-view", normalizedPublish, StringComparison.Ordinal);
        Assert.Contains("--sandbox", normalizedPublish, StringComparison.Ordinal);
        Assert.DoesNotContain("--from", normalizedPublish, StringComparison.Ordinal);
        Assert.DoesNotContain("--to", normalizedPublish, StringComparison.Ordinal);
        Assert.Contains("TABLE OUTPUT", normalizedPublish, StringComparison.Ordinal);

        Assert.Equal(0, status.ExitCode);
        Assert.Contains("No default.", normalizedStatus, StringComparison.Ordinal);
        Assert.Contains("TABLE OUTPUT", normalizedStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("--user-id", normalizedStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("--user-email", normalizedStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeafHelp_ExplainsDeleteConfirmationForInventoryOperationBatches()
    {
        var result = await InvokeAsync(
            ["playfab", "economy", "inventory", "operation", "execute", "--help"]);
        var normalizedOutput = string.Join(
            ' ',
            result.Stdout.Split(
                [' ', '\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains("SAFETY", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            "Batches containing Delete operations require --confirm.",
            normalizedOutput,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Commands that once said nothing about their effect. Each mutates a production title and
    /// each said nothing about it, because no metadata had ever been attached.
    /// </summary>
    [Theory]
    [InlineData("playfab title-data set", "/Admin/SetTitleData")]
    [InlineData("playfab group member add", "/Group/AddMembers")]
    [InlineData("playfab api-policy set", "/Admin/UpdatePolicy")]
    [InlineData("playfab player-data delete", "/Admin/UpdateUserData")]
    [InlineData("playfab cloudscript function unregister", "/CloudScript/UnregisterFunction")]
    [InlineData("playfab file upload", "/File/InitiateFileUploads")]
    [InlineData("playfab mps build create", "/MultiplayerServer/CreateBuildWithManagedContainer")]
    public async Task MutatingLeafHelp_NamesItsEffectAndItsApiPath(
        string commandPath,
        string expectedApiPath)
    {
        var result = await InvokeAsync([.. commandPath.Split(' '), "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SAFETY", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            "Mutates production title state",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("LEARN MORE", result.Stdout, StringComparison.Ordinal);

        // Accepts either LEARN MORE shape: one path shares the lead-in's line, several are listed
        // under it.
        Assert.Contains(
            result.Stdout.Split(Environment.NewLine),
            line => line.EndsWith(expectedApiPath, StringComparison.Ordinal));
    }

    /// <summary>
    /// A command reaching several endpoints lists them one per line. Comma-joining the six
    /// <c>player-data</c> paths produced a 200-column line in a section the renderer copies
    /// verbatim, so it overflowed every terminal it was shown in.
    /// </summary>
    [Fact]
    public async Task LeafHelp_ListsSeveralApiPathsOnePerLine()
    {
        var single = await InvokeAsync(["playfab", "file", "list", "--help"]);
        var several = await InvokeAsync(["playfab", "player-data", "get", "--help"]);

        Assert.Contains(
            "Calls the PlayFab API: /File/GetFiles",
            single.Stdout,
            StringComparison.Ordinal);

        var lines = several.Stdout.Split(Environment.NewLine);
        Assert.Contains("  Calls one or more of these PlayFab APIs:", lines);
        Assert.Contains("    /Admin/GetUserData", lines);
        Assert.Contains("    /Admin/GetUserPublisherInternalData", lines);

        // Scoped to the lines naming a path: the rest of the page has its own width rules, and a
        // later change to an unrelated section should not land on this test.
        var apiPathLines = lines.Where(line => line.Contains("/Admin/GetUser", StringComparison.Ordinal));
        Assert.All(apiPathLines, line => Assert.True(
            line.Length <= 80,
            $"API path line exceeded 80 columns: '{line}'"));
    }

    /// <summary>
    /// A command that curates no scope or output prose renders no SCOPE or OUTPUT section, but
    /// still renders SAFETY, which is derived from its effect. Asserted against a synthetic
    /// command so that adding prose to a real command can't break it.
    /// </summary>
    [Fact]
    public void LeafHelp_OmitsSectionsThatHaveNoContent()
    {
        var rootCommand = new RootCommand("Test root");
        var command = new Command("bare", "A command with only a safety classification");
        command.WithMetadata(new CommandMetadata(Safety: CommandSafety.ReadOnly));
        rootCommand.Subcommands.Add(command);
        using var writer = new StringWriter();

        CommandHelpWriter.Write(writer, rootCommand, command, CliIdentity.CommandName);

        var help = writer.ToString();
        Assert.DoesNotContain("SCOPE", help, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTPUT", help, StringComparison.Ordinal);
        Assert.Contains("SAFETY", help, StringComparison.Ordinal);
    }

    /// <summary>
    /// The renderer must still omit SAFETY for a command carrying no classification — it is the
    /// behaviour that keeps Help from inventing a claim. Every shipped command now records one,
    /// so this is asserted against a synthetic command rather than a real gap in the surface.
    /// </summary>
    [Fact]
    public void LeafHelp_OmitsSafetyWhenTheCommandRecordsNoClassification()
    {
        var rootCommand = new RootCommand("Test root");
        var command = new Command("unclassified", "A command with no metadata at all");
        rootCommand.Subcommands.Add(command);
        using var writer = new StringWriter();

        CommandHelpWriter.Write(writer, rootCommand, command, CliIdentity.CommandName);

        Assert.DoesNotContain("SAFETY", writer.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every example in <c>playfab api</c> Help calls a real, read-only PlayFab operation, so an
    /// agent copying one can't change a title. Any string parses as a path, so parsing alone
    /// can't show this.
    /// </summary>
    [Fact]
    public async Task ApiHelp_ExamplesCallRealReadOnlyOperations()
    {
        // The path is supplied by the caller, so SAFETY states that the effect depends on it
        // rather than omitting the section.
        var help = await InvokeAsync(["playfab", "api", "--help"]);
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("Depends on the API path supplied.", help.Stdout, StringComparison.Ordinal);
        Assert.Contains("OUTPUT", help.Stdout, StringComparison.Ordinal);

        var api = Program.BuildRootCommand().Subcommands
            .Single(command => command.Name == "playfab").Subcommands
            .Single(command => command.Name == "api");
        var examples = CommandMetadataRegistry.GetMetadata(api).Examples;
        Assert.NotNull(examples);
        Assert.NotEmpty(examples);

        foreach (var example in examples)
        {
            Assert.Contains($"$ {example}", help.Stdout, StringComparison.Ordinal);
            var path = example.Split(' ')[3];
            Assert.Contains(PlayFabApiMetadata.AllEndpoints, endpoint => endpoint.Path == path);
            Assert.True(DestructiveOperations.IsReadOnly(path), $"{path} isn't read-only");
        }
    }

    /// <summary>
    /// The CLI bundles no API descriptions, so Help is how an agent learns where to look: the path
    /// format, PlayFab's REST reference and Swagger specs, and which token the call is made with.
    /// The links are in a section the renderer doesn't wrap, so they must fit 80 columns as written.
    /// </summary>
    [Fact]
    public async Task ApiHelp_PointsToPlayFabsReferenceAndSpecs()
    {
        var help = await InvokeAsync(["playfab", "api", "--help"]);

        Assert.Equal(0, help.ExitCode);
        var text = NormalizeWhitespace(help.Stdout);
        Assert.Contains("/<Prefix>/<Operation>", text, StringComparison.Ordinal);
        Assert.Contains("JSON request object", text, StringComparison.Ordinal);
        Assert.Contains("https://learn.microsoft.com/rest/api/playfab/", text, StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/PlayFab/API_Specs/tree/master/Swagger/PlayFab",
            text,
            StringComparison.Ordinal);
        // Spec files are named for API groups, which a path's first segment doesn't always match.
        Assert.Contains("/Catalog is in Economy.swagger.json", text, StringComparison.Ordinal);
        Assert.Contains("security field", text, StringComparison.Ordinal);
        Assert.Contains(
            "in place of a title secret key or title entity token",
            text,
            StringComparison.Ordinal);

        var root = Program.BuildRootCommand();
        Assert.True(CommandPath.TryResolve(root, ["playfab", "api"], out var api, out _, out _));
        using var narrow = new StringWriter();
        CommandHelpWriter.Write(narrow, root, api, CliIdentity.CommandName, width: 80);
        var learnMore = narrow.ToString().Split(Environment.NewLine)
            .SkipWhile(line => line != "LEARN MORE")
            .TakeWhile(line => line.Length > 0)
            .Where(line => !line.Contains(ProjectLinks.Documentation, StringComparison.Ordinal));
        Assert.All(learnMore, line => Assert.True(
            line.Length <= 80,
            $"LEARN MORE line exceeded 80 columns: '{line}'"));
    }

    [Fact]
    public async Task LeafHelp_RendersCuratedScopeExamplesAndOutput()
    {
        var result = await InvokeAsync(
            ["playfab", "economy", "inventory", "collection", "list", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SCOPE", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("EXAMPLES", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("OUTPUT", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            "Calls the PlayFab API: /Inventory/GetInventoryCollectionIds",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SandboxListHelp_RendersMetadata()
    {
        var result = await InvokeAsync(["sandbox", "list", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SCOPE", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("SAFETY", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("EXAMPLES", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("OUTPUT", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("TABLE OUTPUT", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("GLOBAL", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            "xbox sandbox list --output table",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.DoesNotContain("/xboxpartner/", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list", "xbox test-account batch list --mine --limit 5", "BATCH ID")]
    [InlineData("get", "xbox test-account batch get 123456 --output table", "GAMERTAG")]
    public async Task TestAccountBatchHelp_RendersMetadata(
        string verb,
        string example,
        string column)
    {
        var result = await InvokeAsync(["test-account", "batch", verb, "--help"]);

        Assert.Equal(0, result.ExitCode);
        foreach (string section in new[] { "SCOPE", "SAFETY", "EXAMPLES", "OUTPUT", "TABLE OUTPUT" })
        {
            Assert.Contains(section, result.Stdout, StringComparison.Ordinal);
        }

        Assert.Contains(example, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("XboxPartner", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(column, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestAccountBatchListHelp_ExplainsIncludeSandboxes()
    {
        var result = await InvokeAsync(["test-account", "batch", "list", "--help"]);
        string help = NormalizeWhitespace(result.Stdout);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--include-sandboxes", help, StringComparison.Ordinal);
        Assert.Contains("SANDBOXES", help, StringComparison.Ordinal);
        Assert.Contains(
            "without --limit it lists at most the newest 10 matching batches.",
            help,
            StringComparison.Ordinal);
        Assert.Contains("Reads the list a page at a time", help, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("batch")]
    [InlineData("batch list")]
    [InlineData("batch get")]
    [InlineData("batch create")]
    [InlineData("batch update")]
    [InlineData("batch delete")]
    [InlineData("get")]
    [InlineData("job")]
    [InlineData("job get")]
    [InlineData("job wait")]
    public async Task TestAccountHelp_DescribesBehaviorWithoutInternalServices(string command)
    {
        string[] path = ["test-account", .. command.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
        var optionHelp = await InvokeAsync([.. path, "--help"]);
        var helpCommand = await InvokeAsync(["help", .. path]);

        Assert.Equal(0, optionHelp.ExitCode);
        Assert.Equal(0, helpCommand.ExitCode);
        Assert.Equal(optionHelp.Stdout, helpCommand.Stdout);
        Assert.DoesNotContain("XboxPartner", optionHelp.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("XTAS", optionHelp.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deployment", optionHelp.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/xboxpartner/", optionHelp.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeafHelp_SectionsAppearInTheDocumentedOrder()
    {
        var result = await InvokeAsync(
            ["playfab", "economy", "inventory", "operation", "execute", "--help"]);

        string[] expectedOrder =
        [
            "DESCRIPTION", "USAGE", "SCOPE", "OPTIONS", "SAFETY", "EXAMPLES", "OUTPUT", "LEARN MORE",
        ];

        var lines = result.Stdout.Split(Environment.NewLine);
        var positions = expectedOrder
            .Select(section => Array.FindIndex(
                lines,
                line => string.Equals(line, section, StringComparison.Ordinal)))
            .ToArray();

        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.OrderBy(position => position), positions);
    }

    [Fact]
    public void ConfigureHelp_DoesNotTreatHiddenOnlySubcommandsAsAVisibleGroup()
    {
        var rootCommand = new RootCommand("Test root");
        var command = new Command("container", "A command whose only child is hidden");
        command.Subcommands.Add(new Command("internal") { Hidden = true });
        rootCommand.Subcommands.Add(command);

        CommandLineExperience.ConfigureHelp(rootCommand, CliIdentity.CommandName);

        Assert.Null(command.Action);

        using var writer = new StringWriter();
        CommandHelpWriter.Write(
            writer,
            rootCommand,
            command,
            CliIdentity.CommandName);
        Assert.DoesNotContain("AVAILABLE COMMANDS", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ShowsPlayFabOptionsOnlyInsideThePlayFabSubtree()
    {
        var root = await InvokeAsync([]);
        var playFabGroup = await InvokeAsync(["help", "playfab"]);
        var playFabScoped = await InvokeAsync(["playfab", "title-data", "get", "--help"]);
        var outsidePlayFab = await InvokeAsync(["auth", "status", "--help"]);

        Assert.DoesNotContain("--data-only", root.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--confirm", root.Stdout, StringComparison.Ordinal);
        Assert.Contains("--data-only", playFabGroup.Stdout, StringComparison.Ordinal);
        Assert.Contains("--confirm", playFabGroup.Stdout, StringComparison.Ordinal);
        Assert.Contains("--data-only", playFabScoped.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--data-only", outsidePlayFab.Stdout, StringComparison.Ordinal);

        // --playfab-title-id is NOT PlayFab-scoped: like the other global overrides, it stays
        // visible everywhere.
        Assert.Contains("--playfab-title-id", root.Stdout, StringComparison.Ordinal);
        Assert.Contains("--playfab-title-id", playFabGroup.Stdout, StringComparison.Ordinal);
        Assert.Contains("--playfab-title-id", playFabScoped.Stdout, StringComparison.Ordinal);
        Assert.Contains("--playfab-title-id", outsidePlayFab.Stdout, StringComparison.Ordinal);

        // Scoping is a Help concern only: the option still parses anywhere, which is the form
        // the documentation's examples use.
        Assert.Contains("--config-env", outsidePlayFab.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayFabOptions_RemainParseableOutsideThePlayFabSubtree()
    {
        var rootCommand = Program.BuildRootCommand();

        var result = rootCommand.Parse(["--playfab-title-id", "ABC123", "auth", "status"]);

        Assert.Empty(result.Errors);
        Assert.Equal("ABC123", result.GetValue(Program.PlayFabTitleIdOption));
    }

    [Fact]
    public void EveryVisibleRootCommand_IsDeliberatelyCategorized()
    {
        var rootCommand = Program.BuildRootCommand();

        var uncategorized = rootCommand.Subcommands
            .Where(command => !command.Hidden)
            .Where(command =>
                CommandMetadataRegistry.GetMetadata(command).Category == HelpCategory.None)
            .Select(command => command.Name)
            .ToArray();

        Assert.Empty(uncategorized);
    }

    [Fact]
    public async Task RootHelp_ListsHelpAsASupportCommand()
    {
        var result = await InvokeAsync([]);

        var supportIndex = result.Stdout.IndexOf("SUPPORT COMMANDS", StringComparison.Ordinal);
        var helpIndex = result.Stdout.IndexOf("  help:", StringComparison.Ordinal);

        Assert.NotEqual(-1, supportIndex);
        Assert.NotEqual(-1, helpIndex);
        Assert.True(helpIndex > supportIndex);
        Assert.DoesNotContain("ADDITIONAL COMMANDS", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpCommand_WithAnUnknownPath_WritesContextualErrorToStderr()
    {
        var result = await InvokeAsync(["help", "playfab", "dsadsa"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.StartsWith(
            $"unknown command \"dsadsa\" for \"{CliIdentity.CommandName} playfab\"",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Usage:  {CliIdentity.CommandName} playfab <command> [flags]",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DeepHelp_NarrowWidthUsesStackedRows()
    {
        var rootCommand = Program.BuildRootCommand();
        var target = ResolveCommand(rootCommand, "playfab", "economy", "inventory", "list");
        using var writer = new StringWriter();

        CommandHelpWriter.Write(writer, rootCommand, target, CliIdentity.CommandName, width: 40);

        var output = writer.ToString();
        Assert.Contains($"  --entity-id <entity-id>{Environment.NewLine}    ", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepHelp_EmitsNoAnsiWhenColorIsDisabled()
    {
        var rootCommand = Program.BuildRootCommand();
        var target = ResolveCommand(rootCommand, "playfab", "file", "list");
        using var writer = new StringWriter();

        CommandHelpWriter.Write(writer, rootCommand, target, CliIdentity.CommandName, useAnsi: false);

        Assert.DoesNotContain("\u001b[", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DeepHelp_BoldsSectionHeadingsWhenAnsiIsEnabled()
    {
        var rootCommand = Program.BuildRootCommand();
        var target = ResolveCommand(rootCommand, "playfab", "file", "list");
        using var writer = new StringWriter();

        CommandHelpWriter.Write(writer, rootCommand, target, CliIdentity.CommandName, useAnsi: true);

        Assert.Contains("\u001b[1mUSAGE\u001b[0m", writer.ToString(), StringComparison.Ordinal);
    }

    private static Command ResolveCommand(RootCommand rootCommand, params string[] segments)
    {
        Assert.True(
            CommandPath.TryResolve(rootCommand, segments, out var command, out _, out _),
            $"Could not resolve '{string.Join(' ', segments)}'.");
        return command;
    }

    private static async Task<CapturedInvocation> InvokeAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            var exitCode = await Program.Main(args);
            return new CapturedInvocation(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static string NormalizeNewLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string NormalizeWhitespace(string value) =>
        string.Join(
            ' ',
            value.Split(
                [' ', '\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries));

    private sealed record CapturedInvocation(int ExitCode, string Stdout, string Stderr);
}
