// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
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
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());
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
        Program.ApiClientFactory = _ => PlayFabTestClient.Create(handler, auth);
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
        Assert.Equal(1, auth.TokenRequests);
        Assert.True(auth.TokenRequestCancellationToken.IsCancellationRequested);
        Assert.True(handler.LastCancellationToken.IsCancellationRequested);
    }
}
