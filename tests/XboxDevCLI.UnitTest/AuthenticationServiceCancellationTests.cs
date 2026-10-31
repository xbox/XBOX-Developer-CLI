// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// MSAL's account lookup takes no cancellation token, so Ctrl-C must still end the wait on it.
/// </summary>
// Registering the real token cache warns on stderr when Linux has no keyring.
[Collection("ConsoleRedirect")]
public class AuthenticationServiceCancellationTests
{
    public static TheoryData<string> AccountReads => ["account", "token", "logout"];

    [Theory]
    [MemberData(nameof(AccountReads))]
    public async Task CancelingWhileTheAccountLookupIsPending_StopsWaiting(string read)
    {
        var lookup = new TaskCompletionSource<IEnumerable<IAccount>>();
        var app = new Mock<IPublicClientApplication>();
        app.SetupGet(application => application.UserTokenCache).Returns(Mock.Of<ITokenCache>());
        app.Setup(application => application.GetAccountsAsync()).Returns(lookup.Task);
        var auth = new AuthenticationService(CreateAuthConfig(), app.Object);
        using var cts = new CancellationTokenSource();

        Task pending = read switch
        {
            "account" => auth.GetCachedAccountAsync(cts.Token),
            "token" => auth.AcquireTokenSilentAsync(cts.Token),
            _ => auth.LogoutAsync(cts.Token),
        };
        Assert.False(pending.IsCompleted);

        await cts.CancelAsync();

        // A timeout fails a regression (TimeoutException) instead of hanging the run.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// MSAL's removal takes no token either. Ctrl-C mid-logout stops the wait and starts no
    /// further removal, so logout never goes on to report success.
    /// </summary>
    [Fact]
    public async Task CancelingLogoutWhileAnAccountRemovalIsPending_StopsWithoutRemovingMore()
    {
        var (app, removal) = CreateAppWithPendingRemoval();
        var auth = new AuthenticationService(CreateAuthConfig(), app.Object);
        using var cts = new CancellationTokenSource();

        Task pending = auth.LogoutAsync(cts.Token);
        Assert.False(pending.IsCompleted);

        await cts.CancelAsync();
        removal.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        app.Verify(application => application.RemoveAsync(It.IsAny<IAccount>()), Times.Once);
    }

    /// <summary>
    /// Once sign-in succeeds, Ctrl-C must not stop login's cleanup: other accounts left cached
    /// would make every command refuse to pick one.
    /// </summary>
    [Fact]
    public async Task CancelingLoginAfterSignIn_StillSignsOutEveryOtherAccount()
    {
        var (app, removal) = CreateAppWithPendingRemoval();
        var auth = new BrowserlessAuthenticationService(app.Object, CreateAccount("signed-in"));
        using var cts = new CancellationTokenSource();

        var pending = auth.LoginAsync(cts.Token);
        Assert.False(pending.IsCompleted);

        await cts.CancelAsync();
        removal.SetResult();

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("signed-in@example.com", result.Account.Username);
        app.Verify(application => application.RemoveAsync(It.IsAny<IAccount>()), Times.Exactly(2));
    }

    private static (Mock<IPublicClientApplication> App, TaskCompletionSource Removal) CreateAppWithPendingRemoval()
    {
        var removal = new TaskCompletionSource();
        var app = new Mock<IPublicClientApplication>();
        app.SetupGet(application => application.UserTokenCache).Returns(Mock.Of<ITokenCache>());
        app.Setup(application => application.GetAccountsAsync())
            .ReturnsAsync([CreateAccount("first"), CreateAccount("second")]);
        app.Setup(application => application.RemoveAsync(It.IsAny<IAccount>())).Returns(removal.Task);
        return (app, removal);
    }

    private sealed class BrowserlessAuthenticationService(IPublicClientApplication app, IAccount account)
        : AuthenticationService(CreateAuthConfig(), app)
    {
        protected override Task<AuthenticationResult> AcquireTokenInteractiveAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new AuthenticationResult(
                "access-token", false, "unique-id", DateTimeOffset.UtcNow.AddHours(1),
                DateTimeOffset.UtcNow.AddHours(1), "tenant", account,
                "id-token", ["openid"], Guid.NewGuid()));
    }

    private static IAccount CreateAccount(string objectId) =>
        Mock.Of<IAccount>(account =>
            account.Username == $"{objectId}@example.com"
            && account.HomeAccountId == new AccountId($"{objectId}.tenant", objectId, "tenant"));

    private static IConfiguration CreateAuthConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000001",
            ["AzureAd:TenantId"] = "organizations",
            ["AzureAd:Scopes"] = "openid",
        })
        .Build();
}
