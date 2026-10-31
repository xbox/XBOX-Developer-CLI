// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XboxDevCLI.PlayFab.Generated.CloudScript;
using XboxDevCLI.Services;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Creates the "playfab cloudscript function" command group for managing Azure Functions
/// registrations used by Cloud Script (the <c>/CloudScript/*Function*</c> APIs).
/// </summary>
/// <remarks>
/// These commands manage the <em>registration</em> that tells PlayFab about an Azure Function.
/// They never create, modify, or delete the Azure Function itself, which lives in Azure and is
/// managed there.
/// </remarks>
internal static class CloudScriptFunctionCommandFactory
{
    internal const string ListFunctionsPath = "/CloudScript/ListFunctions";
    internal const string ListHttpFunctionsPath = "/CloudScript/ListHttpFunctions";
    internal const string ListQueuedFunctionsPath = "/CloudScript/ListQueuedFunctions";
    internal const string ListEventHubFunctionsPath = "/CloudScript/ListEventHubFunctions";
    internal const string RegisterHttpFunctionPath = "/CloudScript/RegisterHttpFunction";
    internal const string UnregisterFunctionPath = "/CloudScript/UnregisterFunction";

    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] ListFunctionTypes = { "http", "queued", "eventhub" };

    /// <summary>
    /// Trigger types that can be registered. PlayFab also exposes RegisterQueuedFunction and
    /// RegisterEventHubFunction, which the CLI does not surface yet; --type is validated against
    /// this list so an unsupported value fails locally with an actionable message instead of
    /// being silently treated as HTTP. Both of those take a connection string, which is a secret:
    /// surfacing either means reading the connection string from a file or stdin, as
    /// <c>--key-file</c> does for the HTTP function key, never from an option.
    /// </summary>
    private static readonly string[] RegisterFunctionTypes = { "http" };

    /// <summary>The query parameter Azure Functions reads a function key from.</summary>
    private const string FunctionKeyParameter = "code";

    private const string StandardInputPath = "-";

    internal const string FunctionKeyInUrlMessage =
        "--url must not include the function key (its code query parameter), because an option's " +
        "value is visible in shell history and process listings. Remove code from --url, and pass " +
        "the key with --key-file <path>, or pipe it in with --key-file -.";

    internal const string FunctionKeyFromTerminalMessage =
        "--key-file - reads the function key from stdin, which is a terminal here, so the key would " +
        "show on screen as you type it. Pipe the key in, or pass --key-file with the path of a file " +
        "holding it.";

    public static Command Create()
    {
        var command = new Command(
            "function",
            "Manage Azure Functions registrations used by Cloud Script");

        command.Subcommands.Add(CreateListCommand());
        command.Subcommands.Add(CreateRegisterCommand());
        command.Subcommands.Add(CreateUnregisterCommand());

        return command;
    }

    internal static string BuildListFunctionsApiPath(string? type)
    {
        return type switch
        {
            "http" => ListHttpFunctionsPath,
            "queued" => ListQueuedFunctionsPath,
            "eventhub" => ListEventHubFunctionsPath,
            _ => ListFunctionsPath,
        };
    }

    internal static (string apiPath, RegisterHttpFunctionRequest request) BuildRegisterHttpFunctionCall(
        string name, string url, string? functionKey = null)
    {
        var request = new RegisterHttpFunctionRequest
        {
            FunctionName = name,
            FunctionUrl = functionKey is null ? url : AppendFunctionKey(url, functionKey),
        };

        return (RegisterHttpFunctionPath, request);
    }

    /// <summary>
    /// Returns whether <paramref name="url"/>'s query string has a <c>code</c> parameter, which is
    /// where Azure Functions reads a function key from. The name is compared after
    /// percent-decoding and without regard to case, so <c>%63ode</c> or <c>CODE</c> can't slip a
    /// key past the check.
    /// </summary>
    internal static bool HasFunctionKeyInQuery(string url)
    {
        // The fragment ends the query, and a ? inside it is just fragment text.
        var fragmentStart = url.IndexOf('#', StringComparison.Ordinal);
        var beforeFragment = fragmentStart < 0 ? url : url[..fragmentStart];
        var queryStart = beforeFragment.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return false;
        }

        foreach (var parameter in beforeFragment[(queryStart + 1)..].Split('&'))
        {
            var separator = parameter.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? parameter : parameter[..separator];
            if (string.Equals(Uri.UnescapeDataString(name), FunctionKeyParameter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Adds <paramref name="functionKey"/> to <paramref name="url"/> as the <c>code</c> query
    /// parameter, keeping the rest of the URL exactly as given, so PlayFab receives the same URL
    /// the Azure portal shows.
    /// </summary>
    internal static string AppendFunctionKey(string url, string functionKey)
    {
        // Azure keys are base64, so = and / are common and legal in a query value; leaving them
        // as they are keeps the URL byte-for-byte what the portal shows. Anything else outside
        // the unreserved set, such as + or &, would change the key's meaning, so it's encoded.
        var encodedKey = Uri.EscapeDataString(functionKey)
            .Replace("%3D", "=", StringComparison.Ordinal)
            .Replace("%2F", "/", StringComparison.Ordinal);

        var fragmentStart = url.IndexOf('#', StringComparison.Ordinal);
        var beforeFragment = fragmentStart < 0 ? url : url[..fragmentStart];
        var fragment = fragmentStart < 0 ? string.Empty : url[fragmentStart..];

        var separator = !beforeFragment.Contains('?', StringComparison.Ordinal) ? "?"
            : beforeFragment.EndsWith('?') || beforeFragment.EndsWith('&') ? string.Empty
            : "&";

        return $"{beforeFragment}{separator}{FunctionKeyParameter}={encodedKey}{fragment}";
    }

    /// <summary>
    /// Reads the function key from <paramref name="path"/>, or from stdin when the path is
    /// <c>-</c>. Throws <see cref="InvalidOperationException"/> with guidance, never including
    /// the key, and <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is canceled, even while stdin is waiting for input.
    /// </summary>
    internal static async Task<string> ReadFunctionKeyAsync(
        string path,
        Func<bool> isStandardInputRedirected,
        Func<Stream> openStandardInput,
        CancellationToken cancellationToken)
    {
        string content;
        if (path == StandardInputPath)
        {
            if (!isStandardInputRedirected())
            {
                throw new InvalidOperationException(FunctionKeyFromTerminalMessage);
            }

            // Read the bytes as UTF-8 rather than through Console.In, which decodes with the
            // console code page on Windows. A pipe read may ignore its token, so WaitAsync makes
            // Ctrl+C stop the wait even when the writer never closes the pipe.
            using var reader = new StreamReader(openStandardInput(), new UTF8Encoding(false));
            content = await reader.ReadToEndAsync(cancellationToken).WaitAsync(cancellationToken);
        }
        else
        {
            try
            {
                content = await File.ReadAllTextAsync(path, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                throw new InvalidOperationException(
                    $"Could not read --key-file '{path}': {exception.Message}", exception);
            }
        }

        // A function key never contains whitespace, so a trailing newline or stray space from an
        // editor or `echo` is trimmed rather than sent.
        var key = content.Trim();
        if (key.Length == 0)
        {
            throw new InvalidOperationException(
                path == StandardInputPath
                    ? "The function key read from stdin (--key-file -) is empty."
                    : $"The function key in --key-file '{path}' is empty.");
        }

        return key;
    }

    internal static (string apiPath, UnregisterFunctionRequest request) BuildUnregisterFunctionCall(
        string name)
    {
        var request = new UnregisterFunctionRequest
        {
            FunctionName = name,
        };

        return (UnregisterFunctionPath, request);
    }

    private static Command CreateListCommand()
    {
        var typeOption = new Option<string?>("--type")
        {
            Description = "Filter by function type: http, queued, or eventhub (omit for all)",
        };

        typeOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                !ListFunctionTypes.Contains(result.Tokens[0].Value, StringComparer.Ordinal))
            {
                result.AddError($"Invalid value for --type. Allowed values: {string.Join(", ", ListFunctionTypes)}.");
            }
        });

        var command = new Command("list", "List registered Azure Functions")
        {
            typeOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var type = parseResult.GetValue(typeOption);
            var output = OutputOptions.From(parseResult);

            try
            {
                using var client = PlayFabCommandHelper.CreateClient(parseResult);
                return await RunListAsync(client, type, output, Console.Out, Console.Error, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });

        // --type selects one of four APIs, each returning a different model. FunctionName is the
        // only field all four share, so it is the only column a single view can honestly declare;
        // any other would render as dashes for three of the four types.
        return command.WithMetadata(new CommandMetadata(
            ApiPaths:
            [
                ListFunctionsPath,
                ListHttpFunctionsPath,
                ListQueuedFunctionsPath,
                ListEventHubFunctionsPath,
            ],
            // Bound to the default --type's model. The other three responses are checked by the
            // metadata validator, which walks every path this command can dispatch to, so a
            // rename in any of them still fails before merge; this binding just makes the most
            // common one fail at compile time.
            Table: new TableView(
                nameof(ListFunctionsResult.Functions),
                [
                    new TableColumn("FUNCTION NAME", nameof(FunctionModel.FunctionName), 48),
                ])));
    }

    /// <summary>
    /// Executes the "cloudscript function list" logic against an already-constructed client.
    /// Extracted from the command action so the path-selection and output logic can be
    /// unit-tested with a fake transport and captured output streams.
    /// </summary>
    internal static Task<int> RunListAsync(
        PlayFabApiClient client,
        string? type,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var apiPath = BuildListFunctionsApiPath(type);
        return PlayFabCommandHelper.InvokeAsync(client, apiPath, "{}", output, stdout, stderr, cancellationToken: cancellationToken);
    }

    private static Command CreateRegisterCommand()
    {
        var nameOption = new Option<string>("--name")
        {
            Description = "Name of the function to register",
            Required = true,
        };

        var urlOption = new Option<string>("--url")
        {
            Description =
                "URL of the Azure Function HTTP endpoint, without its function key. Pass the key with --key-file.",
            Required = true,
        };
        urlOption.Validators.Add(result =>
        {
            // The message never repeats the URL, because the key is in it.
            if (result.Tokens.Count > 0 && HasFunctionKeyInQuery(result.Tokens[0].Value))
            {
                result.AddError(FunctionKeyInUrlMessage);
            }
        });

        var keyFileOption = new Option<string?>("--key-file")
        {
            Description =
                "File holding the function key, or - to read it from piped stdin. The CLI adds it to the " +
                "URL as the code query parameter. Omit it for a function that needs no key.",
            HelpName = "path",
        };

        var typeOption = new Option<string>("--type")
        {
            Description = $"Trigger type of the function to register. One of: {string.Join(", ", RegisterFunctionTypes)}.",
            Required = true,
        };
        typeOption.AcceptOnlyFromAmong(RegisterFunctionTypes);

        var command = new Command(
            "register",
            "Register an Azure Function with PlayFab. Registers the function with the title; " +
            "it does not create or modify the Azure Function itself.")
        {
            typeOption,
            nameOption,
            urlOption,
            keyFileOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var type = parseResult.GetValue(typeOption)!;
            var name = parseResult.GetValue(nameOption)!;
            var url = parseResult.GetValue(urlOption)!;
            var keyFile = parseResult.GetValue(keyFileOption);
            var output = OutputOptions.From(parseResult);

            // Dispatch on --type rather than assuming HTTP. Adding a value to
            // RegisterFunctionTypes without a matching arm here would otherwise register the
            // function against /CloudScript/RegisterHttpFunction under the wrong trigger type,
            // which is a production mutation to the wrong endpoint.
            if (!string.Equals(type, "http", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"Unsupported --type '{type}'. Supported trigger types: {string.Join(", ", RegisterFunctionTypes)}.");
                return 1;
            }

            string? functionKey = null;
            if (keyFile is not null)
            {
                try
                {
                    functionKey = await ReadFunctionKeyAsync(
                        keyFile, () => Console.IsInputRedirected, Console.OpenStandardInput, cancellationToken);
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Console.Error.WriteLine("Canceled while reading the function key. Nothing was registered.");
                    return 1;
                }
            }

            try
            {
                using var client = PlayFabCommandHelper.CreateClient(parseResult);
                return await RunRegisterHttpAsync(
                    client, name, url, functionKey, output, Console.Out, Console.Error,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [RegisterHttpFunctionPath],
            Examples:
            [
                "xbox playfab cloudscript function register --type http --name MatchWebhook " +
                    "--url https://nodpublishers.azurewebsites.net/api/MatchWebhook --key-file ./function-key.txt",
                "xbox playfab cloudscript function register --type http --name PublicStatus " +
                    "--url https://nodpublishers.azurewebsites.net/api/PublicStatus",
            ],
            LearnMore:
            [
                "Azure reads a function key from the URL's code query parameter. Pass --url without it and " +
                "the key with --key-file; the CLI sends PlayFab the complete URL.",
            ],
            SafetyDetails:
                "Registers the function with the title; the Azure Function itself is untouched."));
    }

    /// <summary>
    /// Executes the "cloudscript function register --type http" logic against an
    /// already-constructed client. Extracted from the command action so the request-building and
    /// output logic can be unit-tested with a fake transport and captured output streams.
    /// </summary>
    internal static Task<int> RunRegisterHttpAsync(
        PlayFabApiClient client,
        string name,
        string url,
        string? functionKey,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var (apiPath, request) = BuildRegisterHttpFunctionCall(name, url, functionKey);
        var body = JsonSerializer.Serialize(request, RequestOptions);

        return PlayFabCommandHelper.InvokeAsync(client, apiPath, body, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    private static Command CreateUnregisterCommand()
    {
        var nameOption = new Option<string>("--name")
        {
            Description = "Name of the function to unregister",
            Required = true,
        };

        var command = new Command(
            "unregister",
            "Unregister an Azure Function from PlayFab. Removes the title's registration; " +
            "it does not delete the Azure Function itself.")
        {
            nameOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var name = parseResult.GetValue(nameOption)!;
            var output = OutputOptions.From(parseResult);

            try
            {
                using var client = PlayFabCommandHelper.CreateClient(parseResult);
                return await RunUnregisterAsync(client, name, output, Console.Out, Console.Error, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });

        // UnregisterFunction is not a Delete* path, so the runtime confirmation gate does not
        // apply; the detail says what is and is not removed so nobody reads this as deleting the
        // function in Azure.
        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [UnregisterFunctionPath],
            SafetyDetails:
                "Removes the title's registration; the Azure Function itself is untouched."));
    }

    /// <summary>
    /// Executes the "cloudscript function unregister" logic against an already-constructed
    /// client. Extracted from the command action so the request-building and output logic can
    /// be unit-tested with a fake transport and captured output streams.
    /// </summary>
    internal static Task<int> RunUnregisterAsync(
        PlayFabApiClient client,
        string name,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var (apiPath, request) = BuildUnregisterFunctionCall(name);
        var body = JsonSerializer.Serialize(request, RequestOptions);

        return PlayFabCommandHelper.InvokeAsync(client, apiPath, body, output, stdout, stderr, cancellationToken: cancellationToken);
    }
}
