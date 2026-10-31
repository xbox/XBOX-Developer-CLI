// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Shared helper for curated PlayFab commands: config → auth → client construction, the
/// serialize → invoke → format/error pipeline, common option factories, and option-value parsing.
/// The testable action core (<see cref="RunAsync{TRequest}"/>) takes an injected client and
/// output writers so command actions can be unit-tested with a fake transport.
/// </summary>
internal static class PlayFabCommandHelper
{
    /// <summary>
    /// Serialization options for request bodies: optional (null) fields are omitted so a
    /// minimal, valid PlayFab request is produced from only the options the user supplied.
    /// </summary>
    internal static readonly JsonSerializerOptions RequestOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Creates a configured <see cref="PlayFabApiClient"/> from the current parse result.
    /// Caller is responsible for disposing the returned client.
    /// </summary>
    public static PlayFabApiClient CreateClient(ParseResult parseResult)
    {
        var config = Program.BuildConfiguration(parseResult);

        // Honours the same test seam as Program.InvokeApiAsync. Commands reaching the API through
        // this helper — file list among them — never touch that method, so without a shared seam
        // their actions could not be driven end to end, and `file list` declaring a view its
        // action ignored is exactly the regression this feature shipped once already.
        if (Program.ApiClientFactory is { } factory)
        {
            return factory(config);
        }

        var authService = Program.GetAuthentication(parseResult);
        return new PlayFabApiClient(config, authService);
    }

    /// <summary>
    /// Sends a request through an already-constructed <see cref="PlayFabApiClient"/> and
    /// writes the formatted response to <paramref name="stdout"/>, or the formatted error
    /// to <paramref name="stderr"/>, in the shape described by <paramref name="output"/>.
    /// </summary>
    /// <remarks>
    /// Extracted from the command actions so the call/format/error branches can be
    /// unit-tested with a fake transport and captured output streams, mirroring
    /// <see cref="Program.InvokeApiAsync"/> without the parse-result and body-file plumbing.
    /// <para>
    /// <paramref name="output"/> carries the command's declared table view alongside the format,
    /// so a command cannot declare table support and then reach a formatter that ignores it — the
    /// caller has to supply the view to call this at all.
    /// </para>
    /// <paramref name="confirmedDestructive"/> is forwarded to
    /// <see cref="PlayFabApiClient.CallApiAsync(string, string?, CancellationToken, bool)"/>; it must be
    /// <c>true</c> for destructive operations (<c>Delete*</c>) and defaults to <c>false</c> so
    /// this path stays fail-closed for destructive endpoints.
    /// </remarks>
    /// <returns>0 on success; 1 on failure.</returns>
    public static async Task<int> InvokeAsync(
        PlayFabApiClient client,
        string apiPath,
        string jsonBody,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        bool confirmedDestructive = false)
    {
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            var response = await client.CallApiAsync(apiPath, jsonBody, cancellationToken, confirmedDestructive);

            if (output.TableView is null)
            {
                stdout.WriteLine(Program.FormatOutput(response, output.IsCompact, output.DataOnly));
                return 0;
            }

            stdout.WriteLine(Program.FormatTableOutput(response, output.TableView));
            if (Program.FormatNextPageNotice(response, output.Paging) is { } notice)
            {
                stderr.WriteLine(notice);
            }

            return 0;
        }
        catch (PlayFabApiException ex)
        {
            stderr.WriteLine(Program.FormatErrorOutput(ex, output.IsCompact));
            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Testable action core: serializes <paramref name="request"/> and routes it through
    /// <see cref="InvokeAsync"/> using an injected <paramref name="client"/> and the supplied
    /// output writers, so command actions can be unit-tested with a fake transport.
    /// </summary>
    /// <returns>0 on success; 1 on failure.</returns>
    internal static async Task<int> RunAsync<TRequest>(
        PlayFabApiClient client,
        string apiPath,
        TRequest request,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        bool confirmedDestructive = false)
        where TRequest : class
    {
        var jsonBody = JsonSerializer.Serialize(request, RequestOptions);
        return await InvokeAsync(
            client, apiPath, jsonBody, output, stdout, stderr, cancellationToken, confirmedDestructive);
    }

    /// <summary>
    /// SetAction-side boilerplate: resolves the global <c>--output</c>/<c>--data-only</c> options,
    /// constructs the shared <see cref="PlayFabApiClient"/>, and invokes <paramref name="run"/>
    /// with the client, resolved output options, and the process-global <see cref="Console"/>
    /// streams. Client-construction/transport exceptions are surfaced to stderr as exit code 1.
    /// The per-command logic lives in <paramref name="run"/> (a <c>Run*Async</c> method) so it can
    /// be unit-tested with an injected client and captured writers. <paramref name="cancellationToken"/>
    /// is handed to <paramref name="run"/> so Ctrl-C and SIGTERM reach the PlayFab request.
    /// </summary>
    internal static async Task<int> WithClientAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken,
        Func<PlayFabApiClient, OutputOptions, TextWriter, TextWriter, CancellationToken, Task<int>> run)
    {
        var output = OutputOptions.From(parseResult);

        try
        {
            using var client = CreateClient(parseResult);
            return await run(client, output, Console.Out, Console.Error, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    #region Option factories

    internal static Option<string> RequiredOption(string name, string description) =>
        new(name) { Description = description, Required = true };

    internal static Option<string?> OptionalOption(string name, string description) =>
        new(name) { Description = description };

    internal static Option<string> PlayFabIdRequired(
        string description = "Unique PlayFab assigned ID of the user to operate on.") =>
        RequiredOption("--playfab-id", description);

    #endregion

    #region Option-value parsing helpers

    /// <summary>
    /// Splits a comma-separated option value into a trimmed list, or returns <c>null</c> when the
    /// value is absent so the corresponding request field is omitted.
    /// </summary>
    internal static List<string>? ParseCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

    /// <summary>
    /// Parses an optional enum option value (case-insensitive). Returns <c>true</c> with a
    /// <c>null</c> result when the value is absent; returns <c>false</c> with an
    /// <paramref name="error"/> listing the allowed names when the value is invalid.
    /// </summary>
    internal static bool TryParseEnum<TEnum>(
        string optionName, string? value, out TEnum? result, out string? error)
        where TEnum : struct, Enum
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        // Match on enum names only (case-insensitive). Enum.TryParse would also accept numeric
        // forms like "0", which aren't documented and would confuse users.
        var trimmed = value.Trim();
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                result = Enum.Parse<TEnum>(name);
                return true;
            }
        }

        error = $"Invalid value '{value}' for option '{optionName}'. Allowed values: " +
                string.Join(", ", Enum.GetNames<TEnum>()) + ".";
        return false;
    }

    #endregion
}
