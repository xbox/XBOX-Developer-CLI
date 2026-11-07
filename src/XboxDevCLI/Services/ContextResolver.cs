// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Safety;

namespace XboxDevCLI.Services;

/// <summary>The service whose endpoint a saved context value belongs to.</summary>
internal enum ContextEndpoint
{
    XboxPartner,
    PlayFab,
}

/// <summary>
/// Fills in context values: the options marked with <see cref="AsContextValue"/>, such as the
/// seller and PlayFab title, that a command can take without its flag. Between parsing and running
/// a command, each marked option it takes but wasn't given gets the option's variable
/// (<c>XBOX_</c> and the option's name, so <c>--seller-id</c> is <c>XBOX_SELLER_ID</c>), else the
/// value <c>context set</c> saved under the option's name in the selected profile, for the
/// signed-in account and the host of the endpoint the value belongs to. The value is parsed as if
/// it had been typed, so the option's own parsing and validation apply, and its source is kept.
/// </summary>
/// <remarks>
/// Saved context is shared by every session of the OS user, and variables never appear on the
/// command line. So a command that changes something names each value it didn't get from its
/// command line, and where it came from, on stderr, even with <c>--confirm</c>; with
/// <c>--verbose</c>, every command does. A destructive operation that acts on such a value, such
/// as <c>/Admin/DeleteTitle</c> on the title, is refused. A command that uses saved values gets a
/// token only for the account they were saved for, should another sign in while it runs. Only
/// marked options resolve, so a variable can't fill in <c>--confirm</c> or a request body.
/// </remarks>
internal static class ContextResolver
{
    /// <summary>The options marked with <see cref="AsContextValue"/>, with the endpoint each one's saved value belongs to.</summary>
    private static readonly ConditionalWeakTable<Option, object> MarkedOptions = new();

    /// <summary>
    /// Options whose inline value is a request body or part of one, which a suggested command
    /// doesn't repeat.
    /// </summary>
    private static readonly string[] InlineBodyOptions = ["--body", "--statements"];


    /// <summary>Reads the signed-in account. A test seam; <see cref="ReadCachedIdentityAsync"/> by default.</summary>
    internal static Func<ParseResult, Task<SignedInIdentity?>> ReadIdentityAsync { get; set; } =
        ReadCachedIdentityAsync;

    /// <summary>
    /// Reads the signed-in account from the token cache, with the invocation's authentication
    /// service (<see cref="Program.GetAuthentication"/>), which the command then uses too.
    /// </summary>
    internal static Task<SignedInIdentity?> ReadCachedIdentityAsync(ParseResult parseResult) =>
        // Runs before the command is invoked, so there is no Ctrl-C token yet; it reads only the local cache.
        SignedInIdentity.FromCacheAsync(Program.GetAuthentication(parseResult), CancellationToken.None);

    /// <summary>
    /// Marks <paramref name="option"/> as a context value, and returns it for chaining. When a
    /// command that takes it runs without it, <see cref="ResolveAsync"/>, which <c>Program.Main</c>
    /// calls right after parsing, fills it in as if typed: from <c>XBOX_</c> and the option's name,
    /// else the value <c>context set</c> saved under the option's name for the host of
    /// <paramref name="endpoint"/>. So the command only reads the option.
    /// </summary>
    internal static TOption AsContextValue<TOption>(this TOption option, ContextEndpoint endpoint)
        where TOption : Option
    {
        MarkedOptions.AddOrUpdate(option, endpoint);
        return option;
    }


    /// <summary>The endpoint a marked option's saved value belongs to, or <c>null</c> when it isn't marked.</summary>
    internal static ContextEndpoint? GetEndpoint(Option option) =>
        MarkedOptions.TryGetValue(option, out var endpoint) ? (ContextEndpoint)endpoint : null;

    /// <summary>The name a value is saved under: the option's name, such as <c>seller-id</c>.</summary>
    internal static string GetKey(Option option) => option.Name.TrimStart('-');

    /// <summary>The option's variable: <c>--seller-id</c> is <c>XBOX_SELLER_ID</c>.</summary>
    internal static string GetVariable(Option option) =>
        "XBOX_" + GetKey(option).Replace('-', '_').ToUpperInvariant();

