// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Output;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.Presentation;
using XboxDevCLI.Safety;
using XboxDevCLI.Services;
using XboxDevCLI.Telemetry;

namespace XboxDevCLI;

public class Program
{
    internal const long DefaultMaxRequestBodyBytes = 1 * 1024 * 1024;

    // Each invocation's dependencies follow its ParseResult, without a static cache keeping
    // completed invocations alive. ShareConfiguration carries them across context reparsing.
    // GetValue can run its factory more than once under contention; the stored Lazy ensures only
    // one invocation captures the environment.
    private static readonly ConditionalWeakTable<ParseResult, Lazy<CommandInvocation>> Invocations = new();

    /// <summary>
    /// Parser configuration that disables response-file expansion (<c>@file</c> arguments).
    /// Without this, <see cref="System.CommandLine"/> tries to open the path as a file and
    /// throws <see cref="UnauthorizedAccessException"/> when it encounters a directory or a
    /// path it cannot read.  XboxDevCLI does not document or support response files, so we
    /// disable the feature to make parsing fail-safe against arbitrary argv.
    /// </summary>
    internal static readonly ParserConfiguration NoResponseFileConfig =
        new() { ResponseFileTokenReplacer = null };

    // Global options available to all commands

    /// <summary>
    /// Earlier releases loaded <c>appsettings.{environment}.json</c> from beside the executable.
    /// Hidden, and kept only to tell anyone who still passes it what replaced it.
    /// </summary>
    internal static readonly Option<string> ConfigEnvOption = CreateRetiredConfigEnvOption();

    internal const string ConfigEnvRetiredMessage =
        "--config-env is no longer supported because settings files are no longer read. "
        + "Set XBOX_ environment variables instead, such as XBOX_AZUREAD__CLIENTID.";

    private static Option<string> CreateRetiredConfigEnvOption()
    {
        var option = new Option<string>("--config-env")
        {
            Description = "No longer supported. Set XBOX_ environment variables instead.",
            HelpName = "environment",
            Recursive = true,
            Hidden = true,
        };
        option.Validators.Add(result => result.AddError(ConfigEnvRetiredMessage));
        return option;
    }

    internal static readonly Option<string?> ClientIdOption = new("--client-id")
    {
        Description = "Override the Microsoft Entra application client ID for user sign-in; refused in automation modes.",
        HelpName = "id",
        Recursive = true,
    };

    internal static readonly Option<string?> TenantIdOption = new("--tenant-id")
    {
        Description = "Override the Microsoft Entra tenant ID for user sign-in; refused in automation modes.",
        HelpName = "id",
        Recursive = true,
    };

    internal static readonly Option<string?> ScopesOption = new("--scopes")
    {
        Description = "Override Microsoft Entra authentication scopes.",
        HelpName = "scopes",
        Recursive = true,
    };

    /// <remarks>
    /// A context value (<see cref="ContextResolver"/>) whose saved value belongs to the PlayFab
    /// endpoint, so it's filled in only for commands that call PlayFab. It is registered
    /// recursively so it parses both before and after a subcommand, the placement flexibility
    /// the documentation's examples rely on.
    /// </remarks>
    internal static readonly Option<string?> PlayFabTitleIdOption = CreatePlayFabTitleIdOption()
        .AsContextValue(ContextEndpoint.PlayFab);

    internal static readonly Option<string> OutputOption = CreateOutputOption();

    /// <remarks>
    /// Registered recursively so it parses both before and after a subcommand, but it is read only
    /// when unwrapping a PlayFab response envelope, so it is marked
    /// <see cref="OptionAudience.PlayFab"/> and Help hides it outside the <c>playfab</c> subtree
    /// rather than restricting where it can be typed.
    /// </remarks>
    internal static readonly Option<bool> DataOnlyOption =
        new Option<bool>("--data-only")
        {
            Description = "Output only the PlayFab response's 'data' field.",
            Recursive = true,
        }.WithAudience(OptionAudience.PlayFab);

    internal static readonly Option<bool> VerboseOption = new("--verbose")
    {
        Description = "Write diagnostic information to stderr.",
        Recursive = true,
    };

    internal static readonly Option<bool> ConfirmOption = CreateConfirmOption();

    internal static readonly Option<bool> DisableTelemetryOption = new("--disable-telemetry")
    {
        Description = "Disable anonymous telemetry for this invocation.",
        Recursive = true,
    };

