// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.Json;
using Xunit;

namespace XboxDevCLI.EndToEndTests;

/// <summary>
/// Read-only scenarios run against the CLI under test. Service calls are Get or List;
/// the workflow selects the harness commit independently of the candidate executable.
/// </summary>
[Collection(LiveCollection.Name)]
public sealed class LiveReadOnlyTests(LiveCliFixture fixture)
{
    [LiveFact(LiveService.Authentication)]
    public async Task AuthStatus_ReportsTheEnvironmentServicePrincipal()
    {
        using var status = await fixture.RunJsonAsync("auth", "status", "--output", "json-compact");
        var root = status.RootElement;

        Assert.True(root.GetProperty("signedIn").GetBoolean(), "auth status did not report signedIn.");
        Assert.Equal("githuboidc", root.GetProperty("authenticationMode").GetString());
        Assert.Equal("app", root.GetProperty("principalType").GetString());
        Assert.Equal(fixture.Settings.ClientId, root.GetProperty("appId").GetString(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(fixture.Settings.TenantId, root.GetProperty("tenantId").GetString(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(fixture.Settings.Profile.Audience, root.GetProperty("audience").GetString());
    }

    [LiveFact(LiveService.PlayFab)]
    public async Task ApiCall_GetTitleData_ReturnsTheSentinel()
    {
        var playFab = fixture.Settings.Profile.PlayFab;
        var body = fixture.WriteBodyFile("get-title-data.json", new { Keys = new[] { playFab.TitleData.Key } });

        using var response = await fixture.RunJsonAsync(
            "--playfab-title-id", playFab.TitleId,
            "playfab", "api", "call", "/Admin/GetTitleData",
            "--body-file", body,
            "--output", "json-compact");

        AssertTitleDataSentinel(response.RootElement, "playfab api call /Admin/GetTitleData");
    }

    [LiveFact(LiveService.PlayFab)]
    public async Task TitleDataGet_ReturnsTheSentinel()
    {
        var playFab = fixture.Settings.Profile.PlayFab;

        // An explicit --output returns the envelope instead of the single-key bare value.
        using var response = await fixture.RunJsonAsync(
            "--playfab-title-id", playFab.TitleId,
            "playfab", "title-data", "get", playFab.TitleData.Key,
            "--output", "json-compact");

        AssertTitleDataSentinel(response.RootElement, "playfab title-data get");
    }

    [LiveFact(LiveService.XboxPartner)]
    public async Task SellerList_ReturnsASellerArray()
    {
        using var response = await fixture.RunJsonAsync("seller", "list", "--output", "json-compact");

        Assert.True(
            response.RootElement.TryGetProperty("sellers", out var sellers)
                && sellers.ValueKind == JsonValueKind.Array,
            "seller list did not return a sellers array.");
    }

    [LiveFact(LiveService.XboxPartner)]
    public async Task GameList_ReadsTheFixtureSeller()
    {
        using var response = await fixture.RunJsonAsync(
            "game", "list",
            "--seller-id", fixture.Settings.Profile.XboxPartner.SellerId,
            "--output", "json-compact");

        Assert.True(
            response.RootElement.TryGetProperty("games", out var games)
                && games.ValueKind == JsonValueKind.Array,
            "game list did not return a games array.");
    }

    private void AssertTitleDataSentinel(JsonElement envelope, string command)
    {
        var fixtureData = fixture.Settings.Profile.PlayFab.TitleData;
        Assert.True(
            envelope.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() == 200,
            $"{command} did not return a 200 envelope.");

        // Report only whether the sentinel matched; the response itself stays out of logs.
        var matched = envelope.TryGetProperty("data", out var data)
            && data.TryGetProperty("Data", out var values)
            && values.TryGetProperty(fixtureData.Key, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() == fixtureData.Value;
        Assert.True(
            matched,
            $"{command} did not return the expected value for title-data key '{fixtureData.Key}'.");
    }
}
