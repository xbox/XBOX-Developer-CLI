// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI;

namespace XboxDevCLI.FuzzTest;

/// <summary>
/// Fuzz properties for configuration layering (<see cref="Program.LoadConfiguration"/>).
/// CLI option overrides are the highest-priority layer; feeding adversarial values
/// through the parser into the configuration must never throw, and accepted override
/// values must round-trip into the resolved configuration.
/// </summary>
public class ConfigurationFuzzTests
{
    /// <summary>
    /// Reduce a fuzzed value to something a real process could receive as an argv element:
    /// a non-null string with no embedded NUL byte (the OS cannot deliver a NUL inside argv).
    /// </summary>
    private static string ArgvSafe(string? value) =>
        (value ?? string.Empty).Replace("\0", string.Empty);

    [FuzzProperty]
    public bool LoadConfiguration_NeverThrows(AdversarialText text)
    {
        var value = ArgvSafe(text.Value);
        var root = Program.BuildRootCommand();
        var parse = root.Parse(new[]
        {
            "--client-id", value,
            "--tenant-id", value,
            "--scopes", value,
            "auth", "status",
        }, Program.NoResponseFileConfig);

        if (parse.Errors.Count > 0)
        {
            // The parser rejected the value before configuration is ever built; that is
            // a parse concern covered elsewhere, not a LoadConfiguration failure.
            return true;
        }

        var config = Program.LoadConfiguration(parse);
        return config is not null;
    }

    [FuzzProperty]
    public bool TitleIdOverride_RoundTrips(AdversarialText text)
    {
        var value = ArgvSafe(text.Value);
        var root = Program.BuildRootCommand();
        var parse = root.Parse(
            new[] { "--playfab-title-id", value, "auth", "status" },
            Program.NoResponseFileConfig);

        if (parse.Errors.Count > 0)
        {
            return true;
        }

        var config = Program.LoadConfiguration(parse);

        // CLI overrides are applied last, so the accepted value, trimmed, must win over
        // every other layer.
        return config["PlayFab:TitleId"] == value.Trim();
    }
}