    /// <summary>The option's description, plus where a marked option's value comes from, for Help.</summary>
    internal static string Describe(Option option) =>
        GetEndpoint(option) is null
            ? option.Description ?? string.Empty
            : $"{option.Description} Defaults to {GetVariable(option)}, then the value saved by "
              + $"'context set {GetKey(option)}'.";

    /// <summary>Every marked option under <paramref name="command"/>, with the command it's on.</summary>
    internal static IEnumerable<(Command Command, Option Option)> FindAll(Command command) =>
        command.Options
            .Where(option => GetEndpoint(option) is not null)
            .Select(option => (command, option))
            .Concat(command.Subcommands.SelectMany(FindAll));

    /// <summary>
    /// What a command says when it needs <paramref name="option"/> and nothing supplied it: how to
    /// supply it, or, signed out, to sign in, since saved values and <c>context set</c> need an
    /// account. A service principal has no saved context, so it's told only the flag and variable.
    /// </summary>
    internal static async Task<string> GetMissingMessageAsync(ParseResult parseResult, Option option)
    {
        var key = GetKey(option);
        if (Program.GetInvocation(parseResult).IsAutomationSelected)
        {
            return $"No {key} is set. Pass {option.Name} or set {GetVariable(option)}.";
        }

        if (await ReadIdentityAsync(parseResult) is null)
        {
            return $"Not signed in. Run '{CliIdentity.CommandName} auth login' first.";
        }

        var profile = ContextProfiles.From(parseResult);
        return $"No {key} is set{(profile == ContextProfiles.Default ? "" : $" in profile {profile}")}. "
            + $"Pass {option.Name}, set {GetVariable(option)}, or run "
            + $"'{ContextProfiles.ContextCommand(profile)} set {key} <value>'.";
    }

    /// <summary>
    /// Why saved context can't be used: a service principal has no saved context, so each value
    /// comes from its flag or variable.
    /// </summary>
    internal static string GetAutomationMessage(ParseResult parseResult, string subject) =>
        $"{subject} doesn't apply when {AutomationAuthenticationSettings.ModeVariable} is "
        + $"'{Program.GetInvocation(parseResult).AuthenticationModeName}': a service principal has no saved "
        + "context. Pass each value with its flag or XBOX_ variable, such as --playfab-title-id or "
        + "XBOX_PLAYFAB_TITLE_ID.";

    /// <summary>The PlayFab endpoint saved PlayFab values belong to.</summary>
    internal static string GetPlayFabEndpoint(IConfiguration configuration) =>
        EndpointOverrideWarning.GetEffectiveBaseUrlTemplate(
            configuration[EndpointOverrideWarning.ConfigurationKey]);

    /// <summary>
    /// Why <paramref name="value"/> isn't valid for <paramref name="option"/>, as its own parsing
    /// and validation say when it's given to <paramref name="command"/>, or <c>null</c>.
    /// </summary>
    internal static string? Validate(Command command, Option option, string value)
    {
        var path = new List<string>();
        var root = command;
        while (root.Parents.OfType<Command>().FirstOrDefault() is { } parent)
        {
            path.Insert(0, root.Name);
            root = parent;
        }

        return root.Parse([.. path, $"{option.Name}={value}"], Program.NoResponseFileConfig).Errors
            .FirstOrDefault(error => IsAbout(error, [option]))?.Message;
    }

    /// <summary>
    /// Fills in the context values <paramref name="parseResult"/>'s command takes but wasn't
    /// given, and returns the invocation to run: <paramref name="parseResult"/> when there's
    /// nothing to fill in, else <paramref name="args"/> parsed again with the values added. Any
    /// other parse error wins, so nothing is looked up for an invocation that can't run.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A value fails its option's validation, naming where it came from; an explicit
    /// <c>--profile</c> doesn't exist; a profile is selected in a service-principal mode; a command
    /// that calls PlayFab has no title; or a destructive operation acts on a value the command
    /// didn't get from its command line.
    /// </exception>
    internal static async Task<ParseResult> ResolveAsync(
        ParseResult parseResult,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var options = GetOptions(parseResult).ToList();
        var missing = options.Where(option => parseResult.GetResult(option) is not { Implicit: false }).ToList();
        if (parseResult.Errors.Any(error => !IsAbout(error, missing)))
        {
            return parseResult;
        }

        // Preparation validates what the command declares it needs, and refuses options that don't
        // apply to the selected sign-in mode, before anything is looked up.
        Program.Prepare(parseResult);
        var explicitProfile = ContextProfiles.Normalize(parseResult.GetValue(ContextProfiles.Option));

        if (options.Count == 0)
        {
            return parseResult;
        }

        var values = await FindAsync(parseResult, missing, explicitProfile, cancellationToken);
        if (values.Count == 0)
        {
            return parseResult;
        }

        // Added ahead of any "--", after which everything is an argument.
        var position = args.ToList().IndexOf("--") is var index and >= 0 ? index : args.Count;
        var resolved = parseResult.RootCommandResult.Command.Parse(
            [.. args.Take(position), .. values.Select(value => $"{value.Option.Name}={value.Value}"), .. args.Skip(position)],
            Program.NoResponseFileConfig);
        var problems = values
            .SelectMany(value => resolved.Errors
                .Where(error => IsAbout(error, [value.Option]))
                .Select(error => $"{value.Origin}: {error.Message}"))
            .ToList();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        }

