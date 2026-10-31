// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FsCheck;
using FsCheck.Fluent;

namespace XboxDevCLI.FuzzTest;

/// <summary>
/// Wrapper types and FsCheck generators that produce adversarial inputs for the
/// fuzz properties. Using dedicated wrapper types (rather than raw <see cref="string"/>)
/// lets each property opt in to a targeted generator via
/// <c>[Property(Arbitrary = new[] { typeof(FuzzArbitraries) })]</c> without disturbing
/// FsCheck's default generation for other parameters.
/// </summary>
internal static class FuzzArbitraries
{
    /// <summary>Tokens deliberately chosen to stress path normalization and the destructive gate.</summary>
    private static readonly string[] PathTokens =
    {
        "/", "\\", "//", "..", ".", "?", "#", "%2F", "%5C", "%44", "%00", " ", "\t",
        "Admin", "MultiplayerServer", "Get", "List", "Search", "Set", "Update",
        "Delete", "DeleteTitle", "Shutdown", "ShutdownMultiplayerServerSession",
        "\u0000", "\uFFFF", "e", "\u044belete", "query=1", "frag",
    };

    /// <summary>Tokens used to assemble almost-JSON and malformed-JSON fragments.</summary>
    private static readonly string[] JsonTokens =
    {
        "{", "}", "[", "]", ":", ",", "\"", "\"data\"", "\"code\"", "\"status\"",
        "null", "true", "false", "0", "-1", "1e999", "\\", "\\u", "\\uZZZZ",
        "\uD800", "value", " ", "\n", "\t", "\"a\":", "{\"data\":", "}}}", "[[[",
    };

    private static Gen<string> FromTokens(string[] tokens) =>
        Gen.Elements(tokens).ArrayOf().Select(string.Concat);

    /// <summary>Adversarial API paths (backslashes, percent-encoding, unicode, query/fragment).</summary>
    public static Arbitrary<AdversarialPath> Paths() =>
        Gen.OneOf(
                FromTokens(PathTokens),
                FromTokens(PathTokens).Select(s => "/" + s),
                FromTokens(PathTokens).Select(s => s + "?" + s),
                ArbMap.Default.GeneratorFor<string>().Where(s => s is not null))
            .Select(s => new AdversarialPath(s))
            .ToArbitrary();

    /// <summary>Arbitrary text including the empty string, whitespace, and (occasionally) null.</summary>
    public static Arbitrary<AdversarialText> Texts() =>
        ArbMap.Default.GeneratorFor<string>()
            .Select(s => new AdversarialText(s))
            .ToArbitrary();

    /// <summary>Fragments that are frequently invalid JSON but sometimes accidentally valid.</summary>
    public static Arbitrary<JsonFragment> JsonFragments() =>
        Gen.OneOf(
                FromTokens(JsonTokens),
                ArbMap.Default.GeneratorFor<string>().Where(s => s is not null))
            .Select(s => new JsonFragment(s))
            .ToArbitrary();

    /// <summary>Comma-separated-list-like strings, biased toward commas and whitespace.</summary>
    public static Arbitrary<CsvLike> CsvLikes() =>
        Gen.Elements(new[] { ",", " ", "\t", "a", "b", "value", "", ",,", " , " })
            .ArrayOf()
            .Select(parts => new CsvLike(string.Concat(parts)))
            .ToArbitrary();

    /// <summary>Tokens that resemble real CLI arguments the parser must survive.</summary>
    private static readonly string[] ArgTokens =
    {
        "auth", "login", "logout", "status", "feedback", "report-issue", "playfab", "api", "list",
        "describe", "type", "--output", "-o", "json", "json-compact", "--data-only",
        "--confirm", "--yes", "-y", "--verbose", "--playfab-title-id", "ABC123",
        "--body", "{}", "/Admin/GetTitleData", "--", "-", "--unknown", "=", "\"",
        "'", " ", "", "@response", "--client-id", "--config-env", "Development",
    };

    /// <summary>
    /// Arbitrary argv arrays assembled from CLI-like tokens plus random noise. The NUL
    /// character is stripped because a process can never receive a NUL byte inside an
    /// argv element from the OS, so it is outside the reachable input domain.
    /// </summary>
    public static Arbitrary<ArgList> ArgLists() =>
        Gen.OneOf(
                Gen.Elements(ArgTokens),
                ArbMap.Default.GeneratorFor<string>().Where(s => s is not null))
            .Select(token => token.Replace("\0", string.Empty))
            .ArrayOf()
            .Select(args => new ArgList(args))
            .ToArbitrary();
}

public readonly record struct AdversarialPath(string Value);

public readonly record struct AdversarialText(string? Value);

public readonly record struct JsonFragment(string Value);

public readonly record struct CsvLike(string Value);

public readonly record struct ArgList(string[] Value);
