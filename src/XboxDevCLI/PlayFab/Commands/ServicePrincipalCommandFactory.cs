// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.PlayFab.Generated.Studio;
using XboxDevCLI.Services;
using static XboxDevCLI.PlayFab.Commands.PlayFabCommandHelper;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Creates the <c>playfab service-principal</c> command group, which manages the PlayFab access
/// that a studio grants to an Entra service principal:
/// <list type="bullet">
///   <item><c>list</c> — the service principals registered in a studio (<c>/Studio/ListServicePrincipals</c>).</item>
///   <item><c>get</c> — one service principal's complete grant set (<c>/Studio/GetServicePrincipalAccess</c>).</item>
///   <item><c>set</c> — replaces that grant set (<c>/Studio/SetServicePrincipalAccess</c>).</item>
///   <item><c>revoke</c> — removes the service principal from the studio (<c>/Studio/DeleteServicePrincipalAccess</c>).</item>
/// </list>
/// <para>
/// Only a signed-in person can manage grants; PlayFab rejects a service-principal caller. The
/// commands refuse a service-principal authentication mode up front rather than falling back to a
/// cached user sign-in, so an automation run can never act with a person's authority.
/// </para>
/// <para>
/// The studio is always named explicitly with <c>--studio-id</c>. It is not a context value: a
/// grant's scope must never be inferred from a saved seller or title. Studio APIs aren't scoped to
/// a title, so the commands take no title and send their requests to <c>PlayFab:StudioBaseUrl</c>.
/// </para>
/// </summary>
internal static class ServicePrincipalCommandFactory
{
    internal const string ListPath = "/Studio/ListServicePrincipals";
    internal const string GetPath = "/Studio/GetServicePrincipalAccess";
    internal const string SetPath = "/Studio/SetServicePrincipalAccess";
    internal const string DeletePath = "/Studio/DeleteServicePrincipalAccess";

    private const int MaxPageSize = 100;

    /// <summary>
    /// Grants files are matched case-insensitively, and an unrecognized field fails the parse: a
    /// misspelled <c>TitleGrants</c> would otherwise be dropped and, because <c>set</c> replaces
    /// the whole grant set, silently revoke every title grant.
    /// </summary>
    private static readonly JsonSerializerOptions GrantsParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static Command Create()
    {
        var command = new Command(
            "service-principal",
            "Manage the PlayFab access a studio grants to Entra service principals. Requires a signed-in user.");

        command.Subcommands.Add(CreateListCommand());
        command.Subcommands.Add(CreateGetCommand());
        command.Subcommands.Add(CreateSetCommand());
        command.Subcommands.Add(CreateRevokeCommand());

        return command;
    }

    #region Shared options

    private static Option<string> StudioIdOption() =>
        RequiredOption("--studio-id", "Id of the studio that owns the grants. Always required; never taken from saved context.");

    private static Option<string> TenantIdOption() =>
        RequiredOption("--principal-tenant-id", "Entra tenant id (GUID) of the service principal.");

    private static Option<string> ObjectIdOption() =>
        RequiredOption("--principal-object-id", "Entra object id (GUID) of the service principal (its enterprise application object, not the application id).");

    #endregion

    #region list

    private static Command CreateListCommand()
    {
        var studioIdOption = StudioIdOption();
        var limitOption = new Option<int?>("--limit")
        {
            Description = $"Maximum number of service principals to return (1-{MaxPageSize}; PlayFab defaults to 50). Use --continuation-token for further pages.",
        };
        var continuationTokenOption = OptionalOption(
            "--continuation-token", "Opaque token from a previous page's response to retrieve the next page.");

        var command = new Command("list", "List the service principals registered in a studio.")
        {
            studioIdOption,
            limitOption,
            continuationTokenOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithUserClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunListAsync(
                    client,
                    parseResult.GetValue(studioIdOption)!,
                    parseResult.GetValue(limitOption),
                    parseResult.GetValue(continuationTokenOption),
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [ListPath],
            Scope: "Reads the service principals registered in one studio.",
            Output: "A page of service principals plus a continuation token for the next page.",
            Table: new TableView(
                nameof(ListServicePrincipalsResult.ServicePrincipals),
                [
                    new TableColumn("DISPLAY NAME", nameof(ServicePrincipalSummary.DisplayName), 30),
                    new TableColumn("OBJECT ID", nameof(ServicePrincipalSummary.ObjectId), 38),
                    new TableColumn("CLIENT ID", nameof(ServicePrincipalSummary.ClientId), 38),
                    new TableColumn("TENANT ID", nameof(ServicePrincipalSummary.TenantId), 38),
                ]),
            Paging: new CommandPaging(
                nameof(ListServicePrincipalsRequest.PageSize),
                nameof(ListServicePrincipalsResult.ContinuationToken),
                PageSizeOption: limitOption.Name,
                TokenOption: continuationTokenOption.Name),
            Examples: [
                "xbox playfab service-principal list --studio-id 1234567890ABCDEF",
            ]));
    }

