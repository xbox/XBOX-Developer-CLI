// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace XboxDevCLI.Safety;

/// <summary>
/// Detects when the PlayFab API endpoint (<c>PlayFab:BaseUrlTemplate</c>) has been
/// repointed away from the endpoint shipped with the CLI, and surfaces a visible
/// warning on standard error.
/// </summary>
/// <remarks>
/// Guards against tampering: the endpoint is read from <c>appsettings.json</c>, an
/// environment variable, or an environment overlay, so anyone able to write to those
/// sources can silently redirect every request — and therefore the bearer token sent
/// with it — to a host they control. The CLI cannot prevent the override (it is a
/// legitimate development affordance), so it makes the override loud instead: the
/// effective endpoint is compared against the compiled-in default and a warning naming
/// the target host is written to stderr.
/// </remarks>
internal static class EndpointOverrideWarning
{
    /// <summary>Configuration key holding the PlayFab API endpoint template.</summary>
    internal const string ConfigurationKey = "PlayFab:BaseUrlTemplate";

    /// <summary>
    /// The PlayFab API endpoint shipped in <c>appsettings.json</c>. This is compiled into
    /// the binary on purpose: comparing configuration against itself would be defeated by
    /// the very tampering this check exists to detect.
    /// </summary>
    internal const string DefaultBaseUrlTemplate = "https://xboxdevcli.playfabapi.com";

    /// <summary>Configuration key holding the XboxPartner gateway base URL.</summary>
    internal const string XboxPartnerConfigurationKey = "XboxPartner:BaseUrl";

    /// <summary>
    /// The XboxPartner gateway shipped in <c>appsettings.json</c>, compiled in for the same
    /// reason as <see cref="DefaultBaseUrlTemplate"/>.
    /// </summary>
    /// <remarks>
    /// This is a tamper-detection baseline only; it is never a fallback.
    /// <c>XboxPartnerCommandSupport.ResolveBaseUrl</c> still fails when the configuration key is
    /// missing or blank, so the shipped appsettings.json remains the single source of the
    /// gateway URL and a deployment can still be retargeted without a rebuild.
    /// </remarks>
    internal const string DefaultXboxPartnerBaseUrl = "https://p-xbsvc.xboxpartner.mainp.azureplayfab.com";

    /// <summary>Placeholder substituted for the title-ID format slot when parsing the host.</summary>
    private const string TitleIdPlaceholder = "titleid";

    /// <summary>
    /// Returns the endpoint template that the client will use. A missing value selects the
    /// shipped default; a blank configured value is retained so the client reports its
    /// configuration error rather than silently changing it.
    /// </summary>
    internal static string GetEffectiveBaseUrlTemplate(string? baseUrlTemplate) =>
        baseUrlTemplate ?? DefaultBaseUrlTemplate;

