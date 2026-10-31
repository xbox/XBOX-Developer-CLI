// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

// Exercises the real on-disk MSAL token cache (the cache path is not injectable today).
// Safe to run in parallel and needs no test collection: the access here is read-only, and
// MsalCacheHelper serializes cache I/O across instances/processes via its own cross-process
// lock, so it cannot race the other cache-touching tests (e.g. StatusCommandTests).
public class AuthenticationServiceCacheTests
{
    private static IConfiguration CreateAuthConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000001",
            ["AzureAd:TenantId"] = "organizations",
            ["AzureAd:Scopes"] = "openid",
        })
        .Build();

    [Fact]
    public async Task GetCachedAccountAsync_RegistersCacheWithoutThrowing_OnEveryPlatform()
    {
        var auth = new AuthenticationService(CreateAuthConfig());

        // Registering and reading the MSAL token cache must succeed on every supported OS.
        // On Linux/macOS this throws today because the cache is registered without
        // WithLinuxKeyring()/WithMacKeyChain(), leaving the libsecret schema attributes null
        // (ArgumentNullException, parameter 'attributeKey1'). Reading an empty cache should
        // simply return null, not crash.
        var exception = await Record.ExceptionAsync(() => auth.GetCachedAccountAsync(CancellationToken.None));

        Assert.Null(exception);
    }
}
