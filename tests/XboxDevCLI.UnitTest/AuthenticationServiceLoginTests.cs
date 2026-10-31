// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

// LoginAsync registers the real token cache, which warns on stderr when Linux has no keyring.
[Collection("ConsoleRedirect")]
public class AuthenticationServiceLoginTests
{
    [Fact]
    public async Task LoginAsync_SignsOutEveryOtherCachedAccount()
    {
        var personal = CreateAccount("personal", "tenant-a");
        var work = CreateAccount("work", "tenant-b");
        var other = CreateAccount("other", "tenant-c");
        var mockApp = CreateMockApplication(personal, work, other);
        // MSAL returns a new account object for the user who signed in, not the cached one.
        var signedIn = SignInAs(CreateAccount("work", "tenant-b"));
        var auth = new BrowserlessAuthenticationService(mockApp.Object, () => Task.FromResult(signedIn));

        var result = await auth.LoginAsync(CancellationToken.None);

        Assert.Same(signedIn, result);
        mockApp.Verify(app => app.RemoveAsync(personal), Times.Once);
        mockApp.Verify(app => app.RemoveAsync(other), Times.Once);
        mockApp.Verify(app => app.RemoveAsync(work), Times.Never);
    }

    [Fact]
    public async Task LoginAsync_WhenSignInFails_KeepsCachedAccounts()
    {
        var mockApp = CreateMockApplication(CreateAccount("personal", "tenant-a"));
        var auth = new BrowserlessAuthenticationService(
            mockApp.Object,
            () => Task.FromException<AuthenticationResult>(
                new MsalClientException("authentication_canceled", "User canceled authentication.")));

        await Assert.ThrowsAsync<MsalClientException>(() => auth.LoginAsync(CancellationToken.None));

        mockApp.Verify(app => app.RemoveAsync(It.IsAny<IAccount>()), Times.Never);
    }

    [Fact]
    public async Task AcquireTokenSilentAsync_WithMoreThanOneCachedAccount_RefusesToPickOne()
    {
        var mockApp = CreateMockApplication(CreateAccount("personal", "tenant-a"), CreateAccount("work", "tenant-b"));
        var auth = new AuthenticationService(CreateAuthConfig(), mockApp.Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.AcquireTokenSilentAsync(CancellationToken.None));

        Assert.Contains("auth login", exception.Message);
    }

    private sealed class BrowserlessAuthenticationService(
        IPublicClientApplication app,
        Func<Task<AuthenticationResult>> signIn)
        : AuthenticationService(CreateAuthConfig(), app)
    {
        protected override Task<AuthenticationResult> AcquireTokenInteractiveAsync(
            CancellationToken cancellationToken) => signIn();
    }

    private static IAccount CreateAccount(string objectId, string tenantId) =>
        Mock.Of<IAccount>(account =>
            account.Username == $"{objectId}@example.com"
            && account.HomeAccountId == new AccountId($"{objectId}.{tenantId}", objectId, tenantId));

    private static AuthenticationResult SignInAs(IAccount account) => new(
        "access-token", false, "unique-id", DateTimeOffset.UtcNow.AddHours(1),
        DateTimeOffset.UtcNow.AddHours(1), account.HomeAccountId.TenantId, account,
        "id-token", ["openid"], Guid.NewGuid());

    private static Mock<IPublicClientApplication> CreateMockApplication(params IAccount[] accounts)
    {
        var tokenCache = new Mock<ITokenCache>();
        var mockApp = new Mock<IPublicClientApplication>();
        mockApp.SetupGet(app => app.UserTokenCache).Returns(tokenCache.Object);
        mockApp.Setup(app => app.GetAccountsAsync()).ReturnsAsync(accounts);
        mockApp.Setup(app => app.RemoveAsync(It.IsAny<IAccount>())).Returns(Task.CompletedTask);
        return mockApp;
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
