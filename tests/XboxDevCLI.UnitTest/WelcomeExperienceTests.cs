// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using XboxDevCLI.Presentation;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class WelcomeExperienceTests
{
    [Fact]
    public void RenderLogo_WithoutColor_ReturnsEmbeddedLogo()
    {
        var rendered = WelcomeExperience.RenderLogo(useColor: false);

        Assert.Equal(WelcomeExperience.PlainLogo, rendered);
        Assert.Contains("*++++*#%%%%#*++++*", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderLogo_WithColor_PreservesArtAndUsesTrueColorPalette()
    {
        var rendered = WelcomeExperience.RenderLogo(useColor: true);
        var withoutAnsi = Regex.Replace(rendered, "\u001b\\[[0-9;]*m", string.Empty);
        var expectedColors = new[]
        {
            "\u001b[1;38;2;239;255;197m",
            "\u001b[1;38;2;199;255;0m",
            "\u001b[1;38;2;139;240;0m",
            "\u001b[22;38;2;82;176;0m",
            "\u001b[22;38;2;16;124;16m",
            "\u001b[22;38;2;11;90;11m",
            "\u001b[22;38;2;6;58;6m",
        };

        Assert.Equal(WelcomeExperience.PlainLogo, withoutAnsi);
        Assert.All(
            expectedColors,
            color => Assert.Contains(color, rendered, StringComparison.Ordinal));
        Assert.EndsWith("\u001b[0m", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, null, "xterm-256color", null, true)]
    [InlineData(true, null, "xterm-256color", null, false)]
    [InlineData(false, "", "xterm-256color", null, false)]
    [InlineData(false, null, "dumb", null, false)]
    [InlineData(false, null, "DUMB", null, false)]
    [InlineData(false, null, "xterm-256color", "0", false)]
    public void SupportsColor_RespectsTerminalConventions(
        bool isRedirected,
        string? noColor,
        string? term,
        string? cliColor,
        bool expected)
    {
        Assert.Equal(
            expected,
            WelcomeExperience.SupportsColor(isRedirected, noColor, term, cliColor));
    }

    [Fact]
    public void SupportsColor_RedirectedRuntimeOutputIsFalse()
    {
        Assert.False(WelcomeExperience.SupportsColor(isRedirected: true));
    }

    [Fact]
    public void WriteIfFirstRun_WritesAndCreatesSentinelOnlyOnce()
    {
        var stateDirectory = CreateTempStateDirectory();

        try
        {
            using var first = new StringWriter();
            WelcomeExperience.WriteIfFirstRun(
                stateDirectory,
                "1.0.0",
                first,
                useColor: false);

            Assert.NotEmpty(first.ToString());
            var sentinelPath = Path.Combine(
                stateDirectory,
                WelcomeExperience.SentinelFileName);
            Assert.Equal("1.0.0", File.ReadAllText(sentinelPath));

            using var second = new StringWriter();
            WelcomeExperience.WriteIfFirstRun(
                stateDirectory,
                "1.0.0",
                second,
                useColor: false);

            Assert.Empty(second.ToString());
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Fact]
    public void WriteIfFirstRun_NewVersionWritesAgainAndUpdatesSentinel()
    {
        var stateDirectory = CreateTempStateDirectory();

        try
        {
            using var previousVersion = new StringWriter();
            WelcomeExperience.WriteIfFirstRun(
                stateDirectory,
                "1.0.0",
                previousVersion,
                useColor: false);
            using var updatedVersion = new StringWriter();

            WelcomeExperience.WriteIfFirstRun(
                stateDirectory,
                "1.1.0",
                updatedVersion,
                useColor: false);

            Assert.NotEmpty(updatedVersion.ToString());
            Assert.Equal(
                "1.1.0",
                File.ReadAllText(
                    Path.Combine(stateDirectory, WelcomeExperience.SentinelFileName)));
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Fact]
    public void WriteFirstRun_PrintsLogoAndXboxCommandsWithOneLeadingBlankLine()
    {
        using var writer = new StringWriter();

        WelcomeExperience.WriteFirstRun(writer, useColor: false);

        var output = writer.ToString();
        Assert.StartsWith(
            $"{Environment.NewLine}{WelcomeExperience.PlainLogo}",
            output,
            StringComparison.Ordinal);
        Assert.False(
            output.StartsWith(
                $"{Environment.NewLine}{Environment.NewLine}",
                StringComparison.Ordinal));
        Assert.Contains(
            $"XBOX CLI: {CliIdentity.Tagline}",
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PlayFab operations from your terminal.",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Run this command to get started:{Environment.NewLine}  $ xbox auth login",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Run this command to explore all commands:{Environment.NewLine}  $ xbox --help",
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("XboxDevCLI login", output, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteFirstRun_LeavesCommandExamplesUnstyledWhenColorIsEnabled()
    {
        using var writer = new StringWriter();

        WelcomeExperience.WriteFirstRun(writer, useColor: true);

        var output = writer.ToString();
        Assert.Contains("  $ xbox auth login", output, StringComparison.Ordinal);
        Assert.Contains("  $ xbox --help", output, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\u001b[1;38;2;139;240;0mxbox",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WriteFocusedFirstRun_StopsAfterBrandedGetStartedSection()
    {
        using var writer = new StringWriter();

        WelcomeExperience.WriteFocusedFirstRun(writer, useColor: true);

        var output = writer.ToString();
        Assert.StartsWith(
            $"{Environment.NewLine}{WelcomeExperience.RenderLogo(useColor: true)}",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\u001b[1;38;2;139;240;0mXBOX\u001b[0m CLI: {CliIdentity.Tagline}",
            output,
            StringComparison.Ordinal);
        Assert.Contains("\u001b[1mGET STARTED\u001b[0m", output, StringComparison.Ordinal);
        Assert.EndsWith(
            $"  $ {CliIdentity.CommandName} --help{Environment.NewLine}{Environment.NewLine}",
            output,
            StringComparison.Ordinal);
        Assert.Contains($"  $ {CliIdentity.CommandName} auth login", output, StringComparison.Ordinal);
        Assert.DoesNotContain("USAGE", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Run this command to explore", output, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteVersion_ColorsOnlyXboxWhenColorIsEnabled()
    {
        using var writer = new StringWriter();

        WelcomeExperience.WriteVersion(writer, "0.1.0", useColor: true);

        Assert.Equal(
            $"\u001b[1;38;2;139;240;0mXBOX\u001b[0m CLI version 0.1.0{Environment.NewLine}",
            writer.ToString());
    }

    [Fact]
    public void WriteInfo_PrintsVersionUrlAndPlainShellCommandAfterLogo()
    {
        using var writer = new StringWriter();

        WelcomeExperience.WriteInfo(writer, "0.1.0", useColor: true);

        var output = writer.ToString();
        var logoEnd = output.IndexOf(WelcomeExperience.PlainLogo, StringComparison.Ordinal)
            + WelcomeExperience.PlainLogo.Length;
        var versionStart = output.IndexOf("XBOX\u001b[0m CLI version 0.1.0", StringComparison.Ordinal);
        var urlStart = output.IndexOf(ProjectLinks.Home, StringComparison.Ordinal);
        var instructionStart = output.IndexOf("Run this command to get started:", StringComparison.Ordinal);

        Assert.True(logoEnd < versionStart);
        Assert.True(versionStart < urlStart);
        Assert.True(urlStart < instructionStart);
        Assert.Contains("  $ xbox auth login", output, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\u001b[1;38;2;139;240;0mxbox",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WriteIfFirstRun_UnavailableStateStorageStillWritesWelcome()
    {
        var stateFile = Path.GetTempFileName();

        try
        {
            using var writer = new StringWriter();

            WelcomeExperience.WriteIfFirstRun(
                stateFile,
                "1.0.0",
                writer,
                useColor: false);

            Assert.NotEmpty(writer.ToString());
        }
        finally
        {
            File.Delete(stateFile);
        }
    }

    [Fact]
    public void WriteIfFirstRun_InvalidStatePathStillWritesWelcome()
    {
        using var writer = new StringWriter();

        WelcomeExperience.WriteIfFirstRun(
            "\0",
            "1.0.0",
            writer,
            useColor: false);

        Assert.NotEmpty(writer.ToString());
    }

    [Fact]
    public void ProgramWelcome_FirstBareInvocationWritesFocusedLaunchAndSuppressesRootHelp()
    {
        var stateDirectory = CreateTempStateDirectory();

        try
        {
            var root = Program.BuildRootCommand();
            var parseResult = root.Parse([]);
            using var output = new StringWriter();

            var suppressRootHelp = Program.WriteWelcomeIfFirstRun(
                parseResult,
                [],
                stateDirectory,
                output,
                isInteractive: true,
                isErrorRedirected: false,
                useColor: false);

            var rendered = output.ToString();
            var tagline = $"{CliIdentity.DisplayName}: {CliIdentity.Tagline}";
            Assert.True(suppressRootHelp);
            Assert.StartsWith(
                $"{Environment.NewLine}{WelcomeExperience.PlainLogo}",
                rendered,
                StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(rendered, tagline));
            Assert.Contains("GET STARTED", rendered, StringComparison.Ordinal);
            Assert.EndsWith(
                $"  $ {CliIdentity.CommandName} --help{Environment.NewLine}{Environment.NewLine}",
                rendered,
                StringComparison.Ordinal);
            Assert.Contains(
                $"  $ {CliIdentity.CommandName} auth login",
                rendered,
                StringComparison.Ordinal);
            Assert.DoesNotContain("USAGE", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("CORE COMMANDS", rendered, StringComparison.Ordinal);
            Assert.Equal(
                CliIdentity.Version,
                File.ReadAllText(
                    Path.Combine(stateDirectory, WelcomeExperience.SentinelFileName)));
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void ProgramWelcome_ExplicitRootHelpWritesOnlyLogoBeforeCompleteHelp(
        string rootHelpArgument)
    {
        var stateDirectory = CreateTempStateDirectory();

        try
        {
            var root = Program.BuildRootCommand();
            var arguments = new[] { rootHelpArgument };
            var parseResult = root.Parse(arguments);
            using var output = new StringWriter();

            var suppressRootHelp = Program.WriteWelcomeIfFirstRun(
                parseResult,
                arguments,
                stateDirectory,
                output,
                isInteractive: true,
                isErrorRedirected: false,
                useColor: false);
            CommandLine.CommandHelpWriter.WriteRoot(
                output,
                root,
                CliIdentity.CommandName,
                width: 100);

            var rendered = output.ToString();
            var tagline = $"{CliIdentity.DisplayName}: {CliIdentity.Tagline}";
            Assert.False(suppressRootHelp);
            Assert.StartsWith(
                $"{Environment.NewLine}{WelcomeExperience.PlainLogo}",
                rendered,
                StringComparison.Ordinal);
            Assert.True(
                rendered.IndexOf(WelcomeExperience.PlainLogo, StringComparison.Ordinal)
                < rendered.IndexOf(tagline, StringComparison.Ordinal));
            Assert.Equal(1, CountOccurrences(rendered, tagline));
            Assert.Contains("GET STARTED", rendered, StringComparison.Ordinal);
            Assert.Contains("USAGE", rendered, StringComparison.Ordinal);
            Assert.Contains("CORE COMMANDS", rendered, StringComparison.Ordinal);
            Assert.Equal(
                CliIdentity.Version,
                File.ReadAllText(
                    Path.Combine(stateDirectory, WelcomeExperience.SentinelFileName)));
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Theory]
    [InlineData("--config-env", "Development", "--help")]
    [InlineData("--verbose", "-h")]
    public void ProgramWelcome_RootHelpWithGlobalOptionsWritesOnlyLogoBeforeCompleteHelp(
        params string[] arguments)
    {
        var stateDirectory = CreateTempStateDirectory();

        try
        {
            var root = Program.BuildRootCommand();
            var parseResult = root.Parse(arguments);
            using var output = new StringWriter();

            var suppressRootHelp = Program.WriteWelcomeIfFirstRun(
                parseResult,
                arguments,
                stateDirectory,
                output,
                isInteractive: true,
                isErrorRedirected: false,
                useColor: false);
            CommandLine.CommandHelpWriter.WriteRoot(
                output,
                root,
                CliIdentity.CommandName,
                width: 100);

            var rendered = output.ToString();
            var tagline = $"{CliIdentity.DisplayName}: {CliIdentity.Tagline}";
            Assert.False(suppressRootHelp);
            Assert.Empty(parseResult.Errors);
            Assert.StartsWith(
                $"{Environment.NewLine}{WelcomeExperience.PlainLogo}",
                rendered,
                StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(rendered, tagline));
            Assert.DoesNotContain(
                "Run this command to explore all commands:",
                rendered,
                StringComparison.Ordinal);
            Assert.Contains("USAGE", rendered, StringComparison.Ordinal);
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Fact]
    public void ProgramWelcome_SubsequentBareInvocationLetsCompleteRootHelpRender()
    {
        var stateDirectory = CreateTempStateDirectory();

        try
        {
            WelcomeExperience.MarkShown(stateDirectory, CliIdentity.Version);
            var root = Program.BuildRootCommand();
            var parseResult = root.Parse([]);
            using var welcomeOutput = new StringWriter();

            var suppressRootHelp = Program.WriteWelcomeIfFirstRun(
                parseResult,
                [],
                stateDirectory,
                welcomeOutput,
                isInteractive: true,
                isErrorRedirected: false,
                useColor: false);

            Assert.False(suppressRootHelp);
            Assert.Empty(welcomeOutput.ToString());

            using var helpOutput = new StringWriter();
            CommandLine.CommandHelpWriter.WriteRoot(
                helpOutput,
                root,
                CliIdentity.CommandName,
                width: 100);
            Assert.Contains("USAGE", helpOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("CORE COMMANDS", helpOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Fact]
    public void ProgramWelcome_NonHelpCommandRetainsFullOnboarding()
    {
        var stateDirectory = CreateTempStateDirectory();

        try
        {
            string[] arguments = ["auth", "status"];
            var parseResult = Program.BuildRootCommand().Parse(arguments);
            using var writer = new StringWriter();

            Program.WriteWelcomeIfFirstRun(
                parseResult,
                arguments,
                stateDirectory,
                writer,
                isInteractive: true,
                isErrorRedirected: false,
                useColor: false);

            var output = writer.ToString();
            Assert.Contains(
                $"{CliIdentity.DisplayName}: {CliIdentity.Tagline}",
                output,
                StringComparison.Ordinal);
            Assert.Contains("Run this command to get started:", output, StringComparison.Ordinal);
            Assert.Contains("Run this command to explore all commands:", output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Fact]
    public void ProgramWelcome_ParseErrorDoesNotWriteOrCreateSentinel()
    {
        var stateDirectory = CreateTempStateDirectory();

        try
        {
            var parseResult = Program.BuildRootCommand().Parse(["not-a-command"]);
            using var writer = new StringWriter();

            var suppressRootHelp = Program.WriteWelcomeIfFirstRun(
                parseResult,
                ["not-a-command"],
                stateDirectory,
                writer,
                isInteractive: true,
                isErrorRedirected: false,
                useColor: false);

            Assert.False(suppressRootHelp);
            Assert.Empty(writer.ToString());
            Assert.False(File.Exists(
                Path.Combine(stateDirectory, WelcomeExperience.SentinelFileName)));
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ProgramWelcome_NonInteractiveOrRedirectedDoesNotWrite(
        bool isInteractive,
        bool isErrorRedirected)
    {
        var stateDirectory = CreateTempStateDirectory();

        try
        {
            var parseResult = Program.BuildRootCommand().Parse([]);
            using var writer = new StringWriter();

            var suppressRootHelp = Program.WriteWelcomeIfFirstRun(
                parseResult,
                [],
                stateDirectory,
                writer,
                isInteractive,
                isErrorRedirected,
                useColor: false);

            Assert.False(suppressRootHelp);
            Assert.Empty(writer.ToString());
            Assert.False(File.Exists(
                Path.Combine(stateDirectory, WelcomeExperience.SentinelFileName)));
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Fact]
    public void ProgramWelcome_InfoCommandLetsInfoOwnRendering()
    {
        var stateDirectory = CreateTempStateDirectory();

        try
        {
            var parseResult = Program.BuildRootCommand().Parse(["info"]);
            using var writer = new StringWriter();

            var suppressRootHelp = Program.WriteWelcomeIfFirstRun(
                parseResult,
                ["info"],
                stateDirectory,
                writer,
                isInteractive: true,
                isErrorRedirected: false,
                useColor: false);

            Assert.False(suppressRootHelp);
            Assert.Empty(writer.ToString());
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    private static string CreateTempStateDirectory() =>
        Path.Combine(
            Path.GetTempPath(),
            "XboxDevCLI-tests",
            Guid.NewGuid().ToString("N"));

    private static void DeleteStateDirectory(string stateDirectory)
    {
        if (Directory.Exists(stateDirectory))
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    private static int CountOccurrences(string value, string searchValue) =>
        value.Split(searchValue, StringSplitOptions.None).Length - 1;
}
