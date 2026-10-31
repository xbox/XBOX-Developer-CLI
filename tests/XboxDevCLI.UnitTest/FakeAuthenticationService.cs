// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.Services;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// An <see cref="AuthenticationService"/> signed in with a fixed token, or signed out when the
/// token is <c>null</c>. Its MSAL client is a mock, so neither the real token cache nor the
/// network is touched.
/// </summary>
internal sealed class FakeAuthenticationService : AuthenticationService
{
    private static readonly IConfiguration DefaultConfiguration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAd:ClientId"] = "test-client",
            ["AzureAd:TenantId"] = "test-tenant",
            ["AzureAd:Scopes"] = "api://test/.default",
        })
        .Build();

    private readonly AuthenticationResult? _token;

    public FakeAuthenticationService(AuthenticationResult? token, IConfiguration? configuration = null)
        : base(configuration ?? DefaultConfiguration, Mock.Of<IPublicClientApplication>())
    {
        _token = token;
        CachedAccount = token?.Account;
    }

    /// <summary>The account the token cache reports. Defaults to the token's account.</summary>
    public IAccount? CachedAccount { get; init; }

    /// <summary>Thrown when the cached account is read, as an unreadable token cache would.</summary>
    public Exception? CachedAccountException { get; init; }

    /// <summary>How many times a token was requested, which can mean a network call.</summary>
    public int TokenRequests { get; private set; }

    /// <summary>How many times the cached account was read, which never needs the network.</summary>
    public int AccountRequests { get; private set; }

    /// <summary>The cancellation token passed with the latest token request.</summary>
    public CancellationToken TokenRequestCancellationToken { get; private set; }

    /// <summary>Thrown when a token is requested, as a failed or canceled refresh would.</summary>
    public Exception? TokenRequestException { get; init; }

    public override Task<AuthenticationResult?> AcquireTokenSilentAsync(
        CancellationToken cancellationToken)
    {
        TokenRequests++;
        TokenRequestCancellationToken = cancellationToken;
        return TokenRequestException is null
            ? Task.FromResult(_token)
            : Task.FromException<AuthenticationResult?>(TokenRequestException);
    }

    public override Task<IAccount?> GetCachedAccountAsync(CancellationToken cancellationToken)
    {
        AccountRequests++;
        return CachedAccountException is null
            ? Task.FromResult(CachedAccount)
            : Task.FromException<IAccount?>(CachedAccountException);
    }

    /// <summary>A token for the given tenant and user, shaped as MSAL returns it.</summary>
    public static AuthenticationResult CreateToken(
        string tenantId = "tenant",
        string objectId = "user",
        string username = "dev@nodpublishers.com") =>
        new(
            "access-token",
            false,
            objectId,
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow.AddHours(1),
            tenantId,
            CreateAccount(tenantId, objectId, username),
            "id-token",
            ["api://test/.default"],
            Guid.NewGuid());

    /// <summary>An account as the token cache reports it.</summary>
    public static IAccount CreateAccount(
        string tenantId = "tenant",
        string objectId = "user",
        string username = "dev@nodpublishers.com") =>
        Mock.Of<IAccount>(account =>
            account.HomeAccountId == new AccountId($"{objectId}.{tenantId}", objectId, tenantId)
            && account.Username == username
            && account.Environment == "login.microsoftonline.com");
}
