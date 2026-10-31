// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Moq;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class LogoutCommandTests
{
    [Fact]
    public async Task LogoutCommand_WithRemovedAccount_PrintsSignedOutMessage()
    {
        var mockAuth = CreateMockAuthService();
        mockAuth
            .Setup(auth => auth.LogoutAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LogoutResult(1, ["dev@example.com"]));

        var (exitCode, stdout, stderr) = await RunLogoutAsync(mockAuth.Object);

        Assert.Equal(0, exitCode);
        Assert.Equal($"Signed out dev@example.com.{Environment.NewLine}", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task LogoutCommand_WithNoCachedAccounts_PrintsNotSignedInMessage()
    {
        var mockAuth = CreateMockAuthService();
        mockAuth
            .Setup(auth => auth.LogoutAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LogoutResult(0, Array.Empty<string>()));

        var (exitCode, stdout, stderr) = await RunLogoutAsync(mockAuth.Object);

        Assert.Equal(0, exitCode);
        Assert.Equal($"No account is currently signed in.{Environment.NewLine}", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task LogoutCommand_WhenLogoutFails_WritesErrorAndReturnsOne()
    {
        var mockAuth = CreateMockAuthService();
        mockAuth
            .Setup(auth => auth.LogoutAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated failure"));

        var (exitCode, stdout, stderr) = await RunLogoutAsync(mockAuth.Object);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Equal($"Logout failed: simulated failure{Environment.NewLine}", stderr);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunLogoutAsync(AuthenticationService authService)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var outWriter = new StringWriter();
        using var errorWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errorWriter);

        try
        {
            var rootCommand = CreateRootCommand(LogoutCommandFactory.Create(_ => authService));
            var exitCode = await rootCommand.Parse("logout").InvokeAsync();
            return (exitCode, outWriter.ToString(), errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static RootCommand CreateRootCommand(Command logoutCommand)
    {
        var rootCommand = new RootCommand("XBOX Dev CLI");
        rootCommand.Options.Add(Program.ConfigEnvOption);
        rootCommand.Options.Add(Program.ClientIdOption);
        rootCommand.Options.Add(Program.TenantIdOption);
        rootCommand.Options.Add(Program.ScopesOption);
        rootCommand.Options.Add(Program.PlayFabTitleIdOption);
        rootCommand.Options.Add(Program.OutputOption);
        rootCommand.Options.Add(Program.DataOnlyOption);
        rootCommand.Options.Add(Program.VerboseOption);
        rootCommand.Options.Add(Program.ConfirmOption);
        rootCommand.Subcommands.Add(logoutCommand);
        return rootCommand;
    }

    private static Mock<AuthenticationService> CreateMockAuthService()
    {
        return new Mock<AuthenticationService>(CreateAuthConfig()) { CallBase = false };
    }

    private static IConfiguration CreateAuthConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000001",
            ["AzureAd:TenantId"] = "organizations",
            ["AzureAd:Scopes"] = "openid",
        })
        .Build();
}
