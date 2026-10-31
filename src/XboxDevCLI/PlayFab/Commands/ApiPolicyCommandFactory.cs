// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.CommandLine;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using XboxDevCLI.PlayFab.Generated.Admin;
using XboxDevCLI.Services;
using static XboxDevCLI.PlayFab.Commands.PlayFabCommandHelper;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Creates the <c>playfab api-policy</c> command group for reading and updating a title's
/// API access policy:
/// <list type="bullet">
///   <item><c>show</c> — reads the policy via <c>/Admin/GetPolicy</c>.</item>
///   <item><c>set</c> — replaces or appends the policy's statements via <c>/Admin/UpdatePolicy</c>.</item>
/// </list>
/// The policy statements for <c>set</c> are supplied as a JSON array (inline with
/// <c>--statements</c> or from a file with <c>--statements-file</c>) that is validated against the
/// generated <see cref="PermissionStatement"/> model before it is sent. <c>--policy-version</c>
/// threads the current version (from <c>show</c>) required by <c>UpdatePolicy</c>, and
/// <c>--overwrite</c> selects overwrite-vs-append semantics (append is the default).
/// <para>
/// <c>UpdatePolicy</c> is not caught by the <c>Delete*</c>/<c>Shutdown*</c> path guard in
/// <c>PlayFabApiClient</c>, so <c>--overwrite</c> — which discards every statement it does not
/// restate — carries its own <c>--confirm</c> requirement here. Its target is the title itself, so
/// <c>ContextResolver</c> also refuses a title it filled in from saved context or the environment;
/// see <c>DestructiveOperations</c>.
/// </para>
/// </summary>
internal static class ApiPolicyCommandFactory
{
    private const string DefaultPolicyName = "ApiPolicy";
    private const string GetPolicyPath = "/Admin/GetPolicy";
    private const string UpdatePolicyPath = "/Admin/UpdatePolicy";

    /// <summary>
    /// User-supplied statement JSON is matched case-insensitively so <c>"effect"</c> and
    /// <c>"Effect"</c> both bind; enum values (<see cref="EffectType"/>) round-trip as strings via
    /// the converter attribute on the generated type.
    /// <para>
    /// <see cref="JsonUnmappedMemberHandling.Disallow"/> is deliberate: with the default
    /// (<c>Skip</c>) a misspelled field is dropped without a word, which for a policy statement
    /// silently changes its meaning. A typo'd <c>ApiConditions</c>, for example, would drop the
    /// condition and leave an unconditional permission behind — broadening production API access
    /// while the command still reports success. Failing the parse instead keeps a typo from
    /// becoming a security change.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions StatementParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Wire-format field names of <see cref="PermissionStatement"/>, used to spell out the valid
    /// options when a statement carries an unknown field.
    /// </summary>
    private static readonly string[] StatementFieldNames =
        typeof(PermissionStatement)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property =>
                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    public static Command Create()
    {
        var apiPolicy = new Command(
            "api-policy",
            "Manage the title's API access policy");
        apiPolicy.Subcommands.Add(CreateShowCommand());
        apiPolicy.Subcommands.Add(CreateSetCommand());
        return apiPolicy;
    }

    private static Option<string?> PolicyNameOption() =>
        OptionalOption(
            "--policy-name",
            "Name of the policy to operate on. Defaults to 'ApiPolicy' when omitted.");

    #region show

    private static Command CreateShowCommand()
    {
        var policyNameOption = PolicyNameOption();

        var command = new Command("get", "Get a title's API access policy (POST /Admin/GetPolicy).")
        {
            policyNameOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunShowAsync(
                    client,
                    parseResult.GetValue(policyNameOption),
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(ApiPaths: [GetPolicyPath]));
    }

    /// <summary>
    /// Action for <c>playfab api-policy get</c>: reads the named policy (default <c>ApiPolicy</c>)
    /// via <c>/Admin/GetPolicy</c> and writes the formatted response.
    /// </summary>
    internal static async Task<int> RunShowAsync(
        PlayFabApiClient client, string? policyName,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var request = new GetPolicyRequest
        {
            PolicyName = ResolvePolicyName(policyName),
        };

        return await RunAsync(client, GetPolicyPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion

    #region set

    private static Command CreateSetCommand()
    {
        var policyNameOption = PolicyNameOption();
        var statementsOption = OptionalOption(
            "--statements",
            "Inline JSON array of permission statements. Mutually exclusive with --statements-file; exactly one is required.");
        var statementsFileOption = OptionalOption(
            "--statements-file",
            "Path to a file containing the JSON array of permission statements. Mutually exclusive with --statements; exactly one is required.");
        var policyVersionOption = new Option<double>("--policy-version")
        {
            Description = "Current policy version to update, as returned by 'playfab api-policy get'. UpdatePolicy fails unless this is the latest version.",
            Required = true,
        };
        var overwriteOption = new Option<bool>("--overwrite")
        {
            Description = "Overwrite the existing policy with these statements instead of appending them to it (default: append). Requires --confirm, since every statement not supplied here is discarded, and the title named with --playfab-title-id on the command line, not from XBOX_PLAYFAB_TITLE_ID or saved context.",
        };

        var command = new Command("set", "Update a title's API access policy (POST /Admin/UpdatePolicy).")
        {
            policyNameOption,
            statementsOption,
            statementsFileOption,
            policyVersionOption,
            overwriteOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunSetAsync(
                    client,
                    parseResult.GetValue(policyNameOption),
                    parseResult.GetValue(statementsOption),
                    parseResult.GetValue(statementsFileOption),
                    parseResult.GetValue(policyVersionOption),
                    parseResult.GetValue(overwriteOption),
                    output, stdout, stderr,
                    cancellationToken,
                    parseResult.GetValue(Program.ConfirmOption))));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [UpdatePolicyPath],
            SafetyDetails:
                "UpdatePolicy is not a Delete operation, so the runtime gate does not cover it; " +
                "--overwrite discards every statement it does not restate and requires --confirm " +
                "on its own. It also refuses a title from XBOX_PLAYFAB_TITLE_ID or saved context: " +
                "name the title with --playfab-title-id on the command line."));
    }