    /// <summary>
    /// An explicitly blank title (for example <c>--playfab-title-id "$TITLE"</c> with the variable
    /// unset) is an error rather than "no flag", so it can't fall back to a saved title. A title is
    /// letters and digits, so one from a variable or saved context can't add a request header.
    /// </summary>
    private static Option<string?> CreatePlayFabTitleIdOption()
    {
        var option = new Option<string?>("--playfab-title-id")
        {
            Description = "Target this PlayFab title.",
            HelpName = "id",
            Recursive = true,
        };
        option.Validators.Add(result =>
        {
            var problem = result.Tokens.Count == 0 ? null
                : string.IsNullOrWhiteSpace(result.Tokens[0].Value) ? "must contain at least one non-whitespace character"
                : result.Tokens[0].Value.Trim().All(char.IsAsciiLetterOrDigit) ? null
                : "must contain only letters and digits";
            if (problem is not null)
            {
                result.AddError($"--playfab-title-id {problem}.");
            }
        });
        return option;
    }

    private static Option<string> CreateOutputOption()
    {
        var option = new Option<string>("--output", "-o")
        {
            Description = "Format output as 'json' (default), 'json-compact', or 'table' where supported.",
            HelpName = "format",
            Recursive = true,
        };

        option.Validators.Add(result => ValidateAllowedValue(result, "json", "json-compact", "table"));
        return option;
    }

    private static Option<bool> CreateConfirmOption()
    {
        // --yes/-y is the Azure/gh-standard alias for the destructive-operation
        // confirmation backstop; --confirm is retained as the primary spelling.
        // It is registered recursively so it parses anywhere. It gates PlayFab API
        // calls, so Help scopes it to the playfab subtree, and it also confirms the
        // Partner Center creates 'game create' and 'sandbox create', and
        // 'xbl publish start', when it appears before the command; those declare
        // their own local --confirm for Help.
        return new Option<bool>("--confirm", "--yes", "-y")
        {
            Description = "Confirm a destructive, irreversible operation; required outside an interactive terminal.",
            Recursive = true,
        }.WithAudience(OptionAudience.PlayFab);
    }

    private static void ValidateAllowedValue(OptionResult result, params string[] allowedValues)
    {
        if (result.Tokens.Count == 0)
        {
            return;
        }

        var value = result.Tokens[0].Value;
        if (!allowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            result.AddError($"Invalid value '{value}' for option '{result.Option.Name}'. Allowed values: {string.Join(", ", allowedValues)}.");
        }
    }

    /// <remarks>
    /// The invocation runs in four steps. It's parsed, its configuration is loaded without
    /// reporting anything, and an unknown command, Help, or the version is handled without
    /// preparing anything, so they never depend on configuration or sign-in, and never warn about
    /// a changed service endpoint; other syntax errors are reported when the parse result is
    /// invoked. Then the dependencies the command declares (<see cref="CommandRequirements"/>) are
    /// prepared, the warnings for the endpoints it calls (<see cref="CommandEndpoints"/>) are
    /// written, and its context values are resolved; then the command runs with them. Expected
    /// failures from preparation, context resolution, and the command itself are reported by
    /// <see cref="CommandErrorBoundary"/>.
    /// </remarks>
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = BuildRootCommand();
        var parseResult = rootCommand.Parse(args, NoResponseFileConfig);
        // Captures the authentication inputs once, for preparation, context, the command, and
        // telemetry. Nothing is validated yet.
        var invocation = GetInvocation(parseResult);
        if (CommandLineErrorWriter.TryWriteUnknownCommand(
                parseResult,
                rootCommand,
                CliIdentity.CommandName,
                Console.Error))
        {
            return 1;
        }

        // Help and the version never prepare anything. All four routes to help count — `--help`,
        // the `help` command, a bare group, and the bare root — which is why they are identified
        // by a marker type rather than enumerated here.
        var helpRequested =
            parseResult.Action is HelpAction or CommandHelpAction or HelpRenderingAction;
        var terminalActionRequested =
            helpRequested || IsVersionRequested(parseResult, args);

        var verbose = parseResult.GetValue(VerboseOption);

        using var telemetry = TelemetryService.Create(
            GetTelemetryConfiguration(parseResult),
            message => WriteVerbose(verbose, () => message));

        var focusedFirstRun = WriteWelcomeIfFirstRun(
            parseResult,
            args,
            CliState.GetDefaultDirectory(),
            Console.Error,
            Environment.UserInteractive,
            Console.IsErrorRedirected);

        if (telemetry.IsCollecting)
        {
            TelemetryDisclosure.WriteIfFirstRun(
                TelemetryDisclosure.GetDefaultStateDirectory(),
                Console.Error,
                WelcomeExperience.SupportsColor(Console.IsErrorRedirected));
        }

