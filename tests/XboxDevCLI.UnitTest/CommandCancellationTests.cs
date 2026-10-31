// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Ctrl-C and SIGTERM cancel the token System.CommandLine hands each action. These drive
/// registered PlayFab commands, one per route to the API, cancel that token while the request
/// is in flight, and check it reached both the sign-in and the HTTP request.
/// </summary>
[Collection("ConsoleRedirect")]
public class CommandCancellationTests
{
    [Theory]
    [InlineData("playfab", "api", "/Admin/GetTitleData")]
    [InlineData("playfab", "title-data", "get")]
    [InlineData("playfab", "player", "account", "get", "--playfab-id", "ABC")]
    public async Task CanceledInvocation_ReachesThePlayFabRequest(params string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayFab:TitleId"] = "TEST123",
                ["PlayFab:BaseUrlTemplate"] = "https://{0}.playfabapi.com",
                ["AzureAd:ClientId"] = "test-client-id",
                ["AzureAd:TenantId"] = "test-tenant-id",
                ["AzureAd:Scopes"] = "api://test/.default",
            })
            .Build();
        var auth = new Mock<AuthenticationService>(config) { CallBase = false };
        CancellationToken authToken = default;
        auth.Setup(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()))
            .Callback((CancellationToken token) => authToken = token)
            .ReturnsAsync(new AuthenticationResult(
                "token", false, "id", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1),
                "tenant", Mock.Of<IAccount>(), "id-token", ["api://test/.default"], Guid.NewGuid()));
        using var cts = new CancellationTokenSource();

        // Cancel while the request is in flight; a token canceled earlier stops the call before it's sent.
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"code":200,"status":"OK","data":{}}"""),
        })
        {
            OnSend = token =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
            },
        };

        var originalOut = Console.Out;
        var originalError = Console.Error;
        Program.ApiClientFactory = _ => new PlayFabApiClient(
            config, auth.Object, new HttpClient(handler) { BaseAddress = new Uri("https://TEST123.playfabapi.com/") });
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());
        int exitCode;
        try
        {
            exitCode = await Program.BuildRootCommand()
                .Parse(args, Program.NoResponseFileConfig)
                .InvokeAsync(cancellationToken: cts.Token);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Program.ApiClientFactory = null;
        }

        Assert.NotEqual(0, exitCode);
        auth.Verify(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(authToken.IsCancellationRequested);
        Assert.True(handler.LastCancellationToken.IsCancellationRequested);
    }
}
