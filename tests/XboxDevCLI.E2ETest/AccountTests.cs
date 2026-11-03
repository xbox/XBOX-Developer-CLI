// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace XboxDevCLI.E2ETest;

public class AccountTests
{
    private static E2ESettings Settings => E2ESettings.Current;

    [LiveFact]
    public async Task AuthStatus_ShowsTheSignedInAccount()
    {
        CliResult result = await Cli.RunAsync("auth", "status", "--output", "json-compact");

        JsonElement status = result.Json();
        result.Check(status.Flag("signedIn") == true, "Expected to be signed in. Run 'xbox auth login' first.");
        result.Check(status.Is("username", Settings.Username), $"Expected to be signed in as {Settings.Username}.");
        result.Check(
            status.Text("tokenExpiresAt") is not null,
            "Expected a token from the cache, which every gateway command needs.");
    }

    [LiveFact]
    public async Task SellerList_IncludesTheSeller()
    {
        CliResult result = await Cli.RunAsync("seller", "list", "--output", "json-compact");

        result.Check(
            result.Json().Items("sellers").Any(seller => seller.Is("sellerId", Settings.SellerId)),
            $"Expected seller {Settings.SellerId}.");
    }
}
