// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class AuthenticationServiceLogoutTests
{
    [Fact]
    public async Task LogoutAsync_RemovesEveryCachedAccount()
    {
        var account1 = Mock.Of<IAccount>(account => account.Username == "first@example.com");
        var account2 = Mock.Of<IAccount>(account => account.Username == "second@example.com");
        var mockApp = CreateMockApplication(account1, account2);
        var auth = new AuthenticationService(CreateAuthConfig(), mockApp.Object);

        var result = await auth.LogoutAsync(CancellationToken.None);

        Assert.Equal(2, result.RemovedAccountCount);
        Assert.Equal(["first@example.com", "second@example.com"], result.Usernames);
        mockApp.Verify(app => app.RemoveAsync(account1), Times.Once);
        mockApp.Verify(app => app.RemoveAsync(account2), Times.Once);
        mockApp.Verify(app => app.RemoveAsync(It.IsAny<IAccount>()), Times.Exactly(2));
    }

    [Fact]
    public async Task LogoutAsync_WithNoCachedAccounts_ReturnsEmptyResult()
    {
        var mockApp = CreateMockApplication();
        var auth = new AuthenticationService(CreateAuthConfig(), mockApp.Object);

        var result = await auth.LogoutAsync(CancellationToken.None);

        Assert.Equal(0, result.RemovedAccountCount);
        Assert.Empty(result.Usernames);
        mockApp.Verify(app => app.RemoveAsync(It.IsAny<IAccount>()), Times.Never);
    }

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
