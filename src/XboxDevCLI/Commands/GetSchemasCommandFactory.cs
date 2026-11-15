// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class GetSchemasCommandFactory
{
    /// <summary>Builds <c>schema list</c>: every schema type, or one type's versions.</summary>
    public static Command CreateList()
    {
        var typeOption = CreateTypeOption(
            "Document schema type whose versions to list. Default: omitted, which lists all types.");
        var command = new Command("list", "List XBOX services configuration schema types, or one type's versions");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "XBOX services configuration schemas.",
            Output: "A JSON array of schema types, or of one type's versions, or a table.",
            Examples:
            [
                "xbox xbl schema list",
                "xbox xbl schema list --output table",
                "xbox xbl schema list --type achievements2017",
            ],
            Table: XboxPartnerCommandSupport.SchemaTableView));
        command.Options.Add(typeOption);
        SetAction(command, typeOption, versionOption: null, destinationOption: null);
        return command;
    }

    /// <summary>Builds <c>schema get</c>: one schema version's type, version, and namespace.</summary>
    public static Command CreateGet()
    {
        var typeOption = CreateTypeOption("Document schema type.");
        typeOption.Required = true;
        var versionOption = CreateVersionOption("Positive schema version. Default: the type's latest version.");
        var command = new Command("get", "Get one XBOX services configuration schema version");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One XBOX services configuration schema version.",
            Output: "A JSON object with the schema's type, version, and namespace, or a table. " +
                "To save the XSD itself, use schema download.",
            Examples:
            [
                "xbox xbl schema get --type achievements2017",
                "xbox xbl schema get --type achievements2017 --version 1 --output table",
            ],
            Table: XboxPartnerCommandSupport.SchemaVersionTableView));
        command.Options.Add(typeOption);
        command.Options.Add(versionOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            try
            {
                var output = OutputOptions.From(parseResult);
                using var client = CreateClient(parseResult);
                return await GetAsync(
                    client,
                    parseResult.GetValue(typeOption)!,
                    parseResult.GetValue(versionOption),
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });
        return command;
    }

    /// <summary>Builds <c>schema download</c>: XSD files saved into a directory.</summary>
    public static Command CreateDownload()
    {
        var typeOption = CreateTypeOption("Document schema type. Default: omitted, which downloads every type.");
        var versionOption = CreateVersionOption(
            "Positive schema version. Requires --type. Default: omitted, which downloads every version.");
        var destinationOption = new Option<string>("--dest")
        {
            Description = "Directory for XSD files, or - to write one XSD to stdout, which requires --type and --version.",
            Required = true,
        };
        destinationOption.Validators.Add(result =>
        {
            if (string.IsNullOrWhiteSpace(result.GetValueOrDefault<string>()))
            {
                result.AddError("--dest must name a directory.");
            }
        });
        var command = new Command("download", "Download XBOX services configuration schemas (XSD files)");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "XBOX services configuration schemas.",
            Output: "XSD files named <type>_<version>.xsd, and the saved paths as JSON or a table. " +
                "With --dest -, the raw XSD on stdout, and --output doesn't apply.",
            Examples:
            [
                "xbox xbl schema download --type achievements2017 --version 1 --dest schemas",
                "xbox xbl schema download --type achievements2017 --version 1 --dest -",
                "xbox xbl schema download --type achievements2017 --dest schemas",
                "xbox xbl schema download --dest schemas",
            ],
            Table: XboxPartnerCommandSupport.SchemaTableView));
        command.Options.Add(typeOption);
        command.Options.Add(versionOption);
        command.Options.Add(destinationOption);
        command.Validators.Add(result =>
        {
            if (result.GetResult(versionOption) is { } version &&
                ParsedVersion(version) is not null &&
                string.IsNullOrWhiteSpace(result.GetValue(typeOption)))
            {
                result.AddError("--version requires --type.");
            }

            if (result.GetValue(destinationOption) == StdoutDestination &&
                (string.IsNullOrWhiteSpace(result.GetValue(typeOption)) ||
                    result.GetResult(versionOption) is not { } givenVersion ||
                    ParsedVersion(givenVersion) is null))
            {
                result.AddError("--dest - writes one schema to stdout. Give both --type and --version.");
            }
        });
        SetAction(command, typeOption, versionOption, destinationOption);
        return command;
    }

    private static Option<string?> CreateTypeOption(string description)
    {
        var option = new Option<string?>("--type") { Description = description };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
            {
                result.AddError("--type must name a schema type.");
            }
        });
        return option;
    }

    private static Option<int?> CreateVersionOption(string description)
    {
        var option = new Option<int?>("--version") { Description = description };
        option.Validators.Add(result =>
        {
            if (ParsedVersion(result) <= 0)
            {
                result.AddError("--version must be a positive integer.");
            }
        });
        return option;
    }

    /// <summary>
    /// The value given for <c>--version</c>, or <c>null</c> when it wasn't given or isn't a number.
    /// </summary>
    /// <remarks>
    /// Validators read the token rather than calling <c>GetValue</c>, which throws for a value
    /// that didn't parse. Leaving that value alone lets the parser report it, rather than a
    /// validator naming some other problem.
    /// </remarks>
    private static int? ParsedVersion(OptionResult result) =>
        result.Tokens.Count > 0 &&
        int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    private static void SetAction(
        Command command,
        Option<string?> typeOption,
        Option<int?>? versionOption,
        Option<string>? destinationOption)
    {
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            try
            {
                var output = OutputOptions.From(parseResult);
                using var client = CreateClient(parseResult);
                await using Stream rawStdout = Console.OpenStandardOutput();
                return await RunAsync(
                    client,
                    parseResult.GetValue(typeOption),
                    versionOption is null ? null : parseResult.GetValue(versionOption),
                    destinationOption is null ? null : parseResult.GetValue(destinationOption),
                    output,
                    Console.Out,
                    Console.Error,
                    rawStdout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });
    }

    private static XboxPartnerClient CreateClient(ParseResult parseResult)
    {
        var configuration = Program.GetConfiguration(parseResult);
        _ = XboxPartnerCommandSupport.ResolveBaseUrl(configuration);
        return Program.XboxPartnerClientFactory is null
            ? new XboxPartnerClient(
                configuration,
                authenticationService: Program.GetAuthentication(parseResult))
            : Program.XboxPartnerClientFactory(configuration);
    }

    /// <summary>The <c>--dest</c> value that writes the selected XSD to stdout.</summary>
    internal const string StdoutDestination = "-";

    /// <summary>Writes one schema version's type, version, and namespace; the latest when no version is given.</summary>
    internal static async Task<int> GetAsync(
        XboxPartnerClient client,
        string schemaType,
        int? version,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        string normalizedType = schemaType.Trim();
        try
        {
            string versionsJson;
            using (RequestProgress.Start($"Getting {normalizedType} schema versions", stderr))
            {
                versionsJson = await client
                    .GetSchemaVersionsAsync(normalizedType, cancellationToken)
                    .ConfigureAwait(false);
            }

            XconSchemaVersion[] versions =
                JsonSerializer.Deserialize<XconSchemaVersion[]>(versionsJson, JsonOptions) ??
                throw new JsonException("The schema version list response was invalid.");
            XconSchemaVersion? selected = version.HasValue
                ? versions.FirstOrDefault(candidate => candidate.Version == version.Value)
                : versions.MaxBy(candidate => candidate.Version);
            if (selected is null)
            {
                stderr.WriteLine(version.HasValue
                    ? FormattableString.Invariant(
                        $"Request failed: Schema type '{normalizedType}' has no version {version.Value}. List its versions with: xbox xbl schema list --type {normalizedType}")
                    : $"Request failed: Schema type '{normalizedType}' has no versions.");
                return 1;
            }

            string versionText = selected.Version.ToString(CultureInfo.InvariantCulture);
            stdout.WriteLine(output.TableView is not null
                ? XboxPartnerTableFormatter.FormatRows(
                    output.TableView,
                    [[normalizedType, versionText, selected.Namespace]])
                : JsonSerializer.Serialize(
                    new
                    {
                        type = normalizedType,
                        version = selected.Version,
                        @namespace = selected.Namespace,
                    },
                    output.IsCompact ? CompactJsonOptions : PrettyJsonOptions));
            return 0;
        }
        catch (XboxPartnerApiException ex) when (!cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, output.IsCompact));
            return 1;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    internal static async Task<int> RunAsync(
        XboxPartnerClient client,
        string? schemaType,
        int? version,
        string? destination,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        Stream rawStdout,
        CancellationToken cancellationToken)
    {
        bool compact = output.IsCompact;
        TableView? tableView = output.TableView;
        try
        {
            if (schemaType is null)
            {
                string typesJson;
                using (RequestProgress.Start("Getting schema types", stderr))
                {
                    typesJson = await client
                        .GetSchemaTypesAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                string[] types = JsonSerializer.Deserialize<string[]>(typesJson, JsonOptions)
                    ?? throw new JsonException(
                        "The schema type list response was invalid.");
                if (string.IsNullOrWhiteSpace(destination))
                {
                    stdout.WriteLine(tableView is not null
                        ? XboxPartnerTableFormatter.FormatRows(
                            tableView,
                            types.Select(type => new[] { type, "", "", "", "" }))
                        : PlayFabApiClient.FormatJson(typesJson, compact));
                    return 0;
                }

                await SaveEverySchemaVersionAsync(
                    client,
                    types,
                    destination,
                    output,
                    stdout,
                    stderr,
                    cancellationToken).ConfigureAwait(false);
                return 0;
            }

            string normalizedType = schemaType.Trim();
            if (!version.HasValue)
            {
                if (!string.IsNullOrWhiteSpace(destination))
                {
                    await SaveEverySchemaVersionAsync(
                        client,
                        [normalizedType],
                        destination,
                        output,
                        stdout,
                        stderr,
                        cancellationToken).ConfigureAwait(false);
                    return 0;
                }

                string versionsJson;
                using (RequestProgress.Start($"Getting {normalizedType} schema versions", stderr))
                {
                    versionsJson = await client
                        .GetSchemaVersionsAsync(normalizedType, cancellationToken)
                        .ConfigureAwait(false);
                }

                XconSchemaVersion[] versions =
                    JsonSerializer.Deserialize<XconSchemaVersion[]>(
                        versionsJson,
                        JsonOptions) ??
                    throw new JsonException(
                        "The schema version list response was invalid.");
                stdout.WriteLine(tableView is not null
                    ? XboxPartnerTableFormatter.FormatRows(
                        tableView,
                        versions.Select(schemaVersion => new[]
                        {
                            normalizedType,
                            schemaVersion.Version.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            schemaVersion.Namespace,
                            "",
                            "",
                        }))
                    : PlayFabApiClient.FormatJson(versionsJson, compact));
                return 0;
            }

            byte[] schema;
            using (RequestProgress.Start(
                FormattableString.Invariant($"Getting {normalizedType} schema version {version.Value}"), stderr))
            {
                schema = await client
                    .GetSchemaAsync(normalizedType, version.Value, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(destination) || destination == StdoutDestination)
            {
                await rawStdout.WriteAsync(schema, cancellationToken).ConfigureAwait(false);
                await rawStdout.FlushAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }

            string schemaPath = GetSchemaPath(
                EnsureDirectory(destination),
                normalizedType,
                version.Value);
            await WriteSchemaFileAsync(schemaPath, schema, cancellationToken)
                .ConfigureAwait(false);
            stdout.WriteLine(tableView is not null
                ? XboxPartnerTableFormatter.FormatRows(
                    tableView,
                    [
                        [
                            normalizedType,
                            version.Value.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            "",
                            schemaPath,
                            schema.Length.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                        ],
                    ])
                : JsonSerializer.Serialize(
                    new
                    {
                        type = normalizedType,
                        version = version.Value,
                        path = schemaPath,
                    },
                    compact ? CompactJsonOptions : PrettyJsonOptions));
            return 0;
        }

        catch (XboxPartnerApiException ex) when (!cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, compact));
            return 1;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
    };

    private static async Task SaveEverySchemaVersionAsync(
        XboxPartnerClient client,
        IEnumerable<string> types,
        string destination,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        string? directory = null;
        var savedPaths = new List<string>();
        var tableRows = new List<string[]>();
        using (var progress = RequestProgress.Start("Getting schema versions", stderr))
        {
            foreach (string type in types)
            {
                progress.Activity = $"Getting {type} schema versions";
                string versionsJson = await client
                    .GetSchemaVersionsAsync(type, cancellationToken)
                    .ConfigureAwait(false);
                XconSchemaVersion[] versions =
                    JsonSerializer.Deserialize<XconSchemaVersion[]>(
                        versionsJson,
                        JsonOptions) ??
                    throw new JsonException("The schema version list response was invalid.");
                foreach (XconSchemaVersion schemaVersion in versions)
                {
                    progress.Activity = FormattableString.Invariant(
                        $"Downloading {type} schema version {schemaVersion.Version}");
                    byte[] content = await client
                        .GetSchemaAsync(type, schemaVersion.Version, cancellationToken)
                        .ConfigureAwait(false);
                    directory ??= EnsureDirectory(destination);
                    string path = GetSchemaPath(directory, type, schemaVersion.Version);
                    await WriteSchemaFileAsync(path, content, cancellationToken)
                        .ConfigureAwait(false);
                    savedPaths.Add(path);
                    tableRows.Add(
                    [
                        type,
                        schemaVersion.Version.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        schemaVersion.Namespace,
                        path,
                        content.Length.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    ]);
                }
            }
        }

        stdout.WriteLine(output.TableView is not null
            ? XboxPartnerTableFormatter.FormatRows(
                output.TableView,
                tableRows)
            : JsonSerializer.Serialize(
                savedPaths,
                output.IsCompact ? CompactJsonOptions : PrettyJsonOptions));
    }

    private static string EnsureDirectory(string destination)
        => SafeOutputDirectory.Create(destination);

    private static string GetSchemaPath(string directory, string schemaType, int version)
    {
        string fileType = schemaType.ToLowerInvariant();
        if (fileType.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "Schema type contains characters that cannot be used in a file name.",
                nameof(schemaType));
        }

        return Path.Combine(directory, $"{fileType}_{version}.xsd");
    }

    private static async Task WriteSchemaFileAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        SafeOutputDirectory.ValidateExistingComponents(Path.GetDirectoryName(path)!);
        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".xboxdevcli-schema-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken)
                .ConfigureAwait(false);
            RejectExistingReparsePoint(path);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void RejectExistingReparsePoint(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The existing schema path is a reparse point.");
        }
    }

    private sealed record XconSchemaVersion(int Version, string Namespace);
}
