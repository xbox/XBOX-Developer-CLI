// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class GetSchemasCommandFactory
{
    public static Command Create()
    {
        var typeOption = new Option<string?>("--type", "-t")
        {
            Description = "Document schema type. Default: omitted, which lists all types.",
        };
        var versionOption = new Option<int?>("--version", "-v")
        {
            Description = "Positive schema version. Default: omitted, which lists versions, or with --dest saves every version.",
        };
        versionOption.Validators.Add(result =>
        {
            int? value = result.GetValueOrDefault<int?>();
            if (value.HasValue && value.Value <= 0)
            {
                result.AddError("--version must be a positive integer.");
            }
        });
        var destinationOption = new Option<string?>("--dest", "-d")
        {
            Description = "Directory for XSD files. Default: omitted; selected XSDs use stdout.",
        };

        var command = new Command("get", "Get XBOX services configuration document schemas");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "XBOX services configuration schemas.",
            Output: "Schema metadata as JSON or a table; selected XSD content is raw by default.",
            Examples:
            [
                "xbox xbl schema get",
                "xbox xbl schema get --output table",
                "xbox xbl schema get --type achievements2017",
                "xbox xbl schema get --type achievements2017 --version 1 --dest schemas",
                "xbox xbl schema get --type achievements2017 --dest schemas",
            ],
            Table: XboxPartnerCommandSupport.SchemaTableView));
        command.Options.Add(typeOption);
        command.Options.Add(versionOption);
        command.Options.Add(destinationOption);
        command.Validators.Add(result =>
        {
            string? schemaType = result.GetValue(typeOption);
            int? version = result.GetValue(versionOption);
            if (version.HasValue && string.IsNullOrWhiteSpace(schemaType))
            {
                result.AddError("--version requires --type.");
            }
        });
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            try
            {
                var output = OutputOptions.From(parseResult);
                var configuration = Program.BuildConfiguration(parseResult);
                _ = XboxPartnerCommandSupport.ResolveBaseUrl(configuration);
                using var client = Program.XboxPartnerClientFactory is null
                    ? new XboxPartnerClient(
                        configuration,
                        authenticationService: Program.GetAuthentication(parseResult))
                    : Program.XboxPartnerClientFactory(configuration);
                await using Stream rawStdout = Console.OpenStandardOutput();
                return await RunAsync(
                    client,
                    parseResult.GetValue(typeOption),
                    parseResult.GetValue(versionOption),
                    parseResult.GetValue(destinationOption),
                    output,
                    Console.Out,
                    Console.Error,
                    rawStdout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });

        return command;
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
                string typesJson = await client
                    .GetSchemaTypesAsync(cancellationToken)
                    .ConfigureAwait(false);
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
                        cancellationToken).ConfigureAwait(false);
                    return 0;
                }

                string versionsJson = await client
                    .GetSchemaVersionsAsync(normalizedType, cancellationToken)
                    .ConfigureAwait(false);
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

            byte[] schema = await client
                .GetSchemaAsync(normalizedType, version.Value, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(destination))
            {
                if (tableView is not null)
                {
                    stdout.WriteLine(XboxPartnerTableFormatter.FormatRows(
                        tableView,
                        [
                            [
                                normalizedType,
                                version.Value.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture),
                                "",
                                "",
                                schema.Length.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture),
                            ],
                        ]));
                    return 0;
                }

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

        catch (XboxPartnerApiException ex)
        {
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, compact));
            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    internal static Task<int> RunAsync(
        XboxPartnerClient client,
        string? schemaType,
        int? version,
        string? destination,
        string outputFormat,
        TextWriter stdout,
        TextWriter stderr,
        Stream rawStdout,
        CancellationToken cancellationToken) =>
        RunAsync(
            client,
            schemaType,
            version,
            destination,
            outputFormat.Equals("table", StringComparison.OrdinalIgnoreCase)
                ? OutputOptions.Table(XboxPartnerCommandSupport.SchemaTableView)
                : outputFormat.Equals("json-compact", StringComparison.OrdinalIgnoreCase)
                    ? OutputOptions.JsonCompact
                    : OutputOptions.Json,
            stdout,
            stderr,
            rawStdout,
            cancellationToken);

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
        CancellationToken cancellationToken)
    {
        string? directory = null;
        var savedPaths = new List<string>();
        var tableRows = new List<string[]>();
        foreach (string type in types)
        {
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
