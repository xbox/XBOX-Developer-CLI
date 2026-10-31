// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI;
using XboxDevCLI.PlayFab.Commands;

namespace XboxDevCLI.FuzzTest;

/// <summary>
/// Fuzz properties for command-line argument parsing and option-value parsing helpers.
/// Parsing untrusted argv must never throw an unhandled exception — parse errors must
/// surface through <see cref="ParseResult"/>, not as a crash.
/// </summary>
public class ParsingFuzzTests
{
    [FuzzProperty]
    public bool BuildRootCommand_Parse_NeverThrows(ArgList args)
    {
        // System.CommandLine must classify any argv as a valid parse or a parse error;
        // it must never throw for the caller.
        var root = Program.BuildRootCommand();
        var result = root.Parse(args.Value, Program.NoResponseFileConfig);
        return result is not null;
    }

    [FuzzProperty]
    public bool ParseCsv_NeverThrows_AndOmitsEmptyEntries(CsvLike csv)
    {
        var parsed = PlayFabCommandHelper.ParseCsv(csv.Value);

        // Null/whitespace collapses to null; otherwise every produced entry is a
        // non-empty, trimmed token (RemoveEmptyEntries + TrimEntries contract).
        if (parsed is null)
        {
            return string.IsNullOrWhiteSpace(csv.Value);
        }

        return parsed.All(e => e.Length > 0 && e.Trim() == e);
    }

    [FuzzProperty]
    public bool TryParseEnum_NeverThrows_AndRoundTrips(AdversarialText text)
    {
        var ok = PlayFabCommandHelper.TryParseEnum<SampleEnum>(
            "--sample", text.Value, out var result, out var error);

        // On success either the value was absent (null result) or it maps to a real
        // enum name; on failure an error message is produced and no value is returned.
        if (ok)
        {
            return result is null || Enum.IsDefined(result.Value);
        }

        return result is null && !string.IsNullOrEmpty(error);
    }

    private enum SampleEnum
    {
        Alpha,
        Beta,
        Gamma,
    }
}
