// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.RegularExpressions;

namespace XboxDevCLI.EndToEndTests;

/// <summary>
/// The service deployment selected by the live job's environment variables.
/// </summary>
internal sealed record E2EProfile(
    string Environment,
    string? Scope,
    string Audience,
    string AllowedHostSuffix,
    PlayFabProfile PlayFab,
    XboxPartnerProfile XboxPartner);

internal sealed record PlayFabProfile(
    string BaseUrlTemplate,
    string? HostHeader,
    string TitleId)
{
    internal bool Enabled => false;
    internal string DisabledReason => "PlayFab does not yet authorize service principals.";
    internal TitleDataFixture TitleData { get; } = new("foo", "bar");
}

internal sealed record TitleDataFixture(string Key, string Value);

internal sealed record XboxPartnerProfile(
    string BaseUrl,
    string SellerId)
{
    internal bool Enabled => false;
    internal string DisabledReason => "XboxPartner does not yet authorize service principals.";
}

internal static partial class E2EProfiles
{
    internal const string AudienceVariable = "E2E_EXPECTED_AUDIENCE";
    internal const string HostSuffixVariable = "E2E_ALLOWED_HOST_SUFFIX";
    internal const string PlayFabBaseUrlVariable = "E2E_PLAYFAB_BASE_URL_TEMPLATE";
    internal const string TitleIdVariable = "E2E_PLAYFAB_TITLE_ID";
    internal const string HostHeaderVariable = "E2E_PLAYFAB_HOST_HEADER";
    internal const string XboxPartnerBaseUrlVariable = "E2E_XBOX_PARTNER_BASE_URL";
    internal const string SellerIdVariable = "E2E_SELLER_ID";

    internal const string PlayFabHostHeaderValue = "{titleId}.playfabapi.com";

    /// <summary>Loads and validates the environment's configuration before any service call.</summary>
    internal static E2EProfile Load(Func<string, string?> getVariable)
    {
        var errors = new List<string>();
        var header = getVariable(HostHeaderVariable);
        var scope = getVariable(LiveSettings.ScopesVariable);
        var profile = new E2EProfile(
            Required(LiveSettings.EnvironmentVariable),
            string.IsNullOrEmpty(scope) ? null : scope,
            Required(AudienceVariable),
            Required(HostSuffixVariable),
            new PlayFabProfile(
                Required(PlayFabBaseUrlVariable),
                string.IsNullOrEmpty(header) ? null : header,
                Required(TitleIdVariable)),
            new XboxPartnerProfile(Required(XboxPartnerBaseUrlVariable), Required(SellerIdVariable)));

        if (errors.Count == 0)
        {
            errors.AddRange(Validate(profile));
        }

        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                $"Live E2E configuration is not valid:{Environment.NewLine}  " +
                string.Join(Environment.NewLine + "  ", errors));
        }

        return profile;

        string Required(string name)
        {
            var value = getVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{name} must be set.");
                return string.Empty;
            }

            return value;
        }
    }

    internal static IReadOnlyList<string> Validate(E2EProfile profile)
    {
        var errors = new List<string>();

        if (profile.Scope is not null && GetScopeError(profile.Scope, profile.Audience) is { } scopeError)
        {
            errors.Add(scopeError);
        }

        var suffix = profile.AllowedHostSuffix;
        if (Uri.CheckHostName(suffix) != UriHostNameType.Dns
            || !suffix.Contains('.')
            || suffix.StartsWith('.')
            || suffix.EndsWith('.'))
        {
            errors.Add($"{HostSuffixVariable} must be one DNS domain suffix, without a leading or trailing dot.");
        }

        ValidatePlayFab(profile.PlayFab, suffix, errors);
        ValidateXboxPartner(profile.XboxPartner, suffix, errors);
        return errors;
    }

    internal static string? GetScopeError(string? scope, string audience) =>
        string.Equals(scope, audience + "/.default", StringComparison.Ordinal)
            ? null
            : $"Effective AzureAd:Scopes (from {LiveSettings.ScopesVariable} or the candidate's built-in " +
              $"defaults, {CliRunner.CandidateDefaultsFileName}) must equal {AudienceVariable} followed by '/.default'.";

    private static void ValidatePlayFab(PlayFabProfile playFab, string hostSuffix, List<string> errors)
    {
        if (!TitleIdPattern().IsMatch(playFab.TitleId))
        {
            errors.Add($"{TitleIdVariable} must be 1-32 letters or digits.");
        }

        // The CLI formats the template with string.Format(template, titleId), so {0} is
        // the only placeholder it understands. Anything else would reach the network as-is.
        var template = playFab.BaseUrlTemplate;
        if (template.Replace("{0}", string.Empty, StringComparison.Ordinal).IndexOfAny(['{', '}']) >= 0)
        {
            errors.Add($"{PlayFabBaseUrlVariable} may contain only the {{0}} title placeholder.");
        }
        else
        {
            ValidateGatewayUrl(
                PlayFabBaseUrlVariable,
                template.Replace("{0}", playFab.TitleId, StringComparison.Ordinal),
                hostSuffix,
                errors);
        }

        if (playFab.HostHeader is not null
            && !string.Equals(playFab.HostHeader, PlayFabHostHeaderValue, StringComparison.Ordinal))
        {
            errors.Add($"{HostHeaderVariable} must be omitted or equal '{PlayFabHostHeaderValue}'.");
        }
    }

    private static void ValidateXboxPartner(
        XboxPartnerProfile xboxPartner,
        string hostSuffix,
        List<string> errors)
    {
        ValidateGatewayUrl(XboxPartnerBaseUrlVariable, xboxPartner.BaseUrl, hostSuffix, errors);

        if (!SellerIdPattern().IsMatch(xboxPartner.SellerId))
        {
            errors.Add($"{SellerIdVariable} must be 1-20 digits.");
        }
    }

    private static void ValidateGatewayUrl(
        string field,
        string value,
        string hostSuffix,
        List<string> errors)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.AbsolutePath != "/"
            || value.EndsWith('/'))
        {
            errors.Add($"{field} must be an https origin with no port, path, credentials, query or fragment.");
            return;
        }

        var host = uri.IdnHost;
        if (!host.EndsWith("." + hostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{field} host '{host}' must be under {hostSuffix} for this environment.");
        }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9]{1,32}\z")]
    private static partial Regex TitleIdPattern();

    [GeneratedRegex(@"\A[0-9]{1,20}\z")]
    private static partial Regex SellerIdPattern();
}
