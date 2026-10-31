// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// <c>xbox context get|set|unset</c>: the values saved for options marked as context values, such
/// as the seller and PlayFab title, which <see cref="ContextResolver"/> gives a command that wasn't
/// given them. <c>context key list</c> lists the keys, the names of those options, and
/// <c>context profile list|delete</c> the profiles. These commands manage saved values only; they
/// don't know about flags or variables.
/// </summary>
/// <remarks>
/// Context is local state, so these commands make no network request: the signed-in account is
/// read from the token cache, and a value is checked only with its options' own validation.
/// </remarks>
internal static class ContextCommandFactory
{
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The table every context command renders: one row per key.</summary>
    public static TableView TableView { get; } = new(
        "values",
        [
            new TableColumn("KEY", "key", 24),
            new TableColumn("VALUE", "value", 40),
        ]);

    /// <summary>The table <c>context key list</c> renders: one row per key.</summary>
    public static TableView KeyTableView { get; } = new(
        "keys",
        [
            new TableColumn("KEY", "name", 24),
            new TableColumn("USED BY", "usedBy", 60),
        ]);

    /// <summary>The table <c>context profile list</c> and <c>delete</c> render: one row per profile.</summary>
    public static TableView ProfileTableView { get; } = new(
        "profiles",
        [
            new TableColumn("NAME", "name", 64),
        ]);

    public static Command Create() =>
        Create(Program.GetAuthentication, () => new ContextStore());

    internal static Command Create(
        Func<ParseResult, AuthenticationService> authFactory,
        Func<ContextStore> storeFactory)
    {
        var command = new Command(
            "context",
            "Save default values, such as the seller and PlayFab title, for later commands");
        command.Subcommands.Add(CreateGetCommand(authFactory, storeFactory));
        command.Subcommands.Add(CreateSetCommand(authFactory, storeFactory));
        command.Subcommands.Add(CreateUnsetCommand(authFactory, storeFactory));
        command.Subcommands.Add(CreateKeyCommand());
        command.Subcommands.Add(CreateProfileCommand(authFactory, storeFactory));
        return command;
    }