        var stopwatch = Stopwatch.StartNew();
        var exitCode = 1;
        try
        {
            if (focusedFirstRun)
            {
                exitCode = 0;
                return exitCode;
            }

            if (!terminalActionRequested)
            {
                if (await ResolveContextAsync(parseResult, args) is not { } resolved)
                {
                    return exitCode;
                }

                parseResult = resolved;

                // This gate is the least authoritative diagnostic the CLI has, so it runs last
                // among the things that can reject an invocation. Parse errors win because they
                // name the actual problem: the parser reports them when the action runs, so
                // `auth status --nope --output table` would otherwise blame the format while the
                // real fault was the typo.
                if (parseResult.Errors.Count == 0
                    && string.Equals(
                        parseResult.GetValue(OutputOption),
                        "table",
                        StringComparison.OrdinalIgnoreCase)
                    && !SupportsTableOutput(parseResult.CommandResult.Command))
                {
                    // Say what is actually true: this command declares no table view. The
                    // response may well be a list — `playfab api` returns whatever the endpoint
                    // returns — so claiming otherwise would misdiagnose a valid request. The
                    // remedy deliberately does not name JSON as "the default" either: `auth status`
                    // and `info` default to human-readable text.
                    Console.Error.WriteLine(TableUnavailableMessage);
                    return exitCode;
                }
            }

            exitCode = await parseResult.InvokeAsync();
            return exitCode;
        }
        catch (Exception exception)
        {
            telemetry.RecordUnhandledFailure(exception);
            throw;
        }
        finally
        {
            // After Ctrl-C, System.CommandLine stops waiting for the command before cleanup it
            // started, such as canceling a pending file upload, has finished.
            await ExitCleanup.Default.WaitAsync(ExitCleanup.MaxWait);
            stopwatch.Stop();
            telemetry.RecordCommand(
                CommandPathResolver.ResolveTelemetryIdentity(
                    rootCommand,
                    parseResult.CommandResult.Command),
                exitCode,
                stopwatch.Elapsed,
                invocation.AuthenticationModeName);
        }
    }

    private static bool IsVersionRequested(ParseResult parseResult, IReadOnlyList<string> arguments) =>
        parseResult.CommandResult.Command is RootCommand
        && arguments.Any(argument => argument == "--version");

    /// <summary>
    /// Returns whether <paramref name="command"/> can render its response as a table.
    /// </summary>
    /// <remarks>
    /// Read from the command's own metadata rather than a central list of command paths. The
    /// canonicalization of the PlayFab surface renamed most of the tree once already, and a
    /// path-keyed list goes silently stale when that happens — the same reasoning that moved Help
    /// grouping and safety classification into metadata.
    /// </remarks>
    internal static bool SupportsTableOutput(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return CommandMetadataRegistry.GetMetadata(command).Table is not null;
    }

    internal static bool WriteWelcomeIfFirstRun(
        ParseResult parseResult,
        IReadOnlyList<string> arguments,
        string stateDirectory,
        TextWriter writer,
        bool isInteractive,
        bool isErrorRedirected,
        bool? useColor = null)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentNullException.ThrowIfNull(writer);

        var infoCommandHandlesWelcome =
            string.Equals(parseResult.CommandResult.Command.Name, "info", StringComparison.Ordinal);
        if (parseResult.Errors.Count > 0
            || infoCommandHandlesWelcome
            || !isInteractive
            || isErrorRedirected)
        {
            return false;
        }

        var colorEnabled =
            useColor ?? WelcomeExperience.SupportsColor(isErrorRedirected);
        if (arguments.Count == 0)
        {
            return WelcomeExperience.WriteFocusedIfFirstRun(
                stateDirectory,
                CliIdentity.Version,
                writer,
                colorEnabled);
        }

        if (IsExplicitRootHelpInvocation(parseResult, arguments))
        {
            WelcomeExperience.WriteLogoIfFirstRun(
                stateDirectory,
                CliIdentity.Version,
                writer,
                colorEnabled);
            return false;
        }

        WelcomeExperience.WriteIfFirstRun(
            stateDirectory,
            CliIdentity.Version,
            writer,
            colorEnabled);
        return false;
    }

    private static bool IsExplicitRootHelpInvocation(
        ParseResult parseResult,
        IReadOnlyList<string> arguments) =>
        (parseResult.CommandResult.Command is RootCommand
            && arguments.Any(argument => argument is "--help" or "-h"))
        || (arguments.Count == 1 && arguments[0] == "help");

    /// <summary>
    /// Builds the root command with all global options and subcommands.
    /// </summary>
    internal static RootCommand BuildRootCommand(
        Func<Command>? sellerCommandFactory = null,
        Func<Command>? gameCommandFactory = null,
        Func<Command>? contextCommandFactory = null,
        Func<Command>? sandboxCommandFactory = null,
        Func<Command>? pcCommandFactory = null)
    {
        var rootCommand = new RootCommand(
            $"{CliIdentity.DisplayName}: {CliIdentity.Tagline}");
        rootCommand.Options.Add(ConfigEnvOption);
        rootCommand.Options.Add(ClientIdOption);
        rootCommand.Options.Add(TenantIdOption);
        rootCommand.Options.Add(ScopesOption);
        rootCommand.Options.Add(PlayFabTitleIdOption);
        rootCommand.Options.Add(ContextProfiles.Option);
        rootCommand.Options.Add(OutputOption);
        rootCommand.Options.Add(DataOnlyOption);
        rootCommand.Options.Add(VerboseOption);
        rootCommand.Options.Add(ConfirmOption);
        rootCommand.Options.Add(DisableTelemetryOption);

        var authCommand = AuthCommandFactory.Create()
            .WithMetadata(new CommandMetadata(Category: HelpCategory.Core));
        rootCommand.Subcommands.Add(authCommand);
        rootCommand.Subcommands.Add(
            (contextCommandFactory ?? ContextCommandFactory.Create).Invoke()
                .WithMetadata(new CommandMetadata(Category: HelpCategory.Core)));
        rootCommand.Subcommands.Add(InfoCommandFactory.Create()
            .WithMetadata(new CommandMetadata(Category: HelpCategory.Support)));
        rootCommand.Subcommands.Add(UpgradeCommandFactory.Create());
        rootCommand.Subcommands.Add(FeedbackCommandFactory.Create());
        rootCommand.Subcommands.Add(
            (gameCommandFactory ?? GameCommandFactory.Create).Invoke()
                .WithMetadata(new CommandMetadata(Category: HelpCategory.Core)));
        rootCommand.Subcommands.Add(
            (sellerCommandFactory ?? SellerCommandFactory.Create).Invoke()
                .WithMetadata(new CommandMetadata(Category: HelpCategory.Core)));
        rootCommand.Subcommands.Add(TestAccountCommandFactory.Create()
            .WithMetadata(new CommandMetadata(Category: HelpCategory.Core)));
        rootCommand.Subcommands.Add(PlayFabCommandFactory.Create()
            .WithMetadata(new CommandMetadata(Category: HelpCategory.Core)));
        rootCommand.Subcommands.Add((sandboxCommandFactory ?? SandboxCommandFactory.Create).Invoke()
            .WithMetadata(new CommandMetadata(Category: HelpCategory.Core)));
        rootCommand.Subcommands.Add((pcCommandFactory ?? PcCommandFactory.Create).Invoke()
            .WithMetadata(new CommandMetadata(
                Category: HelpCategory.Core,
                Stage: CommandStage.Experimental)));
        rootCommand.Subcommands.Add(XboxLiveCommandFactory.Create()
            .WithMetadata(new CommandMetadata(
                Category: HelpCategory.Core,
                Stage: CommandStage.Experimental)));

        CommandLineExperience.ConfigureHelp(rootCommand, CliIdentity.CommandName);
        CommandErrorBoundary.Install(rootCommand);

        return rootCommand;
    }

    /// <summary>
    /// The invocation's shared dependencies, created the first time an invocation asks, which
    /// captures its authentication inputs.
    /// </summary>
    internal static CommandInvocation GetInvocation(ParseResult parseResult) =>
        Invocations.GetValue(
            parseResult,
            static result => new Lazy<CommandInvocation>(
                () => new CommandInvocation(result, () => LayerConfiguration(result)))).Value;

    /// <summary>
    /// Prepares what <paramref name="parseResult"/>'s command declares it needs
    /// (<see cref="CommandRequirements"/>), and writes the warnings for the endpoints it declares
    /// it calls (<see cref="CommandEndpoints"/>). Running it again does nothing more.
    /// </summary>
    internal static void Prepare(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;
        GetInvocation(parseResult).Prepare(CommandRequirements.For(command), CommandEndpoints.For(command));
    }

    /// <summary>
    /// The invocation's loaded configuration, or the error loading it, for a test of its layering:
    ///   1. The built-in settings (<see cref="BuiltInSettings"/>), embedded in the executable
    ///   2. Environment variables prefixed with XBOX_, with <c>__</c> separating sections
    ///   3. CLI option overrides (highest priority)
    /// No settings file is read. Commands use <see cref="GetConfiguration"/>.
    /// </summary>
    internal static IConfiguration LoadConfiguration(ParseResult parseResult) =>
        GetInvocation(parseResult).LoadConfiguration();

    /// <summary>
    /// The settings telemetry reads: the invocation's loaded configuration, or, if it couldn't be
    /// loaded, the <c>XBOX_</c> environment variables and <c>--disable-telemetry</c> over
    /// telemetry's defaults, so a preparation failure is still recorded unless the user opted out.
    /// Telemetry never reports the configuration's error or warnings; preparation does, for a
    /// command that uses them.
    /// </summary>
    internal static IConfiguration GetTelemetryConfiguration(ParseResult parseResult)
    {
        if (GetInvocation(parseResult).LoadedConfiguration.Configuration is { } configuration)
        {
            return configuration;
        }

        var builder = new ConfigurationBuilder().AddEnvironmentVariables("XBOX_");
        if (parseResult.GetValue(DisableTelemetryOption))
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:Enabled"] = bool.FalseString,
            });
        }

        return builder.Build();
    }

    /// <summary>The invocation's prepared configuration snapshot.</summary>
    internal static IConfiguration GetConfiguration(ParseResult parseResult) =>
        GetInvocation(parseResult).Configuration;

    /// <summary>
    /// The invocation's prepared sign-in service for the selected mode. Context resolution,
    /// command actions, and API clients share it.
    /// </summary>
    internal static AuthenticationService GetAuthentication(ParseResult parseResult) =>
        GetInvocation(parseResult).Authentication;

    /// <summary>Makes <paramref name="authentication"/> the invocation's prepared sign-in service.</summary>
    internal static void SetAuthentication(ParseResult parseResult, AuthenticationService authentication) =>
        GetInvocation(parseResult).UseAuthentication(authentication);

    /// <summary>
    /// Gives <paramref name="resolved"/>, the invocation with context values filled in, the
    /// dependencies <paramref name="original"/> prepared: its captured authentication inputs, its
    /// loaded configuration and the endpoint warnings already written, so none is written twice,
    /// and its sign-in service, so the command acts as the account that read its saved values. The
    /// resolved title is added the way <see cref="LayerConfiguration"/> adds the flag.
    /// </summary>
    internal static void ShareConfiguration(ParseResult original, ParseResult resolved)
    {
        var invocation = GetInvocation(original).ForResolved(resolved);
        Invocations.AddOrUpdate(resolved, new Lazy<CommandInvocation>(invocation));
    }

    /// <summary>
    /// Prepares the invocation and fills in the context values it takes but wasn't given
    /// (<see cref="ContextResolver"/>), or reports why it can't through
    /// <see cref="CommandErrorBoundary"/> and returns <c>null</c>.
    /// </summary>
    internal static async Task<ParseResult?> ResolveContextAsync(ParseResult parseResult, IReadOnlyList<string> args)
    {
        try
        {
            return await ContextResolver.ResolveAsync(parseResult, args);
        }
        catch (Exception exception) when (CommandErrorBoundary.IsExpected(exception))
        {
            CommandErrorBoundary.Report(parseResult, exception);
            return null;
        }
    }

    /// <summary>
    /// Layers the invocation's configuration. Authentication, API clients, and the endpoint
    /// warnings all read this one snapshot, so nothing is reloaded between warning about an
    /// endpoint and sending it an access token.
    /// </summary>
    private static IConfigurationRoot LayerConfiguration(ParseResult parseResult)
    {
        var builder = new ConfigurationBuilder()
            .AddJsonStream(BuiltInSettings.Open());
        // Context variables become keys here too, such as SELLER_ID from XBOX_SELLER_ID. That's
        // harmless as long as no setting is named like a context key.
        builder.AddEnvironmentVariables("XBOX_");

        // Apply CLI overrides last so they win over everything else
        var overrides = new Dictionary<string, string?>();

        var clientId = parseResult.GetValue(ClientIdOption);
        if (clientId != null)
        {
            overrides["AzureAd:ClientId"] = clientId;
        }

        var tenantId = parseResult.GetValue(TenantIdOption);
        if (tenantId != null)
        {
            overrides["AzureAd:TenantId"] = tenantId;
        }

        var scopes = parseResult.GetValue(ScopesOption);
        if (scopes != null)
        {
            overrides["AzureAd:Scopes"] = scopes;
        }

        // Set even without the flag, so the built-in settings and XBOX_PLAYFAB__TITLEID can't set
        // the title. ContextResolver fills the flag in from XBOX_PLAYFAB_TITLE_ID or saved context.
        overrides["PlayFab:TitleId"] = parseResult.GetValue(PlayFabTitleIdOption)?.Trim();

        if (XboxPartnerCommandSupport.GetBaseUrlOptionValue(parseResult) is { } baseUrl)
        {
            overrides[EndpointOverrideWarning.XboxPartnerConfigurationKey] = baseUrl;
        }

        if (parseResult.GetValue(DisableTelemetryOption))
        {
            overrides["Telemetry:Enabled"] = bool.FalseString;
        }

        if (overrides.Count > 0)
        {
            builder.AddInMemoryCollection(overrides);
        }

        return builder.Build();
    }


    /// <summary>
    /// What the CLI prints when <c>--output table</c> is asked of a command with no declared view.
    /// </summary>
    /// <remarks>
    /// A constant because the command reference quotes it verbatim, and it has already drifted
    /// once: the docs kept telling users to "re-run with <c>--output json</c> (the default)" after
    /// the runtime stopped saying that. A test asserts the reference still contains this text.
    /// <para>
    /// The remedy names no specific format on purpose. It has to be actionable for every command
    /// that can reach it, and the commands differ: data commands default to JSON, <c>auth status</c>
    /// honours <c>--output json</c> but defaults to text, and <c>info</c> ignores <c>--output</c>
    /// altogether — so suggesting JSON would be wrong advice for <c>info</c> and calling JSON "the
    /// default" would be wrong for both. Omitting the option is the one remedy that works
    /// everywhere, and <c>--help</c> lists what each command actually accepts.
    /// </para>
    /// </remarks>
    internal const string TableUnavailableMessage =
        "Output format 'table' is not available for this command, because it does not " +
        "declare a table view. Omit --output to get this command's default output.";

    /// <summary>
    /// Test seam: when set, supplies the API client instead of constructing a real one.
    /// </summary>
    /// <remarks>
    /// <see cref="InvokeApiAsync"/> is the invocation path for every metadata-backed command,
    /// including all the MPS ones, and it builds its own client and writes straight to the
    /// console — so there was no way to assert that it honours a declared table view. Without
    /// that coverage, a regression in its formatter branch would return JSON for all of those
    /// commands while the suite stayed green, which is exactly how this feature shipped broken
    /// once already.
    /// <para>
    /// Kept as a nullable static rather than plumbed through the signature so the production path
    /// is byte-for-byte unchanged when it is null. Tests must reset it.
    /// </para>
    /// </remarks>
    internal static Func<IConfiguration, PlayFabApiClient>? ApiClientFactory;

    internal static Func<Stream>? StandardInputFactory;

    /// <summary>
    /// Test seam for commands that call the XboxPartner gateway directly.
    /// </summary>
    internal static Func<IConfiguration, XboxPartnerClient>? XboxPartnerClientFactory;
    internal static Func<HttpClient>? AchievementImageDownloadClientFactory;
    internal static Func<HttpClient>? AchievementImageUploadClientFactory;

    /// <summary>
    /// Resolves the JSON request body from inline (<paramref name="body"/>), file, or stdin
    /// (<paramref name="bodyFile"/>) sources, sends an authenticated POST request to
    /// <paramref name="apiPath"/>, and writes the formatted response to stdout.
    /// On error, the formatted error is written to stderr.
    /// </summary>
    /// <returns>0 on success; 1 on failure.</returns>
    internal static async Task<int> InvokeApiAsync(
        ParseResult parseResult,
        string apiPath,
        string? body,
        string? bodyFile,
        CancellationToken cancellationToken)
    {
        var outputFormat = parseResult.GetValue(OutputOption) ?? "json";
        var compact = outputFormat.Equals("json-compact", StringComparison.OrdinalIgnoreCase);
        var verbose = parseResult.GetValue(VerboseOption);

        try
        {
            var config = GetConfiguration(parseResult);

            if (body != null && bodyFile != null)
            {
                Console.Error.WriteLine("Error: --body and --body-file cannot both be specified.");
                return 1;
            }

            if (bodyFile == "-" && DestructiveOperations.IsDestructive(apiPath)
                && !parseResult.GetValue(ConfirmOption))
            {
                Console.Error.WriteLine(
                    $"Error: '{apiPath}' is destructive and requires --confirm when --body-file - reads stdin.");
                return 1;
            }

            var maxRequestBodyBytes = GetMaxRequestBodyBytes(config);

            string? jsonBody = body;
            if (jsonBody != null)
            {
                EnsureRequestBodyWithinLimit("--body", Encoding.UTF8.GetByteCount(jsonBody), maxRequestBodyBytes);
            }

            if (bodyFile != null)
            {
                if (bodyFile != "-" && !File.Exists(bodyFile))
                {
                    Console.Error.WriteLine($"Error: File not found: {bodyFile}");
                    return 1;
                }

                jsonBody = await ReadBodyFileWithLimitAsync(bodyFile, maxRequestBodyBytes, cancellationToken);
            }

            WriteVerbose(verbose, () => $"POST {apiPath}");

            // Destructive ops (Delete*, Shutdown*) require explicit confirmation. The CLI
            // resolves confirmation here (via --confirm or an interactive prompt); the
            // PlayFabApiClient re-checks and enforces it as a fail-closed backstop, so the
            // gate can't be bypassed by any caller that skips this method.
            var confirmedDestructive = false;
            if (DestructiveOperations.IsDestructive(apiPath))
            {
                confirmedDestructive = parseResult.GetValue(ConfirmOption) || ConfirmDestructiveOperation(apiPath);
                if (!confirmedDestructive)
                {
                    return 1;
                }
            }

            var authService = GetAuthentication(parseResult);
            using var client = ApiClientFactory is null
                ? new PlayFabApiClient(config, authService)
                : ApiClientFactory(config);
            var response = await client.CallApiAsync(apiPath, jsonBody, cancellationToken, confirmedDestructive);
            WriteVerbose(verbose, () => "Request succeeded.");

            // One object answers "what shape was asked for, and can this command produce it",
            // rather than re-deriving the format and the declared view separately here.
            var output = OutputOptions.From(parseResult);

            if (output.TableView is null)
            {
                Console.WriteLine(FormatOutput(response, output.IsCompact, output.DataOnly));
                return 0;
            }

            Console.WriteLine(FormatTableOutput(response, output.TableView));
            if (FormatNextPageNotice(response, output.Paging) is { } notice)
            {
                Console.Error.WriteLine(notice);
            }

            return 0;
        }
        catch (PlayFabApiException ex)
        {
            Console.Error.WriteLine(FormatErrorOutput(ex, compact));
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    internal static long GetMaxRequestBodyBytes(IConfiguration configuration)
    {
        var configuredMax = configuration.GetValue<long?>("PlayFab:MaxRequestBodyBytes");
        var maxRequestBodyBytes = configuredMax ?? DefaultMaxRequestBodyBytes;
        if (maxRequestBodyBytes <= 0)
        {
            throw new InvalidOperationException(
                "Configuration key 'PlayFab:MaxRequestBodyBytes' must be a positive number of bytes.");
        }

        return maxRequestBodyBytes;
    }

    internal static async Task<string> ReadBodyFileWithLimitAsync(
        string bodyFile,
        long maxRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        if (bodyFile == "-")
        {
            if (StandardInputFactory is null && OperatingSystem.IsWindows() && !Console.IsInputRedirected)
            {
                return await ReadConsoleInputWithLimitAsync(
                    Console.OpenStandardInput, maxRequestBodyBytes, cancellationToken);
            }

            using var input = StandardInputFactory is null ? Console.OpenStandardInput() : StandardInputFactory();
            return await ReadStandardInputWithLimitAsync(input, maxRequestBodyBytes, cancellationToken);
        }

        var bodyFileSize = new FileInfo(bodyFile).Length;
        EnsureRequestBodyWithinLimit("--body-file", bodyFileSize, maxRequestBodyBytes);
        return await File.ReadAllTextAsync(bodyFile, cancellationToken);
    }

    internal static async Task<string> ReadConsoleInputWithLimitAsync(
        Func<Stream> openInput,
        long maxRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        var originalInputEncoding = Console.InputEncoding;
        try
        {
            // Select UTF-8 before opening the console stream to avoid lossy OEM conversion.
            Console.InputEncoding = new UTF8Encoding(false);
            using var input = openInput();
            return await ReadStandardInputWithLimitAsync(input, maxRequestBodyBytes, cancellationToken);
        }
        finally
        {
            Console.InputEncoding = originalInputEncoding;
        }
    }

    internal static async Task<string> ReadStandardInputWithLimitAsync(
        Stream input,
        long maxRequestBodyBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = maxRequestBodyBytes - buffer.Length;
            var readSize = remaining >= chunk.Length ? chunk.Length : (int)remaining + 1;
            // Console streams may ignore cancellation while waiting for input.
            var bytesRead = await input.ReadAsync(chunk.AsMemory(0, readSize), cancellationToken)
                .AsTask().WaitAsync(cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            EnsureRequestBodyWithinLimit("--body-file -", buffer.Length + bytesRead, maxRequestBodyBytes);
            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
        }

        // PowerShell writes a UTF-8 byte order mark ahead of piped text when $OutputEncoding is
        // [Text.Encoding]::UTF8. Drop it, as reading a body file does, rather than send it.
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            bytes = bytes[Encoding.UTF8.Preamble.Length..];
        }

        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
    }

    internal static void EnsureRequestBodyWithinLimit(string source, long bodySizeBytes, long maxRequestBodyBytes)
    {
        if (bodySizeBytes > maxRequestBodyBytes)
        {
            throw new InvalidOperationException(
                $"{source} exceeds the configured maximum size of {maxRequestBodyBytes} bytes ({bodySizeBytes} bytes). " +
                "Increase 'PlayFab:MaxRequestBodyBytes' to allow larger request bodies.");
        }
    }

    /// <summary>
    /// Writes a diagnostic message to stderr when <paramref name="verbose"/> is enabled
    /// (via the global <c>--verbose</c> flag). Diagnostics always go to stderr so that
    /// machine-readable stdout stays clean for piping. No-op when verbose is disabled.
    /// </summary>
    /// <remarks>
    /// The message is supplied as a factory so diagnostic strings are only built when verbose is enabled.
    /// </remarks>
    internal static void WriteVerbose(bool verbose, Func<string> messageFactory)
    {
        if (verbose)
        {
            Console.Error.WriteLine($"[verbose] {messageFactory()}");
        }
    }

    /// <summary>
    /// Handles confirmation for a destructive, irreversible operation when the
    /// <c>--confirm</c> flag was not supplied.
    /// </summary>
    /// <remarks>
    /// When stdin is interactive, the user is prompted to type <c>yes</c> to proceed.
    /// When stdin is redirected (for example, when driven by an AI agent or script),
    /// the operation is refused so it cannot run without the user deliberately
    /// re-running the command with <c>--confirm</c>.
    /// </remarks>
    /// <returns><c>true</c> when the operation is confirmed and may proceed; otherwise <c>false</c>.</returns>
    internal static bool ConfirmDestructiveOperation(string apiPath)
    {
        Console.Error.WriteLine(
            $"WARNING: '{apiPath}' is a destructive, irreversible operation.");

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine(
                "This operation requires explicit confirmation. Re-run the command with the --confirm flag to proceed.");
            return false;
        }

        Console.Error.Write("Type 'yes' to confirm: ");
        var answer = Console.ReadLine();
        if (string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Console.Error.WriteLine("Operation cancelled.");
        return false;
    }

    /// <summary>
    /// Renders a PlayFab response as a table using the command's declared view.
    /// </summary>
    /// <remarks>
    /// The view describes the shape inside the response envelope's <c>data</c> object, so each
    /// command's columns do not have to repeat the envelope. <c>--data-only</c> is ignored here:
    /// a table has no envelope to strip, so the flag has nothing to do.
    /// </remarks>
    internal static string FormatTableOutput(string response, TableView view)
    {
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        var payload = root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data)
                ? data
                : root;

        return TableFormatter.Format(payload, view);
    }

    /// <summary>
    /// Says that a table shows one page of several, and how to get the next, when the response
    /// carries a next-page token; otherwise <c>null</c>.
    /// </summary>
    /// <remarks>
    /// A table has no place for the token, so without this a partial list reads as complete.
    /// JSON output carries the token itself and gets no notice. The token is written as a JSON
    /// string with every character outside printable ASCII escaped, so it pastes into a request
    /// body as is and a hostile value can't drive the terminal.
    /// </remarks>
    internal static string? FormatNextPageNotice(string response, CommandPaging? paging)
    {
        if (paging is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        var payload = root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data)
                ? data
                : root;

        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(paging.Token, out var token)
            || token.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(token.GetString()))
        {
            return null;
        }

        return $"More results exist; this table shows one page. To get the next page, add " +
            $"\"{paging.Token}\": {QuoteJsonAscii(token.GetString()!)} to the request body, keeping " +
            $"its other fields. Set \"{paging.PageSize}\" there to change how many results a page holds.";
    }

    private static string QuoteJsonAscii(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case < ' ' or > '~':
                    builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    /// <summary>
    /// Formats the API response string based on the selected output mode and format.
    /// </summary>
    /// <param name="response">The raw JSON response string from the PlayFab API.</param>
    /// <param name="compact">When true, produces single-line JSON output.</param>
    /// <param name="dataOnly">When true, extracts and returns only the <c>data</c> field of the response.</param>
    internal static string FormatOutput(string response, bool compact, bool dataOnly)
    {
        if (dataOnly)
        {
            try
            {
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("data", out var dataElement))
                {
                    return PlayFabApiClient.FormatJson(dataElement.GetRawText(), compact);
                }
            }
            catch (JsonException)
            {
                // Not valid JSON — fall through to format the full response
            }
            catch (ArgumentException)
            {
                // Not transcodable to UTF-8 (e.g. an unpaired UTF-16 surrogate) — fall
                // through to format the full response
            }
        }

        return PlayFabApiClient.FormatJson(response, compact);
    }

    /// <summary>
    /// Formats the error output for a <see cref="PlayFabApiException"/>.
    /// When the response body is valid JSON, it is formatted per <paramref name="compact"/>.
    /// Otherwise, falls back to the human-readable exception message.
    /// </summary>
    internal static string FormatErrorOutput(PlayFabApiException ex, bool compact)
    {
        if (ex.HttpStatusCode is null)
        {
            // No response: the message says what failed and, for a write, that it may have been applied.
            return $"Request failed: {ex.Message}";
        }

        try
        {
            var body = JsonNode.Parse(ex.RawBody);
            if (ex.OutcomeWarning is null)
            {
                return PlayFabApiClient.FormatJson(ex.RawBody, compact);
            }

            // Keep stderr one JSON document so scripts that parse the error still see the warning.
            if (body is JsonObject error)
            {
                error["outcomeUnknown"] = ex.OutcomeWarning;
                return PlayFabApiClient.FormatJson(error.ToJsonString(), compact);
            }

            return $"{PlayFabApiClient.FormatJson(ex.RawBody, compact)}{Environment.NewLine}{ex.OutcomeWarning}";
        }
        catch (Exception parseEx) when (parseEx is JsonException or ArgumentException)
        {
            // RawBody is not valid JSON (malformed, or not transcodable to UTF-8 such as
            // an unpaired UTF-16 surrogate) — use human-readable fallback
            if (ex.ApiErrorMessage == null && !string.IsNullOrWhiteSpace(ex.RawBody))
            {
                return $"{ex.Message}{Environment.NewLine}{ex.RawBody}";
            }

            return ex.Message;
        }
    }
}
