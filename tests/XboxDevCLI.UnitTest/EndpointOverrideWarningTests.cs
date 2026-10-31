// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using XboxDevCLI.Safety;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class EndpointOverrideWarningTests
{
    [Fact]
    public void IsOverridden_DefaultEndpointIsNotAnOverride()
    {
        Assert.False(EndpointOverrideWarning.IsOverridden(
            EndpointOverrideWarning.DefaultBaseUrlTemplate));
        Assert.False(EndpointOverrideWarning.IsOverridden(
            "  " + EndpointOverrideWarning.DefaultBaseUrlTemplate.ToUpperInvariant() + "/  "));
    }

    [Fact]
    public void IsOverridden_MissingValueUsesTheShippedDefault()
    {
        Assert.False(EndpointOverrideWarning.IsOverridden(null));
        Assert.Equal(
            EndpointOverrideWarning.DefaultBaseUrlTemplate,
            EndpointOverrideWarning.GetEffectiveBaseUrlTemplate(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsOverridden_BlankValueIsAnOverride(string baseUrlTemplate)
    {
        Assert.True(EndpointOverrideWarning.IsOverridden(baseUrlTemplate));
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("https://{0}.evil.example.com")]
    [InlineData("http://localhost:5000")]
    public void IsOverridden_DifferentEndpointIsAnOverride(string baseUrlTemplate)
    {
        Assert.True(EndpointOverrideWarning.IsOverridden(baseUrlTemplate));
    }

    [Fact]
    public void GetWarning_DefaultEndpointReturnsNull()
    {
        Assert.Null(EndpointOverrideWarning.GetWarning(
            EndpointOverrideWarning.DefaultBaseUrlTemplate));
    }

    [Fact]
    public void GetWarning_OverriddenEndpointNamesTheHost()
    {
        var warning = EndpointOverrideWarning.GetWarning("https://{0}.evil.example.com");

        Assert.NotNull(warning);
        Assert.Contains("Warning", warning);
        Assert.Contains("titleid.evil.example.com", warning);
        Assert.Contains(EndpointOverrideWarning.ConfigurationKey, warning);
        Assert.Contains(EndpointOverrideWarning.DefaultBaseUrlTemplate, warning);
    }

    [Fact]
    public void GetWarning_SchemeDowngradeIsNotHidden()
    {
        var warning = EndpointOverrideWarning.GetWarning("http://xboxdevcli.playfabapi.com");

        Assert.NotNull(warning);
        Assert.Contains("http://xboxdevcli.playfabapi.com", warning);
    }

    [Fact]
    public void GetWarning_NonDefaultPortIsNotHidden()
    {
        var warning = EndpointOverrideWarning.GetWarning("https://xboxdevcli.playfabapi.com:444");

        Assert.NotNull(warning);
        Assert.Contains("https://xboxdevcli.playfabapi.com:444", warning);
    }

    [Fact]
    public void GetWarning_NonRootPathIsNotHidden()
    {
        var warning = EndpointOverrideWarning.GetWarning("https://xboxdevcli.playfabapi.com/proxy");

        Assert.NotNull(warning);
        Assert.Contains("https://xboxdevcli.playfabapi.com/proxy", warning);
    }

    [Theory]
    [InlineData("https://evil.example.com/proxy?mode=steal", "https://evil.example.com/proxy?mode=steal")]
    [InlineData("https://evil.example.com/?mode=steal", "https://evil.example.com/?mode=steal")]
    [InlineData("https://evil.example.com?mode=steal", "https://evil.example.com/?mode=steal")]
    public void GetWarning_QueryParametersAreNotHidden(string baseUrlTemplate, string expectedTarget)
    {
        var warning = EndpointOverrideWarning.GetWarning(baseUrlTemplate);

        Assert.NotNull(warning);
        Assert.Contains($"overridden to '{expectedTarget}'", warning);
    }

    [Fact]
    public void GetWarning_UnparsableTemplateFallsBackToRawValue()
    {
        var warning = EndpointOverrideWarning.GetWarning("not a url");

        Assert.NotNull(warning);
        Assert.Contains("not a url", warning);
    }

    [Theory]
    [InlineData("not-a-url\nspoofed warning", @"not-a-url\u000aspoofed warning")]
    [InlineData("not-a-url\u001b[2J", @"not-a-url\u001b[2J")]
    [InlineData("not-a-url\r\n\u001b[2J", @"not-a-url\u000d\u000a\u001b[2J")]
    [InlineData("not-a-url\t\0\b\u007f\u009b[2J", @"not-a-url\u0009\u0000\u0008\u007f\u009b[2J")]
    [InlineData("not-a-url\u2028spoofed warning", @"not-a-url\u2028spoofed warning")]
    [InlineData("not-a-url\u2029spoofed warning", @"not-a-url\u2029spoofed warning")]
    [InlineData("not-a-url\u202eevil", @"not-a-url\u202eevil")]
    [InlineData("not-a-url\u2066evil\u2069", @"not-a-url\u2066evil\u2069")]
    public void WriteIfOverridden_UnparsableTemplateEscapesControlCharacters(
        string baseUrlTemplate,
        string expectedTarget)
    {
        using var writer = new StringWriter();

        EndpointOverrideWarning.WriteIfOverridden(BuildConfiguration(baseUrlTemplate), writer);

        var warning = writer.ToString();
        Assert.Contains($"overridden to '{expectedTarget}'", warning);
        Assert.Equal(4, warning.Split(Environment.NewLine).Length);
        Assert.DoesNotContain(warning.Replace(Environment.NewLine, string.Empty), char.IsControl);
        Assert.DoesNotContain(warning, character =>
            character is '\u2028' or '\u2029' or '\u202e' or '\u2066' or '\u2069');
    }

    [Fact]
    public void WriteIfOverridden_DefaultEndpointWritesNothing()
    {
        using var writer = new StringWriter();

        EndpointOverrideWarning.WriteIfOverridden(
            BuildConfiguration(EndpointOverrideWarning.DefaultBaseUrlTemplate),
            writer);

        Assert.Empty(writer.ToString());
    }

    [Fact]
    public void WriteIfOverridden_OverriddenEndpointWritesTheWarning()
    {
        using var writer = new StringWriter();

        EndpointOverrideWarning.WriteIfOverridden(
            BuildConfiguration("https://evil.example.com"),
            writer);

        Assert.Contains("evil.example.com", writer.ToString());
    }

    [Fact]
    public void WriteXboxPartnerWarningIfOverridden_DefaultGatewayWritesNothing()
    {
        using var writer = new StringWriter();

        EndpointOverrideWarning.WriteXboxPartnerWarningIfOverridden(
            EndpointOverrideWarning.DefaultXboxPartnerBaseUrl,
            writer);

        Assert.Empty(writer.ToString());
    }

    [Fact]
    public void WriteXboxPartnerWarningIfOverridden_CanDescribeTheTestAccountService()
    {
        using var writer = new StringWriter();

        EndpointOverrideWarning.WriteXboxPartnerWarningIfOverridden(
            "https://nodpublishers.com", writer, serviceName: "service", includeDefaultUrl: false);

        Assert.StartsWith("Warning: the service is overridden", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("XboxPartner:BaseUrl", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("https://nodpublishers.com", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("XboxPartner gateway", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(EndpointOverrideWarning.DefaultXboxPartnerBaseUrl, writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("Requests and your access token will be sent to that host", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("XBOX_XBOXPARTNER__BASEURL", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("If you did not intend this", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GetXboxPartnerWarning_OmittingDefaultUrlStillDetectsTheDefaultEndpoint()
    {
        Assert.Null(EndpointOverrideWarning.GetXboxPartnerWarning(
            EndpointOverrideWarning.DefaultXboxPartnerBaseUrl, serviceName: "service", includeDefaultUrl: false));
    }

    [Fact]
    public void GetXboxPartnerWarning_DefaultPresentationIsUnchanged()
    {
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Warning: the XboxPartner gateway is overridden to 'https://nodpublishers.com' " +
                    "(configuration key 'XboxPartner:BaseUrl').",
                "         Requests and your access token will be sent to that host instead of the default gateway " +
                    $"'{EndpointOverrideWarning.DefaultXboxPartnerBaseUrl}'.",
                "         If you did not intend this, inspect appsettings.json, any appsettings.{environment}.json " +
                    "overlay, the XBOX_XBOXPARTNER__BASEURL environment variable, and the --base-url option."),
            EndpointOverrideWarning.GetXboxPartnerWarning("https://nodpublishers.com"));
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://localhost:54848")]
    public void WriteXboxPartnerWarningIfOverridden_OverriddenGatewayWritesTheWarning(string baseUrl)
    {
        using var writer = new StringWriter();

        EndpointOverrideWarning.WriteXboxPartnerWarningIfOverridden(baseUrl, writer);

        var warning = writer.ToString();
        Assert.Contains(baseUrl, warning, StringComparison.Ordinal);
        Assert.Contains(EndpointOverrideWarning.XboxPartnerConfigurationKey, warning, StringComparison.Ordinal);
        Assert.Contains("--base-url", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "<blank>")]
    [InlineData("   ", "<blank>")]
    public void WriteIfOverridden_BlankConfigurationValueWritesWarning(
        string? baseUrlTemplate,
        string expectedTarget)
    {
        using var writer = new StringWriter();

        EndpointOverrideWarning.WriteIfOverridden(
            BuildConfiguration(baseUrlTemplate),
            writer);

        Assert.Contains("Warning", writer.ToString());
        Assert.Contains(expectedTarget, writer.ToString());
    }

    [Fact]
    public void WriteIfOverridden_MissingConfigurationValueWritesNothing()
    {
        using var writer = new StringWriter();

        EndpointOverrideWarning.WriteIfOverridden(BuildConfiguration(null), writer);

        Assert.Empty(writer.ToString());
    }

    private static IConfiguration BuildConfiguration(string? baseUrlTemplate) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EndpointOverrideWarning.ConfigurationKey] = baseUrlTemplate,
            })
            .Build();
}