        Program.ShareConfiguration(parseResult, resolved);
        WriteSources(resolved, values, explicitProfile);
        RefuseImplicitTarget(parseResult, values);
        return resolved;
    }

    /// <summary>
    /// The marked options the command takes: its own, and those it inherits, except that an
    /// inherited PlayFab value, the title, applies only to a command that calls PlayFab. So a
    /// command that doesn't, such as <c>auth status</c>, never looks it up.
    /// </summary>
    private static IEnumerable<Option> GetOptions(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;
        var callsPlayFab = CommandMetadataRegistry.CallsPlayFab(command);
        var inherited = new List<Option>();
        for (var parent = command.Parents.OfType<Command>().FirstOrDefault();
             parent is not null;
             parent = parent.Parents.OfType<Command>().FirstOrDefault())
        {
            inherited.AddRange(parent.Options.Where(option =>
                option.Recursive && (callsPlayFab || GetEndpoint(option) != ContextEndpoint.PlayFab)));
        }

        return command.Options.Concat(inherited).Where(option => GetEndpoint(option) is not null).Distinct();
    }

    /// <summary>
    /// The values for <paramref name="missing"/>: each option's variable, else its saved value. The
    /// account is read only when a named profile has to be checked or the saved context holds one
    /// of the keys, so a signed-out command that needs no saved value never touches the token
    /// cache. Without a title, a command that calls PlayFab fails here, saying how to set one.
    /// </summary>
    private static async Task<List<ContextValue>> FindAsync(
        ParseResult parseResult,
        IReadOnlyList<Option> missing,
        string? explicitProfile,
        CancellationToken cancellationToken)
    {
        var values = new List<ContextValue>();
        foreach (var option in missing)
        {
            var variable = Environment.GetEnvironmentVariable(GetVariable(option));
            if (!string.IsNullOrWhiteSpace(variable))
            {
                values.Add(new ContextValue(option, variable.Trim(), Profile: null));
            }
        }

        var unsaved = missing.Where(option => values.All(value => value.Option != option)).ToList();

        // A service principal has no saved context, so it's never read.
        if (Program.GetInvocation(parseResult).IsAutomationSelected)
        {
            if (unsaved.FirstOrDefault(option => GetEndpoint(option) == ContextEndpoint.PlayFab) is { } needed)
            {
                throw new InvalidOperationException(await GetMissingMessageAsync(parseResult, needed));
            }

            return values;
        }

        var checkProfile = explicitProfile is not null && explicitProfile != ContextProfiles.Default;
        if (unsaved.Count == 0 && !checkProfile)
        {
            return values;
        }

        var configuration = Program.GetConfiguration(parseResult);

        var profile = ContextProfiles.From(parseResult);
        var store = new ContextStore();
        var identity = new Lazy<Task<SignedInIdentity?>>(() => ReadIdentityAsync(parseResult));

        // A named profile has to be one this account has: with --profile even when nothing saved is
        // needed, and with XBOX_PROFILE once a value is looked up. HasAnyAsync below looks across
        // accounts, so it can't tell. Signed out, there's no account to have profiles; the command
        // fails on its sign-in.
        if (profile != ContextProfiles.Default
            && await identity.Value is { } account
            && !await store.HasProfileAsync(account, profile, cancellationToken))
        {
            throw new InvalidOperationException(ContextProfiles.NotFoundMessage(profile));
        }

        if (unsaved.Count > 0
            && await store.HasAnyAsync(unsaved.Select(GetKey).ToList(), profile, cancellationToken)
            && await identity.Value is { } signedIn)
        {
            foreach (var option in unsaved)
            {
                if (GetEndpointUrl(parseResult, option, configuration) is { } endpoint
                    && await store.GetAsync(signedIn, GetKey(option), endpoint, profile, cancellationToken) is { } saved
                    && !string.IsNullOrWhiteSpace(saved))
                {
                    values.Add(new ContextValue(option, saved.Trim(), profile));
                }
            }
        }

        // PlayFab's client reads the title from configuration, so it can't say which profile lacks
        // one, or that saved values need a sign-in.
        if (unsaved.FirstOrDefault(option => GetEndpoint(option) == ContextEndpoint.PlayFab
                && values.All(value => value.Option != option)) is { } title)
        {
            throw new InvalidOperationException(await GetMissingMessageAsync(parseResult, title));
        }

        return values;
    }

    /// <summary>
    /// Refuses a profile, from <c>--profile</c> or <see cref="ContextProfiles.EnvironmentVariable"/>,
    /// in a service-principal mode, rather than ignoring it: a profile selects saved context, which
    /// a service principal doesn't have.
    /// </summary>
    internal static void RefuseProfileInAutomation(ParseResult parseResult, string? explicitProfile)
    {
        if (!Program.GetInvocation(parseResult).IsAutomationSelected)
        {
            return;
        }

        if (explicitProfile is not null)
        {
            throw new InvalidOperationException(
                GetAutomationMessage(parseResult, "--profile") + " Remove --profile.");
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ContextProfiles.EnvironmentVariable)))
        {
            throw new InvalidOperationException(
                GetAutomationMessage(parseResult, ContextProfiles.EnvironmentVariable)
                + $" Unset {ContextProfiles.EnvironmentVariable}.");
        }
    }

    /// <summary>
    /// The endpoint <paramref name="option"/>'s saved value belongs to: the PlayFab endpoint, or the
    /// XboxPartner gateway, from the command's <c>--base-url</c> or configuration.
    /// </summary>
    private static string? GetEndpointUrl(ParseResult parseResult, Option option, IConfiguration configuration)
    {
        if (GetEndpoint(option) == ContextEndpoint.PlayFab)
        {
            return GetPlayFabEndpoint(configuration);
        }

        var baseUrl = XboxPartnerCommandSupport.GetBaseUrlOptionValue(parseResult);
        baseUrl ??= configuration["XboxPartner:BaseUrl"];
        return string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim();
    }

    /// <summary>
    /// Names each value the command didn't get from its command line on stderr: always for a
    /// command that changes something, and with <c>--verbose</c> for the rest. A variable that wins
    /// over the profile <c>--profile</c> asked for is named even for a read.
    /// </summary>
    private static void WriteSources(ParseResult parseResult, IReadOnlyList<ContextValue> values, string? explicitProfile)
    {
        var changes = CommandMetadataRegistry.IsMutation(parseResult.CommandResult.Command, GetApiPath(parseResult));
        var verbose = parseResult.GetValue(Program.VerboseOption);
        foreach (var value in values)
        {
            var line = $"Using {value.Option.Name} {value.Value} from {value.Origin}";
            if (value.Profile is null && explicitProfile is not null)
            {
                Console.Error.WriteLine($"{line}, not profile {explicitProfile}.");
            }
            else if (changes)
            {
                Console.Error.WriteLine($"{line}.");
            }
            else
            {
                Program.WriteVerbose(verbose, () => $"{line}.");
            }
        }
    }

    /// <summary>
    /// Refuses a destructive operation that acts on a value the command didn't get from its command
    /// line, such as <c>/Admin/DeleteTitle</c> on a saved title, since saved context and the
    /// environment can change unseen. An operation that's destructive only in one mode, such as
    /// <c>/Admin/UpdatePolicy</c> with <c>--overwrite</c>, is refused only in that mode. The message
    /// gives the command with the value written out.
    /// </summary>
    private static void RefuseImplicitTarget(ParseResult original, IReadOnlyList<ContextValue> values)
    {
        IReadOnlyList<string> paths = GetApiPath(original) is { } path
            ? [path]
            : CommandMetadataRegistry.GetMetadata(original.CommandResult.Command).ApiPaths ?? [];
        foreach (var apiPath in paths)
        {
            if (DestructiveOperations.GetContextTarget(apiPath) is not { } rule
                || !IsInMode(original, rule.Mode)
                || values.FirstOrDefault(value => value.Option.Name == rule.Option) is not { } target)
            {
                continue;
            }

            var (command, omitted) = WithOption(original, target.Option.Name, target.Value);
            var operation = rule.Mode is null ? $"'{apiPath}'" : $"'{apiPath}' with {rule.Mode}";
            var message = $"{operation} acts on {GetKey(target.Option)} {target.Value}, which comes from "
                + $"{target.Origin}.{Environment.NewLine}"
                + "The target of a destructive operation must be named on the command line. To proceed, run:"
                + $"{Environment.NewLine}  {command}";
            foreach (var option in omitted)
            {
                message += Environment.NewLine
                    + $"The {option} value isn't repeated, since a request body can hold secrets; put it in place of {Placeholder(option)}.";
            }

            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Whether the invocation selected <paramref name="mode"/>, a flag of its command; always, when
    /// there's no mode.
    /// </summary>
    private static bool IsInMode(ParseResult parseResult, string? mode) =>
        mode is null
        || (parseResult.CommandResult.Command.Options.OfType<Option<bool>>().FirstOrDefault(option => option.Name == mode)
                is { } flag
            && parseResult.GetValue(flag));

    /// <summary>What stands in for an omitted inline value: <c>&lt;body&gt;</c> for <c>--body</c>.</summary>
    private static string Placeholder(string option) => $"<{option.TrimStart('-')}>";

    /// <summary>
    /// The invocation as a command line, quoted for the shell it's likely to be pasted into, with
    /// the global <paramref name="option"/> right after the command name: anywhere later, it could
    /// follow a <c>--</c> and be read as an argument. An inline request body, such as a
    /// <c>--body</c> or <c>--statements</c> value, isn't repeated, since it goes to stderr and a
    /// request body can hold secrets: a placeholder such as <c>&lt;body&gt;</c> stands in for it,
    /// and <c>Omitted</c> names each option left out.
    /// </summary>
    private static (string Command, IReadOnlyList<string> Omitted) WithOption(
        ParseResult parseResult,
        string option,
        string value)
    {
        var bodies = parseResult.CommandResult.Command.Options
            .Where(candidate => InlineBodyOptions.Contains(candidate.Name))
            .ToList();
        var tokens = parseResult.Tokens;
        var arguments = new List<string> { option, ShellQuoting.Quote(value) };
        var omitted = new List<string>();
        for (var index = 0; index < tokens.Count; index++)
        {
            arguments.Add(ShellQuoting.Quote(tokens[index].Value));
            if (tokens[index].Type == TokenType.Option
                && bodies.FirstOrDefault(body => tokens[index].Value == body.Name || body.Aliases.Contains(tokens[index].Value))
                    is { } omittedBody
                && index + 1 < tokens.Count
                && tokens[index + 1].Type == TokenType.Argument)
            {
                arguments.Add(Placeholder(omittedBody.Name));
                if (!omitted.Contains(omittedBody.Name))
                {
                    omitted.Add(omittedBody.Name);
                }

                index++;
            }
        }

        return ($"{CliIdentity.CommandName} {string.Join(' ', arguments)}", omitted);
    }

    /// <summary>The PlayFab path the invocation takes at run time, such as <c>playfab api</c>'s, or <c>null</c>.</summary>
    private static string? GetApiPath(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;
        return CommandMetadataRegistry.GetMetadata(command).Safety == CommandSafety.DependsOnApiPath
            ? parseResult.GetResult(command.Arguments[0])?.Tokens.FirstOrDefault()?.Value
            : null;
    }

    private static bool IsAbout(ParseError error, IReadOnlyCollection<Option> options) =>
        error.SymbolResult switch
        {
            OptionResult result => options.Contains(result.Option),
            ArgumentResult { Parent: OptionResult result } => options.Contains(result.Option),
            _ => false,
        };

    /// <summary>
    /// A value filled in for <see cref="Option"/>, and where it came from: the option's variable
    /// when <see cref="Profile"/> is <c>null</c>, else that profile of the signed-in account's
    /// saved context.
    /// </summary>
    private sealed record ContextValue(Option Option, string Value, string? Profile)
    {
        public string Origin =>
            Profile is null ? GetVariable(Option)
            : Profile == ContextProfiles.Default ? "saved context"
            : $"profile {Profile}";
    }
}
