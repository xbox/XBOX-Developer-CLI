// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Presentation;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class InfoCommandTests
{
    [Fact]
    public async Task InfoCommand_PrintsBannerAndGettingStartedDetails()
    {
        var originalOut = Console.Out;
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "XboxDevCLI-tests",
            Guid.NewGuid().ToString("N"));
        using var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            var root = new RootCommand();
            root.Subcommands.Add(InfoCommandFactory.Create(
                useColor: () => false,
                isInteractive: () => true,
                stateDirectory: () => stateDirectory));

            var exitCode = await root.Parse("info").InvokeAsync();
            var output = writer.ToString();

            Assert.Equal(0, exitCode);
            Assert.Contains(InfoCommandFactory.Banner, output, StringComparison.Ordinal);
            var expectedDetails =
                $"""
                XBOX CLI version {CliIdentity.Version}
                {ProjectLinks.Home}

                Run this command to get started:
                  $ {CliIdentity.CommandName} auth login
                """;
            Assert.Contains(expectedDetails, output, StringComparison.Ordinal);
            Assert.True(File.Exists(
                Path.Combine(stateDirectory, WelcomeExperience.SentinelFileName)));
            Assert.Equal(
                CliIdentity.Version,
                File.ReadAllText(
                    Path.Combine(stateDirectory, WelcomeExperience.SentinelFileName)));
        }
        finally
        {
            Console.SetOut(originalOut);
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildRootCommand_RegistersInfoCommand()
    {
        var root = Program.BuildRootCommand();

        Assert.Contains(root.Subcommands, command => command.Name == "info");
    }

    [Fact]
    public void CommandName_MatchesConfiguredAssemblyName()
    {
        Assert.Equal(typeof(Program).Assembly.GetName().Name, CliIdentity.CommandName);
    }
}
