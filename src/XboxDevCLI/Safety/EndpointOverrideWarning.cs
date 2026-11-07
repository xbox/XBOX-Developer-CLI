// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace XboxDevCLI.Safety;

/// <summary>
/// Detects when the PlayFab API endpoint (<c>PlayFab:BaseUrlTemplate</c>) or the XBOX services
/// endpoint (<c>XboxPartner:BaseUrl</c>) has been repointed away from the endpoint shipped with
/// the CLI, and builds the warning a command that sends a token there writes on standard error.
/// </summary>
/// <remarks>
/// Guards against tampering: the endpoint can be changed by an <c>XBOX_</c> environment variable
/// or a command option, so anyone able to set those can silently redirect every request — and
/// therefore the bearer token sent with it — to a host they control. The CLI cannot prevent the
/// override (it is a legitimate development affordance), so it makes the override loud instead:
/// the effective endpoint is compared against the compiled-in default and a warning naming the
/// target host is written to stderr when a command that calls the endpoint is prepared.
/// </remarks>
internal static class EndpointOverrideWarning
{
    /// <summary>Configuration key holding the PlayFab API endpoint template.</summary>
    internal const string ConfigurationKey = "PlayFab:BaseUrlTemplate";

    /// <summary>
    /// The PlayFab API endpoint in the built-in settings (<c>Resources/defaults.json</c>). It's
    /// also a constant, so the comparison doesn't depend on settings, and a test keeps the two
    /// the same.
    /// </summary>
    internal const string DefaultBaseUrlTemplate = "https://xboxdevcli.playfabapi.com";

    /// <summary>Configuration key holding the service base URL.</summary>
    internal const string XboxPartnerConfigurationKey = "XboxPartner:BaseUrl";

    /// <summary>
    /// The service URL in the built-in settings, a constant for the same reason as
    /// <see cref="DefaultBaseUrlTemplate"/>.
    /// </summary>
    /// <remarks>
    /// This is a tamper-detection baseline only; it is never a fallback.
    /// <c>XboxPartnerCommandSupport.ResolveBaseUrl</c> still fails when the configuration key is
    /// missing or blank, so the built-in settings remain the single source of the service URL.
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
            "         If you did not intend this, check the XBOX_PLAYFAB__BASEURLTEMPLATE environment variable.");
    }

    /// <summary>
    /// Builds the warning text for an overridden service endpoint, or returns <c>null</c>
    /// when the effective endpoint is the shipped default.
    /// </summary>
    internal static string? GetXboxPartnerWarning(string? baseUrl)
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
            $"Warning: the service endpoint is overridden to '{DescribeTarget(baseUrl)}' " +
            $"(configuration key '{XboxPartnerConfigurationKey}').",
            "         Requests and your access token will be sent to that host instead of the default endpoint.",
            "         If you did not intend this, check the XBOX_XBOXPARTNER__BASEURL environment variable " +
            "and the --base-url option.");
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
