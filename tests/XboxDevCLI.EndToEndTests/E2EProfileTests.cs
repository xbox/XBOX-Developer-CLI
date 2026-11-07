// Copyright (C) Microsoft Corporation. All rights reserved.

using Xunit;

namespace XboxDevCLI.EndToEndTests;

/// <summary>Offline checks of environment configuration loading and validation.</summary>
public sealed class E2EProfileTests
{
    [Theory]
    [InlineData(LiveSettings.EnvironmentVariable)]
    [InlineData(E2EProfiles.AudienceVariable)]
    [InlineData(E2EProfiles.HostSuffixVariable)]
    [InlineData(E2EProfiles.PlayFabBaseUrlVariable)]
    [InlineData(E2EProfiles.TitleIdVariable)]
    [InlineData(E2EProfiles.XboxPartnerBaseUrlVariable)]
    [InlineData(E2EProfiles.SellerIdVariable)]
    public void Load_RequiresEachSetting(string name)
    {
        foreach (var value in new string?[] { null, "", " " })
        {
            var exception = Assert.Throws<InvalidDataException>(() => E2EProfiles.Load(Variables((name, value))));

            Assert.Contains(name, exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Load_AllowsAnOmittedScopeOverride(string? scope)
    {
        var profile = E2EProfiles.Load(Variables((LiveSettings.ScopesVariable, scope)));

        Assert.Null(profile.Scope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{titleId}.playfabapi.com")]
    public void Load_KeepsOnlyTheRequestedHostHeader(string? header)
    {
        var profile = E2EProfiles.Load(Variables((E2EProfiles.HostHeaderVariable, header)));

        Assert.Equal(string.IsNullOrEmpty(header) ? null : header, profile.PlayFab.HostHeader);
    }

    public static TheoryData<string, string> InvalidGateways() => new()
    {
        { "http://{0}.api.nodpublishers.com", "https origin" },
        { "https://{0}.api.nodpublishers.com:8443", "https origin" },
        { "https://user@{0}.api.nodpublishers.com", "https origin" },
        { "https://{0}.api.nodpublishers.com/path", "https origin" },
        { "https://{0}.api.nodpublishers.com/", "https origin" },
        { "https://{0}.api.nodpublishers.com?x=1", "https origin" },
        { "https://{0}.api.nodpublishers.com#fragment", "https origin" },
        { "https://{0}.api.lucernepublishing.com", "must be under nodpublishers.com" },
        { "https://{0}.nodpublishers.com.example.com", "must be under nodpublishers.com" },
        { "https://{0}.notnodpublishers.com", "must be under nodpublishers.com" },
        { "https://nodpublishers.com", "must be under nodpublishers.com" },
        { "https://{titleId}.api.nodpublishers.com", "only the {0} title placeholder" },
    };

    [Theory]
    [MemberData(nameof(InvalidGateways))]
    public void Load_RejectsUnapprovedPlayFabGateways(string template, string expected)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            E2EProfiles.Load(Variables((E2EProfiles.PlayFabBaseUrlVariable, template))));

        Assert.Contains(E2EProfiles.PlayFabBaseUrlVariable, exception.Message, StringComparison.Ordinal);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://partner.nodpublishers.com")]
    [InlineData("https://partner.nodpublishers.com/path")]
    [InlineData("https://partner.lucernepublishing.com")]
    public void Load_ValidatesTheXboxPartnerGatewayToo(string url)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            E2EProfiles.Load(Variables((E2EProfiles.XboxPartnerBaseUrlVariable, url))));

        Assert.Contains(E2EProfiles.XboxPartnerBaseUrlVariable, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".nodpublishers.com")]
    [InlineData("nodpublishers.com.")]
    [InlineData("*.nodpublishers.com")]
    [InlineData("com")]
    [InlineData("127.0.0.1")]
    [InlineData("nodpublishers.com,lucernepublishing.com")]
    [InlineData("https://nodpublishers.com")]
    public void Load_RequiresOneExplicitDomainSuffix(string suffix)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            E2EProfiles.Load(Variables((E2EProfiles.HostSuffixVariable, suffix))));

        Assert.Contains(E2EProfiles.HostSuffixVariable, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nodpublishers.com")]
    [InlineData("NODPUBLISHERS.COM")]
    public void Load_AcceptsGatewaysUnderTheConfiguredSuffix(string suffix)
    {
        E2EProfiles.Load(Variables((E2EProfiles.HostSuffixVariable, suffix)));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("{titleId}.example.com")]
    [InlineData("{titleId}.playfabapi.com\r\nX-Other: value")]
    public void Load_RejectsOtherRoutingHeaderValues(string header)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            E2EProfiles.Load(Variables((E2EProfiles.HostHeaderVariable, header))));

        Assert.Contains(E2EProfiles.HostHeaderVariable, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LiveSettings.ScopesVariable, "other/.default")]
    [InlineData(LiveSettings.ScopesVariable, " ")]
    [InlineData(LiveSettings.ScopesVariable, "00000000-0000-0000-0000-000000000042/user.read")]
    [InlineData(E2EProfiles.AudienceVariable, "other")]
    public void Load_RequiresTheScopeToMatchTheExpectedAudience(string name, string value)
    {
        var exception = Assert.Throws<InvalidDataException>(() => E2EProfiles.Load(Variables((name, value))));

        Assert.Contains(LiveSettings.ScopesVariable, exception.Message, StringComparison.Ordinal);
        Assert.Contains(E2EProfiles.AudienceVariable, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(E2EProfiles.TitleIdVariable, "ABC-123")]
    [InlineData(E2EProfiles.TitleIdVariable, "ABC123\n")]
    [InlineData(E2EProfiles.TitleIdVariable, "ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567")]
    [InlineData(E2EProfiles.SellerIdVariable, "seller")]
    [InlineData(E2EProfiles.SellerIdVariable, "1234567\n")]
    [InlineData(E2EProfiles.SellerIdVariable, "123456789012345678901")]
    public void Load_RejectsInvalidFixtureIdentifiers(string name, string value)
    {
        var exception = Assert.Throws<InvalidDataException>(() => E2EProfiles.Load(Variables((name, value))));

        Assert.Contains(name, exception.Message, StringComparison.Ordinal);
    }

    internal static Func<string, string?> Variables(params (string Name, string? Value)[] overrides)
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [LiveSettings.EnvironmentVariable] = "e2e-nod-arena",
            [LiveSettings.ScopesVariable] = "00000000-0000-0000-0000-000000000042/.default",
            [E2EProfiles.AudienceVariable] = "00000000-0000-0000-0000-000000000042",
            [E2EProfiles.HostSuffixVariable] = "nodpublishers.com",
            [E2EProfiles.PlayFabBaseUrlVariable] = "https://{0}.api.nodpublishers.com",
            [E2EProfiles.TitleIdVariable] = "ABC123",
            [E2EProfiles.XboxPartnerBaseUrlVariable] = "https://partner.nodpublishers.com",
            [E2EProfiles.SellerIdVariable] = "1234567",
        };
        foreach (var (name, value) in overrides)
        {
            variables[name] = value;
        }

        return name => variables.GetValueOrDefault(name);
    }
}
