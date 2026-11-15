// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using XboxDevCLI.Services;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// A <see cref="PlayFabApiClient"/> for the title <c>TEST123</c> that sends through the given
/// handler, signed in with <see cref="FakeAuthenticationService"/> unless a test passes its own.
/// </summary>
internal static class PlayFabTestClient
{
    public static IConfiguration CreateConfiguration(long? maxResponseBytes = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayFab:TitleId"] = "TEST123",
                ["PlayFab:BaseUrlTemplate"] = "https://{0}.playfabapi.com",
                ["PlayFab:MaxResponseBytes"] = maxResponseBytes?.ToString(),

                // AuthenticationService checks these when it's constructed, a Moq one included.
                ["AzureAd:ClientId"] = "test-client-id",
                ["AzureAd:TenantId"] = "test-tenant-id",
                ["AzureAd:Scopes"] = "api://test/.default",
            })
            .Build();

    public static PlayFabApiClient Create(
        HttpMessageHandler handler,
        AuthenticationService? authentication = null,
        long? maxResponseBytes = null) =>
        new(
            CreateConfiguration(maxResponseBytes),
            authentication ?? new FakeAuthenticationService(FakeAuthenticationService.CreateToken()),
            new HttpClient(handler));
}