    /// <summary>
    /// Returns <c>true</c> when the effective value of <paramref name="baseUrlTemplate"/>
    /// points somewhere other than <see cref="DefaultBaseUrlTemplate"/>.
    /// </summary>
    internal static bool IsOverridden(string? baseUrlTemplate)
    {
        var effectiveBaseUrlTemplate = GetEffectiveBaseUrlTemplate(baseUrlTemplate);

        return !string.Equals(
            effectiveBaseUrlTemplate.Trim().TrimEnd('/'),
            DefaultBaseUrlTemplate,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the warning text for an overridden endpoint, or returns <c>null</c> when the
    /// effective endpoint is the shipped default.
    /// </summary>
    internal static string? GetWarning(string? baseUrlTemplate)
    {
        if (!IsOverridden(baseUrlTemplate))
        {
            return null;
        }

        return string.Join(
            Environment.NewLine,
            $"Warning: the PlayFab API endpoint is overridden to '{DescribeTarget(baseUrlTemplate)}' " +
            $"(configuration key '{ConfigurationKey}').",
            "         Requests and your access token will be sent to that host instead of the default endpoint " +
            $"'{DefaultBaseUrlTemplate}'.",
            "         If you did not intend this, inspect appsettings.json, any appsettings.{environment}.json " +
            "overlay, and the XBOX_PLAYFAB__BASEURLTEMPLATE environment variable.");
    }

    /// <summary>
    /// Writes the override warning to <paramref name="writer"/> when the endpoint has been
    /// repointed. Called when <c>Program.BuildConfiguration</c> creates the shared invocation
    /// snapshot. Best-effort: a failure to read configuration or write the warning
    /// never fails the command being run.
    /// </summary>
    internal static void WriteIfOverridden(IConfiguration configuration, TextWriter writer)
    {
        try
        {
            var warning = GetWarning(configuration[ConfigurationKey]);
            if (warning == null)
            {
                return;
            }

            writer.WriteLine(warning);
        }
        catch (Exception)
        {
            // The warning is advisory; never let it break the invocation.
        }
    }

    /// <summary>
    /// Builds the warning text for an overridden XboxPartner gateway, or returns <c>null</c>
    /// when the effective gateway is the shipped default.
    /// </summary>
    internal static string? GetXboxPartnerWarning(
        string? baseUrl,
        string serviceName = "XboxPartner gateway",
        bool includeDefaultUrl = true)
    {
        if (string.Equals(
                (baseUrl ?? string.Empty).Trim().TrimEnd('/'),
                DefaultXboxPartnerBaseUrl,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return string.Join(
            Environment.NewLine,
            $"Warning: the {serviceName} is overridden to '{DescribeTarget(baseUrl)}' " +
            $"(configuration key '{XboxPartnerConfigurationKey}').",
            "         Requests and your access token will be sent to that host instead of the default " +
            (includeDefaultUrl ? $"gateway '{DefaultXboxPartnerBaseUrl}'." : "endpoint."),
            "         If you did not intend this, inspect appsettings.json, any appsettings.{environment}.json " +
            "overlay, the XBOX_XBOXPARTNER__BASEURL environment variable, and the --base-url option.");
    }

    /// <summary>
    /// Writes the XboxPartner override warning to <paramref name="writer"/> when the gateway has
    /// been repointed. Called from <c>XboxPartnerCommandSupport.ResolveBaseUrl</c>, which is the
    /// single point where the option and configuration sources converge, so the warning covers
    /// <c>--base-url</c> as well and is emitted before any request carries a token. Best-effort:
    /// a failure to write the warning never fails the command being run.
    /// </summary>
    internal static void WriteXboxPartnerWarningIfOverridden(
        string? baseUrl,
        TextWriter writer,
        string serviceName = "XboxPartner gateway",
        bool includeDefaultUrl = true)
    {
        try
        {
            var warning = GetXboxPartnerWarning(baseUrl, serviceName, includeDefaultUrl);
            if (warning == null)
            {
                return;
            }

            writer.WriteLine(warning);
        }
        catch (Exception)
        {
            // The warning is advisory; never let it break the invocation.
        }
    }

    /// <summary>
    /// Describes the override target as its full authority (scheme, host, and any
    /// non-default port) plus path and query when the template parses as an absolute URI,
    /// otherwise as the raw template with controls, separators, and formatting characters escaped.
    /// Preserves scheme downgrades, non-default ports, non-root paths, and query parameters in the warning.
    /// </summary>
    private static string DescribeTarget(string? baseUrlTemplate)
    {
        if (string.IsNullOrWhiteSpace(baseUrlTemplate))
        {
            return baseUrlTemplate is null ? "<missing>" : "<blank>";
        }

        var trimmed = baseUrlTemplate.Trim();
        var candidate = trimmed.Replace("{0}", TitleIdPlaceholder, StringComparison.Ordinal);
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return string.Concat(trimmed.Select(character =>
                char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Format
                    ? $"\\u{(int)character:x4}" : character.ToString()));
        }

        var authority = uri.GetLeftPart(UriPartial.Authority);
        return uri.PathAndQuery is "" or "/" ? authority : authority + uri.PathAndQuery;
    }
}
