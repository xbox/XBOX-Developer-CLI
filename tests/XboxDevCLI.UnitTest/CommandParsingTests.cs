// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class CommandParsingTests
{
    [Fact]
    public void RootCommand_HasExpectedAuthenticationHierarchy()
    {
        var rootCommand = Program.BuildRootCommand();
        var authCommand = Assert.Single(
            rootCommand.Subcommands,
            command => command.Name == "auth");

        Assert.Contains(authCommand.Subcommands, command => command.Name == "login");
        Assert.Contains(authCommand.Subcommands, command => command.Name == "logout");
        Assert.Contains(authCommand.Subcommands, command => command.Name == "status");
        Assert.DoesNotContain(
            rootCommand.Subcommands,
            command => command.Name is "login" or "logout" or "status");
    }

    [Fact]
    public async Task AuthLogin_Command_ParsesSuccessfully()
    {
        var loginCommand = new Command("login", "Sign in");
        var wasInvoked = false;
        loginCommand.SetAction((ParseResult _) => { wasInvoked = true; return 0; });

        var rootCommand = new RootCommand();
        var authCommand = new Command("auth");
        authCommand.Subcommands.Add(loginCommand);
        rootCommand.Subcommands.Add(authCommand);

        var result = await rootCommand.Parse("auth login").InvokeAsync();

        Assert.True(wasInvoked);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task AuthStatus_Command_ParsesSuccessfully()
    {
        var statusCommand = new Command("status", "Show status");
        var wasInvoked = false;
        statusCommand.SetAction((ParseResult _) => { wasInvoked = true; return 0; });

        var rootCommand = new RootCommand();
        var authCommand = new Command("auth");
        authCommand.Subcommands.Add(statusCommand);
        rootCommand.Subcommands.Add(authCommand);

        var result = await rootCommand.Parse("auth status").InvokeAsync();

        Assert.True(wasInvoked);
        Assert.Equal(0, result);
    }

    [Theory]
    [InlineData("feedback")]
    [InlineData("report-issue")]
    public async Task Feedback_Command_PrintsIssueUrlWithoutOpeningBrowser(string command)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            var result = await Program.Main(["--disable-telemetry", command, "--no-browser"]);

            Assert.Equal(0, result);
            Assert.Contains(ProjectLinks.NewIssue, writer.ToString());
            Assert.Contains("environment=", writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task UnknownCommand_ReturnsNonZero()
    {
        var rootCommand = new RootCommand();
        var authCommand = new Command("auth");
        authCommand.Subcommands.Add(new Command("login"));
        rootCommand.Subcommands.Add(authCommand);

        var result = await rootCommand.Parse("nonexistent").InvokeAsync();

        Assert.NotEqual(0, result);
    }

    [Fact]
    public async Task GlobalOption_ConfigEnv_IsAccepted()
    {
        var envOption = new Option<string>("--config-env") { Recursive = true };
        var loginCommand = new Command("login", "Sign in");
        var wasInvoked = false;
        loginCommand.SetAction((ParseResult _) => { wasInvoked = true; return 0; });

        var rootCommand = new RootCommand();
        var authCommand = new Command("auth");
        authCommand.Subcommands.Add(loginCommand);
        rootCommand.Options.Add(envOption);
        rootCommand.Subcommands.Add(authCommand);

        var result = await rootCommand.Parse("--config-env Development auth login").InvokeAsync();

        Assert.True(wasInvoked);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task GlobalOption_ClientId_IsAccepted()
    {
        var clientIdOption = new Option<string?>("--client-id") { Recursive = true };
        var loginCommand = new Command("login", "Sign in");
        var wasInvoked = false;
        loginCommand.SetAction((ParseResult _) => { wasInvoked = true; return 0; });

        var rootCommand = new RootCommand();
        var authCommand = new Command("auth");
        authCommand.Subcommands.Add(loginCommand);
        rootCommand.Options.Add(clientIdOption);
        rootCommand.Subcommands.Add(authCommand);

        var result = await rootCommand.Parse("--client-id some-id auth login").InvokeAsync();

        Assert.True(wasInvoked);
        Assert.Equal(0, result);
    }
}