    /// <summary>Action for <c>service-principal list</c>.</summary>
    internal static async Task<int> RunListAsync(
        PlayFabApiClient client, string studioId, int? limit, string? continuationToken,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaxPageSize)
        {
            stderr.WriteLine($"Error: --limit must be between 1 and {MaxPageSize}.");
            return 1;
        }

        var request = new ListServicePrincipalsRequest
        {
            StudioId = studioId.Trim(),
            PageSize = limit,
            ContinuationToken = string.IsNullOrWhiteSpace(continuationToken) ? null : continuationToken.Trim(),
        };

        return await RunAsync(client, ListPath, request, output, stdout, stderr, cancellationToken);
    }

    #endregion

    #region get

    private static Command CreateGetCommand()
    {
        var studioIdOption = StudioIdOption();
        var tenantIdOption = TenantIdOption();
        var objectIdOption = ObjectIdOption();

        var command = new Command("get", "Show a service principal's complete grant set in a studio.")
        {
            studioIdOption,
            tenantIdOption,
            objectIdOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithUserClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunGetAsync(
                    client,
                    parseResult.GetValue(studioIdOption)!,
                    parseResult.GetValue(tenantIdOption)!,
                    parseResult.GetValue(objectIdOption)!,
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [GetPath],
            Scope: "Reads one service principal's studio and title grants.",
            Output: "The service principal's registration, studio permissions, title grants, and grant version.",
            Examples: [
                "xbox playfab service-principal get --studio-id 1234567890ABCDEF --principal-tenant-id 22222222-3333-4444-5555-666666666666 --principal-object-id 33333333-4444-5555-6666-777777777777",
            ]));
    }

    /// <summary>Action for <c>service-principal get</c>.</summary>
    internal static Task<int> RunGetAsync(
        PlayFabApiClient client, string studioId, string tenantId, string objectId,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var request = new GetServicePrincipalAccessRequest
        {
            StudioId = studioId.Trim(),
            TenantId = tenantId.Trim(),
            ObjectId = objectId.Trim(),
        };

        return RunAsync(client, GetPath, request, output, stdout, stderr, cancellationToken);
    }

    #endregion

    #region set

    private static Command CreateSetCommand()
    {
        var studioIdOption = StudioIdOption();
        var tenantIdOption = TenantIdOption();
        var objectIdOption = ObjectIdOption();
        var displayNameOption = RequiredOption(
            "--display-name", "Display name for the service principal in this studio.");
        var grantsFileOption = RequiredOption(
            "--grants-file",
            "Path to a JSON file holding the complete grant set: " +
            "{\"StudioPermissions\":[...],\"TitleGrants\":[{\"TitleId\":\"...\",\"Permissions\":[...]}]}, " +
            "or the output of 'playfab service-principal get' with its grants edited. " +
            "Both properties are required; StudioPermissions may be empty, and each title needs at least one permission.");
        var expectedVersionOption = new Option<double?>("--expected-version")
        {
            Description =
                "Replace the grants only if they're still at this version, the Version that 'get' returned. " +
                "Use 0 to require that the service principal has no access yet. Without it, the grants are replaced " +
                "whatever they are now. Required when the grants file has a Version, and must equal it.",
        };

        var command = new Command(
            "set",
            "Register a service principal in a studio if needed and REPLACE its complete grant set. " +
            "Studio permissions and title grants not in the grants file are removed.")
        {
            studioIdOption,
            tenantIdOption,
            objectIdOption,
            displayNameOption,
            grantsFileOption,
            expectedVersionOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithUserClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunSetAsync(
                    client,
                    parseResult.GetValue(studioIdOption)!,
                    parseResult.GetValue(tenantIdOption)!,
                    parseResult.GetValue(objectIdOption)!,
                    parseResult.GetValue(displayNameOption)!,
                    parseResult.GetValue(grantsFileOption)!,
                    parseResult.GetValue(expectedVersionOption),
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [SetPath],
            Scope: "Replaces one service principal's studio and title grants.",
            Output: "The service principal's access after the replacement, including the new grant version.",
            SafetyDetails:
                "The grants file is the complete grant set, not a change to it: any studio permission " +
                "or title grant it omits is removed. Start from the current grants: save the output of " +
                "'playfab service-principal get', edit its StudioPermissions and TitleGrants, and pass its Version " +
                "as --expected-version, so the replacement fails instead of undoing a change someone made since. " +
                "Without --expected-version, the grants are replaced whatever they are now. " +
                "If set fails, the service principal might have no access in the studio until a set " +
                "succeeds, and the grants might or might not have changed. Run 'playfab service-principal get', " +
                "check its grants and Version, and if needed run set again with that Version. " +
                "Unrecognized fields in the grants file are rejected rather than ignored, and its " +
                "TenantId, ObjectId, and DisplayName, when present, must match the options.",
            Examples: [
                "xbox playfab service-principal set --studio-id 1234567890ABCDEF --principal-tenant-id 22222222-3333-4444-5555-666666666666 --principal-object-id 33333333-4444-5555-6666-777777777777 --display-name \"Nod Publishers build\" --grants-file grants.json --expected-version 3",
            ]));
    }

    /// <summary>
    /// Action for <c>service-principal set</c>: reads and validates the grants file, then replaces
    /// the service principal's grant set.
    /// </summary>
    internal static async Task<int> RunSetAsync(
        PlayFabApiClient client, string studioId, string tenantId, string objectId,
        string displayName, string grantsFile, double? expectedVersion,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (expectedVersion is { } requested && (!double.IsFinite(requested) || requested < 0))
        {
            stderr.WriteLine("Error: --expected-version must be 0 or a Version returned by 'playfab service-principal get'.");
            return 1;
        }

        var path = grantsFile.Trim();
        if (!File.Exists(path))
        {
            stderr.WriteLine($"Error: File not found: {path}");
            return 1;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            stderr.WriteLine($"Error: Could not read {path}: {ex.Message}");
            return 1;
        }

        if (!TryParseGrants(json, out var grants, out var error))
        {
            stderr.WriteLine(error);
            return 1;
        }

        var request = new SetServicePrincipalAccessRequest
        {
            StudioId = studioId.Trim(),
            TenantId = tenantId.Trim(),
            ObjectId = objectId.Trim(),
            DisplayName = displayName.Trim(),
            StudioPermissions = grants!.StudioPermissions,
            TitleGrants = grants.TitleGrants,
            ExpectedVersion = expectedVersion,
        };

        if (CheckGrantsFileAgainstRequest(grants, request) is { } mismatch)
        {
            stderr.WriteLine(mismatch);
            return 1;
        }

        return await RunAsync(client, SetPath, request, output, stdout, stderr, cancellationToken);
    }

    /// <summary>
    /// Checks the fields a grants file copied from <c>get</c> may carry against the request built
    /// from the options. The options always decide what's sent; the file's <c>Version</c> is never
    /// sent, because it records when the file was read, not what it now holds. A file field that
    /// disagrees with its option means the file belongs to another service principal, or was read
    /// at another version, so <c>set</c> refuses rather than choose one.
    /// </summary>
    /// <returns>An error message, or <c>null</c> when the file agrees with the request.</returns>
    internal static string? CheckGrantsFileAgainstRequest(GrantsFile grants, SetServicePrincipalAccessRequest request)
    {
        if (Differs(grants.TenantId, request.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return MismatchError("TenantId", grants.TenantId!, "--principal-tenant-id", request.TenantId!);
        }

        if (Differs(grants.ObjectId, request.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            return MismatchError("ObjectId", grants.ObjectId!, "--principal-object-id", request.ObjectId!);
        }

        if (Differs(grants.DisplayName, request.DisplayName, StringComparison.Ordinal))
        {
            return MismatchError("DisplayName", grants.DisplayName!, "--display-name", request.DisplayName!);
        }

        if (grants.Version is not { } fileVersion)
        {
            return null;
        }

        var file = fileVersion.ToString(CultureInfo.InvariantCulture);
        if (request.ExpectedVersion is not { } expected)
        {
            return $"Error: The grants file has Version {file}, but --expected-version isn't set. " +
                   $"Pass --expected-version {file} to replace the grants only if they're still at that version, " +
                   "or remove Version from the file to replace them whatever they are now.";
        }

        if (expected != fileVersion)
        {
            return $"Error: The grants file has Version {file}, but --expected-version is " +
                   $"{expected.ToString(CultureInfo.InvariantCulture)}. Set --expected-version to match, or remove " +
                   "Version from the file. If the grants have changed since you saved the file, run " +
                   "'playfab service-principal get' again and redo your edits on its output.";
        }

        return null;

        static bool Differs(string? fromFile, string? fromOption, StringComparison comparison) =>
            fromFile is not null && !string.Equals(fromFile.Trim(), fromOption, comparison);

        static string MismatchError(string field, string fromFile, string option, string fromOption) =>
            $"Error: The grants file's {field} '{TerminalText.SingleLine(fromFile.Trim())}' doesn't match " +
            $"{option} '{TerminalText.SingleLine(fromOption)}'. " +
            $"Correct the option, or remove {field} from the file.";
    }

    /// <summary>
    /// The shape of a <c>--grants-file</c>: the grants, plus the other fields of the
    /// <c>ServicePrincipal</c> object <c>get</c> returns, so its output can be edited and passed back.
    /// </summary>
    internal sealed class GrantsFile
    {
        public List<string>? StudioPermissions { get; set; }

        public List<ServicePrincipalTitleGrant>? TitleGrants { get; set; }

        public string? TenantId { get; set; }

        public string? ObjectId { get; set; }

        /// <summary>Accepted so <c>get</c>'s output parses; never checked or sent. PlayFab records it from the service principal's first token.</summary>
        public string? ClientId { get; set; }

        public string? DisplayName { get; set; }

        public double? Version { get; set; }

        /// <summary>Accepted so <c>get</c>'s output parses; never checked or sent.</summary>
        public JsonElement? LastUpdated { get; set; }
    }

    /// <summary>
    /// Parses a grants file. The file can be the grant set itself, or <c>get</c>'s output: its
    /// <c>{code, status, data}</c> envelope, its <c>data</c>, or the <c>ServicePrincipal</c> object
    /// inside it. Both <c>StudioPermissions</c> and <c>TitleGrants</c> must be present,
    /// so leaving one out can't silently remove every grant at that level; every title grant needs a
    /// <c>TitleId</c> and a non-empty <c>Permissions</c> array. PlayFab validates the permission names.
    /// </summary>
    internal static bool TryParseGrants(string? json, out GrantsFile? grants, out string? error)
    {
        grants = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The grants file is empty.";
            return false;
        }

        GrantsFile? parsed;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Invalid grants file: expected a JSON object with StudioPermissions and TitleGrants.";
                return false;
            }

            if (!TryUnwrap(ref root, "data", ["code", "status"], out error) ||
                !TryUnwrap(ref root, "ServicePrincipal", [], out error))
            {
                return false;
            }

            parsed = root.Deserialize<GrantsFile>(GrantsParseOptions);
        }
        catch (JsonException ex)
        {
            error = $"Invalid grants file: {ex.Message}";
            return false;
        }

        if (parsed?.StudioPermissions is null || parsed.TitleGrants is null)
        {
            error = "Invalid grants file: both StudioPermissions and TitleGrants are required. " +
                    "Use an empty array to grant nothing at that level.";
            return false;
        }

        if (parsed.StudioPermissions.Any(string.IsNullOrWhiteSpace))
        {
            error = "Invalid grants file: StudioPermissions contains an empty permission name.";
            return false;
        }

        foreach (var grant in parsed.TitleGrants)
        {
            if (grant is null || string.IsNullOrWhiteSpace(grant.TitleId))
            {
                error = "Invalid grants file: every TitleGrants entry needs a TitleId.";
                return false;
            }

            if (grant.Permissions is null || grant.Permissions.Count == 0 || grant.Permissions.Any(string.IsNullOrWhiteSpace))
            {
                error = $"Invalid grants file: the TitleGrants entry for '{grant.TitleId}' needs a non-empty Permissions array of permission names. Omit the title to remove its grant.";
                return false;
            }
        }

        grants = parsed;
        return true;
    }

    /// <summary>
    /// When <paramref name="element"/> has a property named <paramref name="wrapper"/>, replaces it
    /// with that property's object. Any other property must be one of <paramref name="siblings"/>,
    /// so a grants file can't mix the wrapper with grants beside it.
    /// </summary>
    private static bool TryUnwrap(ref JsonElement element, string wrapper, string[] siblings, out string? error)
    {
        error = null;
        JsonElement? inner = null;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, wrapper, StringComparison.OrdinalIgnoreCase))
            {
                inner = property.Value;
            }
        }

        if (inner is null)
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, wrapper, StringComparison.OrdinalIgnoreCase) &&
                !siblings.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                error = $"Invalid grants file: unexpected property '{property.Name}' next to '{wrapper}'.";
                return false;
            }
        }

        if (inner.Value.ValueKind != JsonValueKind.Object)
        {
            error = $"Invalid grants file: '{wrapper}' must be a JSON object.";
            return false;
        }

        element = inner.Value;
        return true;
    }

    #endregion

    #region revoke

    private static Command CreateRevokeCommand()
    {
        var studioIdOption = StudioIdOption();
        var tenantIdOption = TenantIdOption();
        var objectIdOption = ObjectIdOption();

        var command = new Command(
            "revoke",
            "Remove a service principal and all of its grants from a studio. Succeeds when it has no access.")
        {
            studioIdOption,
            tenantIdOption,
            objectIdOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithUserClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunRevokeAsync(
                    client,
                    parseResult.GetValue(studioIdOption)!,
                    parseResult.GetValue(tenantIdOption)!,
                    parseResult.GetValue(objectIdOption)!,
                    output, stdout, stderr,
                    cancellationToken,
                    parseResult.GetValue(Program.ConfirmOption))));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [DeletePath],
            Scope: "Removes one service principal's access to one studio.",
            SafetyDetails:
                "The service principal's next PlayFab request in this studio fails. Restoring access " +
                "needs 'playfab service-principal set' with the complete grant set.",
            Examples: [
                "xbox playfab service-principal revoke --studio-id 1234567890ABCDEF --principal-tenant-id 22222222-3333-4444-5555-666666666666 --principal-object-id 33333333-4444-5555-6666-777777777777 --confirm",
            ]));
    }

    /// <summary>
    /// Action for <c>service-principal revoke</c>. <paramref name="confirmed"/> is passed through
    /// to the client, whose destructive-operation guard refuses the call without it.
    /// </summary>
    internal static Task<int> RunRevokeAsync(
        PlayFabApiClient client, string studioId, string tenantId, string objectId,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken,
        bool confirmed)
    {
        var request = new DeleteServicePrincipalAccessRequest
        {
            StudioId = studioId.Trim(),
            TenantId = tenantId.Trim(),
            ObjectId = objectId.Trim(),
        };

        return RunAsync(client, DeletePath, request, output, stdout, stderr, cancellationToken, confirmedDestructive: confirmed);
    }

    #endregion

    #region Authentication

    /// <summary>
    /// Refuses a service-principal authentication mode before any client is built, then runs the
    /// action through <see cref="PlayFabCommandHelper.WithClientAsync"/>. PlayFab only lets a person
    /// manage grants, and the CLI must not quietly use a cached user sign-in instead.
    /// </summary>
    private static Task<int> WithUserClientAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken,
        Func<PlayFabApiClient, OutputOptions, TextWriter, TextWriter, CancellationToken, Task<int>> run)
    {
        var invocation = Program.GetInvocation(parseResult);
        if (invocation.IsAutomationSelected)
        {
            Console.Error.WriteLine(GetAutomationRefusal(invocation.AuthenticationModeName));
            return Task.FromResult(1);
        }

        return WithClientAsync(parseResult, cancellationToken, run);
    }

    internal static string GetAutomationRefusal(string mode) =>
        $"Error: 'playfab service-principal' doesn't apply when {AutomationAuthenticationSettings.ModeVariable} is '{mode}'. " +
        "Only a signed-in user can manage service-principal grants. Unset " +
        $"{AutomationAuthenticationSettings.ModeVariable} and the other {AutomationAuthenticationSettings.Prefix}* " +
        "settings, run 'xbox auth login', and try again.";

    #endregion
}
