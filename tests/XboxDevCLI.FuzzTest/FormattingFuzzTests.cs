// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using XboxDevCLI;
using XboxDevCLI.Services;

namespace XboxDevCLI.FuzzTest;

/// <summary>
/// Fuzz properties for the output-formatting and envelope-stripping code paths. These
/// parse and re-serialize untrusted API-response JSON, so they must never throw on
/// arbitrary (frequently malformed) input and must honor their documented invariants.
/// </summary>
public class FormattingFuzzTests
{
    [FuzzProperty]
    public bool FormatJson_NeverThrows(JsonFragment json)
    {
        // Any non-null string in, a string out — invalid JSON is echoed back unchanged.
        _ = PlayFabApiClient.FormatJson(json.Value, compact: false);
        _ = PlayFabApiClient.FormatJson(json.Value, compact: true);
        return true;
    }

    [FuzzProperty]
    public bool FormatJson_IsIdempotent(JsonFragment json)
    {
        // Formatting already-formatted output must be a fixed point.
        var once = PlayFabApiClient.FormatJson(json.Value, compact: true);
        var twice = PlayFabApiClient.FormatJson(once, compact: true);
        return once == twice;
    }

    [FuzzProperty]
    public bool FormatOutput_NeverThrows(JsonFragment json)
    {
        // FormatOutput must be total across every output-mode combination: it parses
        // untrusted response JSON, so no input may cause it to throw.
        foreach (var compact in new[] { false, true })
        {
            foreach (var dataOnly in new[] { false, true })
            {
                _ = Program.FormatOutput(json.Value, compact, dataOnly);
            }
        }

        return true;
    }

    [FuzzProperty]
    public bool FormatOutput_DataOnly_StripsEnvelope(JsonFragment json)
    {
        // Build a well-formed envelope { "data": <inner> } from a valid inner JSON value.
        // (Strip surrogate code units first so the value is transcodable to UTF-8.)
        var inner = JsonSerializer.Serialize(RemoveSurrogates(json.Value));
        var envelope = $$"""{"data":{{inner}}}""";
        var compact = json.Value.Length % 2 == 0;

        // The behavior unique to FormatOutput: with --data-only it returns just the
        // "data" payload, formatted exactly as FormatJson would format that payload.
        var actual = Program.FormatOutput(envelope, compact, dataOnly: true);
        var expected = PlayFabApiClient.FormatJson(inner, compact);

        return actual == expected;
    }

    /// <summary>Drop UTF-16 surrogate code units so the string can be JSON-serialized.</summary>
    private static string RemoveSurrogates(string value) =>
        new(value.Where(c => !char.IsSurrogate(c)).ToArray());

    [FuzzProperty]
    public bool FormatErrorOutput_NeverThrows(JsonFragment body)
    {
        var ex = new PlayFabApiException(400, "BadRequest", body.Value);

        // Arbitrary error bodies (valid or malformed JSON) must format without throwing.
        _ = Program.FormatErrorOutput(ex, compact: false);
        _ = Program.FormatErrorOutput(ex, compact: true);
        return true;
    }
}