    /// <summary>
    /// Action for <c>title api-policy set</c>. Takes the policy statements from exactly one of
    /// <c>--statements</c> (inline) or <c>--statements-file</c> (a file path), validates them
    /// against the <see cref="PermissionStatement"/> model, and updates the policy via
    /// <c>/Admin/UpdatePolicy</c> using <paramref name="policyVersion"/> and
    /// <paramref name="overwrite"/>. Returns 1 (with an error on stderr) on any invalid input, or
    /// when <paramref name="overwrite"/> is requested without <paramref name="confirmed"/>.
    /// </summary>
    internal static async Task<int> RunSetAsync(
        PlayFabApiClient client, string? policyName, string? statements, string? statementsFile,
        double policyVersion, bool overwrite,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken,
        bool confirmed = false)
    {
        var hasInline = !string.IsNullOrWhiteSpace(statements);
        var hasFile = !string.IsNullOrWhiteSpace(statementsFile);

        if (hasInline && hasFile)
        {
            stderr.WriteLine("Use --statements or --statements-file, not both.");
            return 1;
        }

        if (!hasInline && !hasFile)
        {
            stderr.WriteLine("Provide the policy statements with --statements or --statements-file.");
            return 1;
        }

        if (overwrite && !confirmed)
        {
            stderr.WriteLine(
                "--overwrite replaces the title's entire API access policy with the supplied " +
                "statements, discarding every statement not listed here. The previous policy is " +
                "not recoverable through this CLI. Re-run with --confirm to proceed, or drop " +
                "--overwrite to append instead.");
            return 1;
        }

        string json;
        if (hasFile)
        {
            var path = statementsFile!.Trim();
            if (!File.Exists(path))
            {
                stderr.WriteLine($"Error: File not found: {path}");
                return 1;
            }

            try
            {
                json = await File.ReadAllTextAsync(path, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                stderr.WriteLine($"Error: Could not read {path}: {ex.Message}");
                return 1;
            }
        }
        else
        {
            json = statements!;
        }

        if (!TryParseStatements(json, out var parsedStatements, out var parseError))
        {
            stderr.WriteLine(parseError);
            return 1;
        }

        var request = new UpdatePolicyRequest
        {
            PolicyName = ResolvePolicyName(policyName),
            PolicyVersion = policyVersion,
            OverwritePolicy = overwrite,
            Statements = parsedStatements,
        };

        return await RunAsync(client, UpdatePolicyPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Normalizes the <c>--policy-name</c> value: blank/absent falls back to <c>ApiPolicy</c>
    /// (the policy titles use in practice); otherwise the trimmed value is forwarded as supplied,
    /// so the CLI does not have to be updated if PlayFab adds further named policies.
    /// </summary>
    internal static string ResolvePolicyName(string? policyName) =>
        string.IsNullOrWhiteSpace(policyName) ? DefaultPolicyName : policyName.Trim();

    /// <summary>
    /// Parses the policy-statements JSON into a list of <see cref="PermissionStatement"/>. The input
    /// must be a non-empty JSON array of statement objects. Returns <c>false</c> with a friendly
    /// <paramref name="error"/> (never leaking the internal type name) for empty input, invalid
    /// JSON, a non-array root, or an empty array.
    /// </summary>
    internal static bool TryParseStatements(
        string? json, out List<PermissionStatement>? statements, out string? error)
    {
        statements = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The policy statements JSON is empty.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON for policy statements: {ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                error = "Invalid policy statements: expected a JSON array of statement objects, e.g. " +
                        "'[{\"Resource\":\"pfrn:api--*\",\"Action\":\"*\",\"Effect\":\"Allow\",\"Principal\":\"*\"}]'.";
                return false;
            }

            List<PermissionStatement>? parsed;
            try
            {
                parsed = root.Deserialize<List<PermissionStatement>>(StatementParseOptions);
            }
            catch (JsonException ex)
            {
                error = DescribeStatementJsonError(ex);
                return false;
            }

            if (parsed is null || parsed.Count == 0)
            {
                error = "Provide at least one policy statement.";
                return false;
            }

            statements = parsed;
            return true;
        }
    }

    /// <summary>
    /// Turns a deserialization failure into a message that is useful without exposing internals.
    /// An unmapped-member failure (see <see cref="StatementParseOptions"/>) reports the offending
    /// field and the valid ones, because the likely cause is a typo the caller can fix; the raw
    /// exception text names the generated .NET type, which means nothing to a CLI user.
    /// </summary>
    private static string DescribeStatementJsonError(JsonException exception)
    {
        var message = exception.Message ?? string.Empty;

        if (message.Contains("could not be mapped", StringComparison.Ordinal))
        {
            var quoted = message.IndexOf('\'');
            var quotedEnd = quoted >= 0 ? message.IndexOf('\'', quoted + 1) : -1;
            var field = quotedEnd > quoted
                ? message[(quoted + 1)..quotedEnd]
                : null;

            var named = field is null ? "an unknown field" : $"unknown field '{field}'";
            return $"Invalid policy statements: {named}. Valid fields are " +
                   string.Join(", ", StatementFieldNames) +
                   ". Field names are matched case-insensitively; check for typos, because an " +
                   "unrecognized field would otherwise be dropped and could silently change what " +
                   "the statement permits.";
        }

        return $"Invalid policy statements: {message}";
    }

    #endregion
}