    /// <summary>
    /// <c>context profile list|delete</c>: the named sets of saved context. A profile is created by
    /// the first value saved to it, and selected for any command with <c>--profile</c>.
    /// </summary>
    private static Command CreateProfileCommand(
        Func<ParseResult, AuthenticationService> authFactory,
        Func<ContextStore> storeFactory)
    {
        var list = new Command("list", "List the profiles saved for the signed-in account");
        list.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            SafetyDetails: "Makes no network request.",
            Scope: "The profiles saved for the signed-in account.",
            Output:
                "JSON by default: a profiles array with each profile's name, starting with "
                + "'default'. Table output has a NAME column.",
            Examples:
            [
                "xbox context profile list",
                "xbox context profile list --output table",
            ],
            LearnMore:
            [
                "A profile is created by the first value saved to it, for example "
                + "'xbox --profile test context set playfab-title-id ABC12'.",
            ],
            Table: ProfileTableView));
        list.SetAction((parseResult, cancellationToken) => RunAsync(async () =>
        {
            RefuseInAutomation(parseResult);
            var identity = await GetRequiredIdentityAsync(
                authFactory(parseResult), cancellationToken);
            var names = await storeFactory().ListProfilesAsync(identity, cancellationToken);
            Write(new { profiles = names.Select(name => new { name }) }, parseResult);
            return 0;
        }));

        var nameArgument = new Argument<string>("name")
        {
            Description = "Profile to delete.",
        };
        var delete = new Command("delete", "Delete a profile and every value saved in it");
        delete.Arguments.Add(nameArgument);
        delete.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.LocalState,
            SafetyDetails: "Makes no network request.",
            Scope: "The profile's values saved for the signed-in account, on every endpoint.",
            Output:
                "JSON by default: a profiles array with the deleted profile. Table output has a "
                + "NAME column.",
            Examples:
            [
                "xbox context profile delete test",
            ],
            LearnMore:
            [
                "The default profile can't be deleted; "
                + "'xbox --profile default context unset --all' clears it.",
            ],
            Table: ProfileTableView));
        delete.SetAction((parseResult, cancellationToken) => RunAsync(async () =>
        {
            RefuseInAutomation(parseResult);
            var name = parseResult.GetValue(nameArgument)!;
            if (ContextProfiles.Validate(name) is { } problem)
            {
                Console.Error.WriteLine($"The profile name {problem}.");
                return 1;
            }

            var profile = ContextProfiles.Normalize(name)!;
            if (profile == ContextProfiles.Default)
            {
                Console.Error.WriteLine(
                    "The default profile can't be deleted. Run "
                    + $"'{ContextProfiles.ContextCommand(ContextProfiles.Default)} unset --all' to "
                    + "clear it.");
                return 1;
            }

            var identity = await GetRequiredIdentityAsync(
                authFactory(parseResult), cancellationToken);
            if (!await storeFactory().RemoveAllAsync(identity, profile, cancellationToken))
            {
                Console.Error.WriteLine(ContextProfiles.NotFoundMessage(profile));
                return 1;
            }

            Write(new { profiles = new[] { new { name = profile } } }, parseResult);
            return 0;
        }));

        var group = new Command("profile", "List and delete named sets of saved context");
        group.Subcommands.Add(list);
        group.Subcommands.Add(delete);
        return group.WithMetadata(new CommandMetadata(
            Scope: "Named sets of saved context. Pick one for any command with --profile <name>."));
    }

    /// <summary>
    /// <c>context key list</c>: the keys the other context commands accept, the names of the
    /// options marked as context values, with the command groups that take each. It reads the
    /// command tree, so it needs no sign-in and no saved context.
    /// </summary>
    private static Command CreateKeyCommand()
    {
        var list = new Command("list", "List the supported context keys");
        list.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            SafetyDetails: "Makes no network request and needs no sign-in.",
            Output:
                "JSON by default: a keys array with each key's name and the command groups that use "
                + "it. Table output has KEY and USED BY columns.",
            Examples:
            [
                "xbox context key list",
                "xbox context key list --output table",
            ],
            LearnMore:
            [
                "Each key is the name of an option commands take as a context value, and only these "
                + "can be saved with 'context set'.",
            ],
            Table: KeyTableView));
        list.SetAction(parseResult =>
        {
            var root = parseResult.RootCommandResult.Command;
            var keys = GetKeys(parseResult).Select(key => new
            {
                Name = key.Key,
                UsedBy = string.Join(
                    ", ",
                    key.Select(use => GetGroup(root, use)).Distinct().Order(StringComparer.Ordinal)),
            });
            Write(new { keys }, parseResult);
            return 0;
        });

        var group = new Command("key", "List the supported context keys");
        group.Subcommands.Add(list);
        return group.WithMetadata(new CommandMetadata(
            Scope: "The keys 'context get', 'context set', and 'context unset' accept."));
    }

    private static Command CreateGetCommand(
        Func<ParseResult, AuthenticationService> authFactory,
        Func<ContextStore> storeFactory)
    {
        var baseUrlOption = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("get", "Show the saved context values");
        command.Options.Add(baseUrlOption);
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope:
                "The context saved for the signed-in account, in the profile --profile or "
                + "XBOX_PROFILE selects.",
            Output:
                "JSON by default: the signed-in account (null when signed out), the profile, and a "
                + "values array of key and value (null when nothing is saved). Table output has "
                + "KEY and VALUE columns.",
            Examples:
            [
                "xbox context get",
                "xbox context get --output table",
                "xbox --profile test context get",
            ],
            LearnMore:
            [
                "Shows saved values only. A command uses its own flag, then the option's XBOX_ "
                + "variable, before a saved value; run it with --verbose to see which it used.",
                "Makes no network request. Signed out, it still succeeds and shows no values.",
            ],
            Table: TableView));

        command.SetAction((parseResult, cancellationToken) => RunAsync(async () =>
        {
            RefuseInAutomation(parseResult);

            // The profile is what 'get' reports on, so a bad XBOX_PROFILE fails first, alone.
            var profile = ContextProfiles.From(parseResult);
            var configuration = Program.BuildConfiguration(parseResult);
            var identity = await TryGetIdentityAsync(authFactory(parseResult), cancellationToken);
            var store = storeFactory();
            var endpoints = CreateEndpoints(parseResult, baseUrlOption, configuration);
            var rows = new List<ContextRow>();
            var problems = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in GetKeys(parseResult))
            {
                string? value = null;
                try
                {
                    value = identity is null
                        ? null
                        : await store.GetAsync(identity, key.Key, endpoints(key), profile, cancellationToken);
                }
                catch (Exception exception) when (exception is InvalidDataException
                                                      or IOException
                                                      or UnauthorizedAccessException)
                {
                    // 'get' is where an unreadable context file gets diagnosed, so report it and
                    // show the rest. A bad file fails every key the same way; say it once.
                    if (problems.Add(exception.Message))
                    {
                        Console.Error.WriteLine(exception.Message);
                    }
                }

                rows.Add(new ContextRow(key.Key, value));
            }

            if (identity is null)
            {
                Console.Error.WriteLine(
                    "Not signed in, so saved context isn't shown. Run "
                    + $"'{CliIdentity.CommandName} auth login' to see it.");
            }

            WriteRows(rows, profile, parseResult, new AccountOutput(identity));
            return 0;
        }));
        return command;
    }

    private static Command CreateSetCommand(
        Func<ParseResult, AuthenticationService> authFactory,
        Func<ContextStore> storeFactory)
    {
        var keyArgument = new Argument<string>("key")
        {
            Description = "Key to save. 'xbox context key list' lists them.",
        };
        var valueArgument = new Argument<string>("value")
        {
            Description = "Value to save.",
        };
        var baseUrlOption = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command(
            "set",
            "Save a value that later commands use when no flag names it");
        command.Arguments.Add(keyArgument);
        command.Arguments.Add(valueArgument);
        command.Options.Add(baseUrlOption);
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.LocalState,
            SafetyDetails: "Makes no network request; the value is checked only with its options' validation.",
            Scope:
                "Saved for the signed-in account, in the profile --profile or XBOX_PROFILE selects, "
                + "and for the host and port of the endpoint the value belongs to.",
            Output:
                "JSON by default: the profile and a values array with the saved key and value. "
                + "Table output has KEY and VALUE columns.",
            Examples:
            [
                "xbox context set seller-id 1234567",
                "xbox context set playfab-title-id ABC12",
                "xbox --profile test context set playfab-title-id DEF34",
            ],
            LearnMore:
            [
                "Keys are the names of the options commands take as context values; 'xbox context "
                + "key list' lists them. The value has to pass those options' own validation.",
                "Like 'git config set', the key and value are positional.",
                "Saving to a named profile creates it.",
                "A command's own flag, and the option's XBOX_ variable, still win over the saved value.",
            ],
            Table: TableView));

        command.SetAction((parseResult, cancellationToken) => RunAsync(async () =>
        {
            RefuseInAutomation(parseResult);
            var profile = ContextProfiles.From(parseResult);
            var keys = GetKeys(parseResult);
            var name = parseResult.GetValue(keyArgument)!;
            var key = name.Trim().ToLowerInvariant();
            if (!keys.Contains(key))
            {
                Console.Error.WriteLine(
                    $"Unknown context key '{name}'. Valid keys: {string.Join(", ", keys.Select(known => known.Key))}.");
                return 1;
            }

            var value = parseResult.GetValue(valueArgument)!.Trim();
            var problem = value.Length == 0
                ? $"{key} must not be blank."
                : keys[key].Select(use => ContextResolver.Validate(use.Command, use.Option, value))
                    .FirstOrDefault(error => error is not null);
            if (problem is not null)
            {
                Console.Error.WriteLine(problem);
                return 1;
            }

            var configuration = Program.BuildConfiguration(parseResult);
            var identity = await GetRequiredIdentityAsync(authFactory(parseResult), cancellationToken);
            await storeFactory().UpdateAsync(
                identity,
                [new ContextChange(key, CreateEndpoints(parseResult, baseUrlOption, configuration)(keys[key]), value)],
                profile,
                cancellationToken);

            WriteRows([new ContextRow(key, value)], profile, parseResult);
            return 0;
        }));
        return command;
    }

    private static Command CreateUnsetCommand(
        Func<ParseResult, AuthenticationService> authFactory,
        Func<ContextStore> storeFactory)
    {
        var keysArgument = new Argument<string[]>("key")
        {
            Description = "Keys to remove. 'xbox context key list' lists them.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var allOption = new Option<bool>("--all")
        {
            Description =
                "Remove every value saved in the profile for the signed-in account, on every "
                + "endpoint, including keys saved by newer CLI versions.",
        };
        var baseUrlOption = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("unset", "Remove saved values");
        command.Arguments.Add(keysArgument);
        command.Options.Add(allOption);
        command.Options.Add(baseUrlOption);
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.LocalState,
            Scope:
                "The context saved for the signed-in account, in the profile --profile or "
                + "XBOX_PROFILE selects. Flags and environment variables are unaffected.",
            Output:
                "JSON by default: the profile and the removed keys, with null values. Table "
                + "output has KEY and VALUE columns.",
            Examples:
            [
                "xbox context unset seller-id",
                "xbox context unset --all",
            ],
            LearnMore:
            [
                "Removing a value that isn't saved succeeds and changes nothing.",
            ],
            Table: TableView));

        command.SetAction((parseResult, cancellationToken) => RunAsync(async () =>
        {
            RefuseInAutomation(parseResult);
            var profile = ContextProfiles.From(parseResult);
            var names = parseResult.GetValue(keysArgument) ?? [];
            var all = parseResult.GetValue(allOption);
            var known = GetKeys(parseResult);
            var validNames = string.Join(", ", known.Select(key => key.Key));
            if (all == (names.Length > 0))
            {
                Console.Error.WriteLine(all
                    ? "Pass key names or --all, not both."
                    : $"Name a key to remove ({validNames}), or pass --all.");
                return 1;
            }

            List<string> keys = all ? [.. known.Select(key => key.Key)] : [];
            foreach (var name in names)
            {
                var key = name.Trim().ToLowerInvariant();
                if (!known.Contains(key))
                {
                    Console.Error.WriteLine($"Unknown context key '{name}'. Valid keys: {validNames}.");
                    return 1;
                }

                if (!keys.Contains(key))
                {
                    keys.Add(key);
                }
            }

            var configuration = Program.BuildConfiguration(parseResult);
            var identity = await GetRequiredIdentityAsync(authFactory(parseResult), cancellationToken);
            var store = storeFactory();

            // A misspelled profile would otherwise remove nothing and look like it worked.
            if (profile != ContextProfiles.Default
                && !await store.HasProfileAsync(identity, profile, cancellationToken))
            {
                Console.Error.WriteLine(ContextProfiles.NotFoundMessage(profile));
                return 1;
            }

            if (all)
            {
                await store.RemoveAllAsync(identity, profile, cancellationToken);
            }
            else
            {
                var endpoints = CreateEndpoints(parseResult, baseUrlOption, configuration);
                await store.UpdateAsync(
                    identity,
                    keys.Select(key => new ContextChange(key, endpoints(known[key]), Value: null)),
                    profile,
                    cancellationToken);
            }

            WriteRows(keys.Select(key => new ContextRow(key, null)), profile, parseResult);
            return 0;
        }));
        return command;
    }

    /// <summary>
    /// The keys values can be saved under, by name: the names of the options marked as context
    /// values in the command tree, each with the commands and options that take it.
    /// </summary>
    private static ILookup<string, (Command Command, Option Option)> GetKeys(ParseResult parseResult) =>
        ContextResolver.FindAll(parseResult.RootCommandResult.Command)
            .OrderBy(use => ContextResolver.GetKey(use.Option), StringComparer.Ordinal)
            .ToLookup(use => ContextResolver.GetKey(use.Option));

    /// <summary>
    /// The endpoint a key's saved value belongs to: the PlayFab endpoint, or the XboxPartner
    /// gateway, resolved at most once, and only if a key needs it.
    /// </summary>
    private static Func<IEnumerable<(Command Command, Option Option)>, string> CreateEndpoints(
        ParseResult parseResult,
        Option<string?> baseUrlOption,
        IConfiguration configuration)
    {
        string? gateway = null;
        return key => ContextResolver.GetEndpoint(key.First().Option) == ContextEndpoint.PlayFab
            ? ContextResolver.GetPlayFabEndpoint(configuration)
            : gateway ??= XboxPartnerCommandSupport.ResolveBaseUrl(parseResult, baseUrlOption, configuration);
    }

    /// <summary>
    /// The top-level command group that takes <paramref name="use"/>'s option: its own, or, for an
    /// option every command inherits, the group it applies to.
    /// </summary>
    private static string GetGroup(Command root, (Command Command, Option Option) use)
    {
        if (use.Command == root)
        {
            return ContextResolver.GetEndpoint(use.Option) == ContextEndpoint.PlayFab
                ? CommandPath.PlayFabCommandName
                : root.Name;
        }

        var command = use.Command;
        while (command.Parents.OfType<Command>().FirstOrDefault() is { } parent && parent != root)
        {
            command = parent;
        }

        return command.Name;
    }

    /// <summary>
    /// Refuses a saved-context command in a service-principal mode, which has no saved context,
    /// rather than telling the caller to sign in as a user.
    /// </summary>
    private static void RefuseInAutomation(ParseResult parseResult)
    {
        if (Program.GetAuthentication(parseResult).IsAutomationSelected)
        {
            throw new InvalidOperationException(ContextResolver.GetAutomationMessage(parseResult, "Saved context"));
        }
    }

    private static async Task<SignedInIdentity> GetRequiredIdentityAsync(
        AuthenticationService auth, CancellationToken cancellationToken) =>
        await SignedInIdentity.FromCacheAsync(auth, cancellationToken)
        ?? throw new InvalidOperationException(
            $"Not signed in. Run '{CliIdentity.CommandName} auth login' first.");

    /// <summary>
    /// The signed-in account for <c>context get</c>, which is how a script or agent checks its
    /// target: a token cache that can't be read is reported, then treated as signed out.
    /// </summary>
    private static async Task<SignedInIdentity?> TryGetIdentityAsync(
        AuthenticationService auth, CancellationToken cancellationToken)
    {
        // Only token-cache failures mean "signed out"; deferred configuration failures must fail.
        auth.ValidateConfiguration();
        try
        {
            return await SignedInIdentity.FromCacheAsync(auth, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or MsalException)
        {
            Console.Error.WriteLine(exception.Message);
            return null;
        }
    }

    /// <summary>
    /// Writes the rows, with the profile they're in, as JSON or as the command's declared table.
    /// <paramref name="account"/> is set for <c>context get</c>, which reports whose context it is;
    /// a <c>null</c> identity in it is written as <c>"account": null</c>.
    /// </summary>
    private static void WriteRows(
        IEnumerable<ContextRow> rows,
        string profile,
        ParseResult parseResult,
        AccountOutput? account = null)
    {
        var values = rows.ToList();
        object payload = account is null
            ? new { profile, values }
            : new
            {
                account = account.Identity is { } identity
                    ? new { identity.Username, identity.TenantId }
                    : null,
                profile,
                values,
            };
        Write(payload, parseResult);
    }

    /// <summary>Writes <paramref name="payload"/> as JSON, or as the command's declared table.</summary>
    private static void Write(object payload, ParseResult parseResult)
    {
        var output = OutputOptions.From(parseResult);
        Console.WriteLine(output.TableView is { } view
            ? TableFormatter.Format(JsonSerializer.SerializeToElement(payload, IndentedJson), view)
            : JsonSerializer.Serialize(payload, output.IsCompact ? CompactJson : IndentedJson));
    }

    /// <summary>Runs a context action, turning local failures into a message and exit code 1.</summary>
    private static async Task<int> RunAsync(Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or InvalidDataException
                                              or IOException
                                              or UnauthorizedAccessException
                                              or MsalException)
        {
            return XboxPartnerCommandSupport.WriteLocalError(exception);
        }
    }

    private sealed record ContextRow(string Key, string? Value);

    private sealed record AccountOutput(SignedInIdentity? Identity);
}
