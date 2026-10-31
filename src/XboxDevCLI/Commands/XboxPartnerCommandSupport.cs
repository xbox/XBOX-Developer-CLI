// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Safety;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class XboxPartnerCommandSupport
{
    /// <summary>True when a person can answer a prompt: stdin and stderr are both a terminal.</summary>
    public static bool IsInteractiveTerminal() =>
        Environment.UserInteractive && !Console.IsInputRedirected && !Console.IsErrorRedirected;

    /// <summary>Writes the gateway's correlation ID to stderr, with layout breaking characters removed.</summary>
    public static void WriteCorrelationId(string? value, TextWriter stderr)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            stderr.WriteLine($"{XboxPartnerClient.CorrelationIdHeader}: {XboxPartnerTableFormatter.NormalizeCell(value)}");
        }
    }

    /// <summary>
    /// Reads a string property from a gateway object, returning an empty string when it is
    /// absent, null, or not a string. A non-object entry is rejected outright, as is a missing
    /// value for a <paramref name="required"/> property.
    /// </summary>
    /// <remarks>
    /// Property names are matched exactly, because the gateway emits camelCase and every name
    /// looked up here is a literal in this assembly rather than user input. Matching loosely
    /// would absorb a gateway contract change silently instead of surfacing it. The
    /// <see cref="JsonValueKind"/> check is what keeps a missing or non-string value from
    /// throwing out of <see cref="JsonElement.GetString"/>; both the table formatter and seller
    /// lookup read through here so neither can accept a payload the other rejects.
    /// </remarks>
    public static string ReadString(JsonElement element, string propertyName, bool required = false)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The XboxPartner response contained an entry that was not an object.");
        }

        var text = element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        if (required && text.Length == 0)
        {
            throw new InvalidDataException(
                $"The XboxPartner response contained an entry with no '{propertyName}' value.");
        }

        return text;
    }

    public static Option<string?> CreateBaseUrlOption() =>
        new("--base-url")
        {
            Description = "Backend service base URL. Overrides XboxPartner:BaseUrl configuration.",
        };

    /// <summary>
    /// Resolves the gateway base URL from <c>--base-url</c>, then <c>XboxPartner:BaseUrl</c>
    /// configuration.
    /// </summary>
    /// <remarks>
    /// There is no code fallback: the shipped appsettings.json is the single source of the
    /// gateway URL, so the deployment can be retargeted without a rebuild and the source tree
    /// carries no environment-specific hostname. A missing or blank key is reported as an
    /// actionable error rather than silently falling back to a compiled-in host.
    /// <see cref="XboxPartnerClient.BuildRequestUri"/> still rejects a non-HTTPS URL, since the
    /// request carries a Microsoft Entra access token.
    /// </remarks>
    public static string ResolveBaseUrl(
        ParseResult parseResult,
        Option<string?> baseUrlOption,
        IConfiguration configuration)
    {
        var baseUrl = parseResult.GetValue(baseUrlOption)
            ?? configuration["XboxPartner:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "No XboxPartner gateway URL. Set XboxPartner:BaseUrl in appsettings.json, set "
                + "the XBOX_XBOXPARTNER__BASEURL environment variable, or pass --base-url.");
        }

        _ = XboxPartnerClient.BuildRequestUri(baseUrl, string.Empty);
        var effectiveBaseUrl = baseUrl.TrimEnd('/');
        EndpointOverrideWarning.WriteXboxPartnerWarningIfOverridden(effectiveBaseUrl, Console.Error);
        return effectiveBaseUrl;
    }

    public static string ResolveBaseUrl(
        IConfiguration configuration,
        string serviceName = "XboxPartner gateway",
        bool includeDefaultUrl = true)
    {
        string? baseUrl = configuration["XboxPartner:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                $"No {serviceName} URL. Set XboxPartner:BaseUrl in appsettings.json or set " +
                "the XBOX_XBOXPARTNER__BASEURL environment variable.");
        }

        _ = XboxPartnerClient.BuildRequestUri(baseUrl, string.Empty);
        string effectiveBaseUrl = baseUrl.TrimEnd('/');
        EndpointOverrideWarning.WriteXboxPartnerWarningIfOverridden(
            effectiveBaseUrl,
            Console.Error,
            serviceName,
            includeDefaultUrl);
        return effectiveBaseUrl;
    }

    public static Option<string?> CreateSellerIdOption()
    {
        var option = new Option<string?>("--seller-id")
        {
            Description = "Partner Center seller ID.",
            HelpName = "id",
        }.AsContextValue(ContextEndpoint.XboxPartner);
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
            {
                result.AddError("--seller-id must contain at least one non-whitespace character.");
            }
        });
        return option;
    }

    /// <summary>
    /// The seller the command acts on. It resolves nothing: <see cref="ContextResolver"/> has
    /// already filled in <c>--seller-id</c> from <c>XBOX_SELLER_ID</c> or saved context, before the
    /// command ran. Without one, this fails saying how to supply it.
    /// </summary>
    public static async Task<string> GetSellerIdAsync(ParseResult parseResult, Option<string?> sellerIdOption) =>
        NormalizeSellerId(parseResult.GetValue(sellerIdOption))
        ?? throw new InvalidOperationException(
            await ContextResolver.GetMissingMessageAsync(parseResult, sellerIdOption));

    private static string? NormalizeSellerId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static void WriteSellerOutput(string json, ParseResult parseResult) =>
        WriteOutput(json, parseResult, XboxPartnerTableFormatter.FormatSellers, "seller");

    public static void WriteGameOutput(
        string json,
        ParseResult parseResult,
        TimeSpan elapsed,
        Option<bool> detailsOption)
    {
        if (parseResult.GetValue(detailsOption))
        {
            var details = XboxPartnerGameDetailsFormatter.Format(json);
            Console.WriteLine(details.Text);
            WriteResultCount(details.ResultCount, elapsed);
            return;
        }

        WriteOutput(
            json,
            parseResult,
            XboxPartnerTableFormatter.FormatGamesWithCount,
            "game",
            elapsed,
            ValidateGames);
    }

    private static void WriteOutput(
        string json,
        ParseResult parseResult,
        Func<string, XboxPartnerTableFormatter.TableFormatResult> tableFormatter,
        string resource,
        TimeSpan elapsed,
        Action<JsonElement>? validateRoot = null)
    {
        if (string.Equals(
                parseResult.GetValue(Program.OutputOption),
                "table",
                StringComparison.OrdinalIgnoreCase))
        {
            var table = tableFormatter(json);
            Console.WriteLine(table.Text);
            WriteResultCount(table.ResultCount, elapsed);
            return;
        }

        WriteJsonOutput(json, parseResult, resource, validateRoot);
    }

    private static void WriteResultCount(int resultCount, TimeSpan elapsed) =>
        Console.Error.WriteLine(
            FormattableString.Invariant(
                $"{resultCount} {(resultCount == 1 ? "result" : "results")} found in {elapsed.TotalSeconds:F3}s"));

    private static void WriteOutput(
        string json,
        ParseResult parseResult,
        Func<string, string> tableFormatter,
        string resource,
        Action<JsonElement>? validateRoot = null)
    {
        if (string.Equals(
                parseResult.GetValue(Program.OutputOption),
                "table",
                StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(tableFormatter(json));
            return;
        }

        WriteJsonOutput(json, parseResult, resource, validateRoot);
    }

    private static void WriteJsonOutput(
        string json,
        ParseResult parseResult,
        string resource,
        Action<JsonElement>? validateRoot)
    {
        var compact = string.Equals(
            parseResult.GetValue(Program.OutputOption),
            "json-compact",
            StringComparison.OrdinalIgnoreCase);

        // Program.FormatOutput returns unparseable input unchanged, so an HTML or truncated
        // 2xx body would otherwise reach stdout with a success exit code.
        try
        {
            using var document = JsonDocument.Parse(json);
            validateRoot?.Invoke(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException(
                $"The XboxPartner {resource} response was not valid JSON.",
                exception);
        }

        Console.WriteLine(Program.FormatOutput(json, compact, dataOnly: false));
    }

    internal static JsonDocument ParseGameResponse(string response)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(response);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException(
                "The XboxPartner game response was not valid JSON.",
                exception);
        }

        try
        {
            ValidateGames(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void ValidateGames(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("games", out var games)
            || games.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The XboxPartner game response must contain a games array.");
        }

        foreach (var game in games.EnumerateArray())
        {
            _ = ReadString(game, "productId");
            XboxPartnerTableFormatter.ValidateTitleId(game);
        }
    }

    public static void WriteGatewayError(
        XboxPartnerApiException exception,
        ParseResult parseResult)
    {
        Console.Error.WriteLine(XboxPartnerErrorGuidance.Describe(
            exception, Program.GetAuthentication(parseResult).IsAutomationSelected));
        if (parseResult.GetValue(Program.VerboseOption)
            && !string.IsNullOrWhiteSpace(exception.ResponseBody))
        {
            var compact = string.Equals(
                parseResult.GetValue(Program.OutputOption),
                "json-compact",
                StringComparison.OrdinalIgnoreCase);
            Console.Error.WriteLine(TerminalText.MultiLine(FormatIfJson(exception.ResponseBody, compact)));
        }
    }

    public static int WriteLocalError(Exception exception)
    {
        Console.Error.WriteLine(TerminalText.MultiLine(exception.Message));
        return 1;
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is an expected gateway, authentication, or local
    /// failure that <see cref="WriteRequestError"/> reports, rather than a bug to let surface.
    /// </summary>
    public static bool IsRequestError(Exception exception) =>
        exception is XboxPartnerApiException or XboxPartnerResponseException
            or OperationCanceledException or MsalException or HttpRequestException
            or InvalidOperationException or InvalidDataException or IOException or ArgumentException
            or JsonException or UnauthorizedAccessException;

    public static void WriteRequestError(
        Exception exception, ParseResult result, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case XboxPartnerApiException api:
                WriteCorrelationId(api.CorrelationId, Console.Error);
                WriteGatewayError(api, result);
                break;
            case XboxPartnerResponseException response:
                WriteCorrelationId(response.CorrelationId, Console.Error);
                Console.Error.WriteLine($"Request failed: {TerminalText.SingleLine(response.Message)}");
                Console.Error.WriteLine(TerminalText.MultiLine(Program.FormatOutput(
                    response.RawResponse,
                    string.Equals(result.GetValue(Program.OutputOption), "json-compact", StringComparison.OrdinalIgnoreCase),
                    dataOnly: false)));
                break;
            case OperationCanceledException:
                Console.Error.WriteLine(cancellationToken.IsCancellationRequested
                    ? "Request canceled." : "Request timed out.");
                break;
            default:
                WriteLocalError(exception);
                break;
        }
    }

    /// <summary>
    /// Removes control characters from server-supplied text, such as a job ID or correlation ID,
    /// before it is written to the terminal, so the gateway cannot inject escape sequences or
    /// forge additional output lines.
    /// </summary>
    public static string ForTerminal(string? value) => TerminalText.SingleLine(value);

    /// <summary>
    /// Builds a copy-pasteable follow-up command that keeps the caller's configuration, identity,
    /// seller, and gateway selections.
    /// </summary>
    public static string BuildRecoveryCommand(
        ParseResult result, string command, string sellerId, string baseUrl)
    {
        var arguments = RecoveryCommandArguments.Create(result);
        arguments.Add(command);
        arguments.Add($"--seller-id {ShellQuoting.Quote(sellerId)} --base-url {ShellQuoting.Quote(baseUrl)}");
        return string.Join(' ', arguments);
    }

    public static T DeserializeResponse<T>(string response, string resource, string? serviceName = "XboxPartner")
    {
        try
        {
            return JsonSerializer.Deserialize<T>(
                    response,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("The response was null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The {(serviceName is null ? resource : $"{serviceName} {resource}")} response was not valid JSON.",
                exception);
        }
    }

    /// <summary>
    /// The shape of a seller <em>list</em> response, declared so table support is discoverable
    /// from metadata like every other command's.
    /// </summary>
    /// <remarks>
    /// Seller commands read their rows through <see cref="XboxPartnerTableFormatter"/> rather
    /// than the shared reader, because they are gateway responses rather than PlayFab envelopes.
    /// The layout is no longer separate: that formatter extracts the cells and hands them to
    /// <c>TableFormatter.Render</c>, so this declaration and every other table are sized, padded,
    /// and truncated by the same code.
    /// </remarks>
    public static TableView SellerTableView { get; } = new(
        "sellers",
        [
            new CommandLine.TableColumn("SELLER ID", "sellerId", 24),
            new CommandLine.TableColumn("NAME", "name", 60),
        ]);

    /// <summary>The bounded table view for grouped game associations.</summary>
    public static TableView GameTableView { get; } = new(
        "games",
        [
            new CommandLine.TableColumn("TITLE ID", "xboxTitleId", 10),
            new CommandLine.TableColumn("STORE IDS", "storeId", 32),
            new CommandLine.TableColumn("PRODUCT NAME", "name", 40),
            new CommandLine.TableColumn("MSA APP ID", "msaAppId", 20),
            new CommandLine.TableColumn("PACKAGE FAMILY NAME", "packageFamilyName", 56),
        ]);

    /// <summary>The view for <c>sandbox list</c>, read from the CLI's <c>sandboxes</c> wrapper.</summary>
    public static TableView SandboxTableView { get; } = new(
        "sandboxes",
        [
            new CommandLine.TableColumn(
                "SANDBOX",
                JsonNamingPolicy.CamelCase.ConvertName(nameof(XboxPartnerSandboxSummary.SandboxId)),
                64),
            new CommandLine.TableColumn(
                "TYPE",
                JsonNamingPolicy.CamelCase.ConvertName(nameof(XboxPartnerSandboxSummary.Type)),
                10),
            new CommandLine.TableColumn(
                "GLOBAL",
                JsonNamingPolicy.CamelCase.ConvertName(nameof(XboxPartnerSandboxSummary.Global)),
                6),
        ]);

    /// <summary>The one-row view for <c>sandbox get</c>.</summary>
    public static TableView SandboxRecordTableView { get; } = TableView.Record(
        [
            new CommandLine.TableColumn(
                "SANDBOX ID",
                JsonNamingPolicy.CamelCase.ConvertName(nameof(XboxPartnerSandbox.SandboxId)),
                24),
            new CommandLine.TableColumn(
                "TYPE",
                JsonNamingPolicy.CamelCase.ConvertName(nameof(XboxPartnerSandbox.Type)),
                10),
            new CommandLine.TableColumn(
                "SELLER ID",
                JsonNamingPolicy.CamelCase.ConvertName(nameof(XboxPartnerSandbox.SellerId)),
                24),
        ]);

    public static TableView ConfigurationTableView { get; } = new(
        "configurations",
        CreateConfigurationColumns());

    public static TableView ConfigurationRecordTableView { get; } = TableView.Record(
        CreateConfigurationColumns());

    private static IReadOnlyList<CommandLine.TableColumn> CreateConfigurationColumns() =>
        [
            new CommandLine.TableColumn(
                "SANDBOX",
                JsonNamingPolicy.CamelCase.ConvertName(
                    nameof(XboxLiveConfigurationSummary.Sandbox)),
                24),
            new CommandLine.TableColumn(
                "WORKING",
                JsonNamingPolicy.CamelCase.ConvertName(
                    nameof(XboxLiveConfigurationSummary.WorkingVersion)),
                12),
            new CommandLine.TableColumn(
                "WORKING COMMIT",
                JsonNamingPolicy.CamelCase.ConvertName(
                    nameof(XboxLiveConfigurationSummary.WorkingCommitTime)),
                22),
            new CommandLine.TableColumn(
                "PUBLISHED",
                JsonNamingPolicy.CamelCase.ConvertName(
                    nameof(XboxLiveConfigurationSummary.PublishedVersion)),
                12),
            new CommandLine.TableColumn(
                "PUBLISHED COMMIT",
                JsonNamingPolicy.CamelCase.ConvertName(
                    nameof(XboxLiveConfigurationSummary.PublishedCommitTime)),
                22),
            new CommandLine.TableColumn(
                "UNPUBLISHED",
                JsonNamingPolicy.CamelCase.ConvertName(
                    nameof(XboxLiveConfigurationSummary.HasUnpublishedChanges)),
                11),
        ];

    /// <summary>
    /// A shared view for schema discovery, downloads, and saved-file summaries.
    /// Columns that do not apply to the selected mode are left blank.
    /// </summary>
    public static TableView SchemaTableView { get; } = new(
        "schemas",
        [
            new CommandLine.TableColumn("TYPE", "type", 24),
            new CommandLine.TableColumn("VERSION", "version", 9),
            new CommandLine.TableColumn("NAMESPACE", "namespace", 36),
            new CommandLine.TableColumn("PATH", "path", 60),
            new CommandLine.TableColumn("BYTES", "bytes", 12),
        ]);

    /// <summary>The one-row summary produced after extracting a document archive.</summary>
    public static TableView DocumentDownloadTableView { get; } = TableView.Record(
        [
            new CommandLine.TableColumn("TYPE", "type", 10),
            new CommandLine.TableColumn("SANDBOX", "sandbox", 24),
            new CommandLine.TableColumn("VIEW", "view", 12),
            new CommandLine.TableColumn("VERSION", "version", 12),
            new CommandLine.TableColumn("FILES", "files", 8),
            new CommandLine.TableColumn("DESTINATION", "destination", 60),
        ]);

    public static TableView DocumentCommitTableView { get; } = TableView.Record(
        [
            new CommandLine.TableColumn("TYPE", "type", 10),
            new CommandLine.TableColumn("SANDBOX", "sandbox", 24),
            new CommandLine.TableColumn("VALIDATE ONLY", "validateOnly", 13),
            new CommandLine.TableColumn("CAN COMMIT", "canCommit", 10),
            new CommandLine.TableColumn("COMMITTED", "committed", 10),
            new CommandLine.TableColumn("FILES", "files", 8),
            new CommandLine.TableColumn("ETAG FILE", "etagFile", 60),
        ]);

    public static TableView PublishTableView { get; } = TableView.Record(
        [
            new CommandLine.TableColumn("STATUS", "status", 16),
            new CommandLine.TableColumn("SOURCE", "source", 24),
            new CommandLine.TableColumn("DESTINATION", "destination", 24),
            new CommandLine.TableColumn("VALIDATE ONLY", "validateOnly", 13),
            new CommandLine.TableColumn("SOURCE VERSION", "sourceVersion", 14),
        ]);

    public static TableView PublishStatusTableView { get; } = TableView.Record(
        [
            new CommandLine.TableColumn("STATUS", "status", 16),
            new CommandLine.TableColumn("SANDBOX", "sandbox", 24),
        ]);

    /// <summary>
    /// A table column paired with the cell it shows. Views and rows are both derived from one
    /// list of these, so reordering or adding a column cannot put a value under the wrong header.
    /// </summary>
    internal sealed record TableField<T>(CommandLine.TableColumn Column, Func<T, string> Cell);

    /// <summary>The <c>test-account batch list</c> columns, newest batch first.</summary>
    internal static IReadOnlyList<TableField<TestAccountBatch>> TestAccountBatchFields { get; } =
    [
        new(
            new("BATCH ID", Camel(nameof(TestAccountBatch.Id)), 20),
            batch => batch.Id.ToString(CultureInfo.InvariantCulture)),
        new(
            new("EMAIL TEMPLATE", Camel(nameof(TestAccountBatch.EmailTemplate)), 40),
            batch => TestAccountText(batch.EmailTemplate)),
        new(
            new("SIZE", Camel(nameof(TestAccountBatch.BatchSize)), 6),
            batch => batch.BatchSize.ToString(CultureInfo.InvariantCulture)),
        new(
            new("STATE", Camel(nameof(TestAccountBatch.IsComplete)), 8),
            batch => TestAccountState(batch.IsComplete)),
        new(
            new("CREATED BY", Camel(nameof(TestAccountBatch.CreatedBy)), 20),
            batch => batch.CreatedBy.ToString(CultureInfo.InvariantCulture)),
        new(
            new("CREATED (UTC)", Camel(nameof(TestAccountBatch.CreationDate)), 16),
            batch => TestAccountDate(batch.CreationDate)),
        new(
            new("EXPIRES (UTC)", Camel(nameof(TestAccountBatch.AccessExpirationDate)), 16),
            batch => TestAccountDate(batch.AccessExpirationDate)),
    ];

    /// <summary>Appended to the batch list table by <c>--include-sandboxes</c>.</summary>
    /// <remarks>
    /// Deliberately left out of <see cref="TestAccountBatchesView"/>: the view is what metadata
    /// and help advertise, and this column only exists when the option is set. It reads a list,
    /// which the column rules otherwise forbid; that is safe here because its cell joins the list
    /// into one string rather than letting the renderer print raw JSON.
    /// </remarks>
    internal static TableField<TestAccountBatch> TestAccountSandboxesField { get; } = new(
        new("SANDBOXES", Camel(nameof(TestAccountBatch.SandboxAccess)), 60),
        batch => TestAccountList(batch.SandboxAccess));

    public static TableView TestAccountBatchesView { get; } = new(
        Camel(nameof(TestAccountBatchesResponse.Batches)),
        [.. TestAccountBatchFields.Select(field => field.Column)]);

    /// <summary>The accounts of one batch for <c>test-account batch get</c>.</summary>
    internal static IReadOnlyList<TableField<TestAccount>> TestAccountFields { get; } =
    [
        new(new("EMAIL", Camel(nameof(TestAccount.Email)), 60), account => TestAccountText(account.Email)),
        new(new("GAMERTAG", Camel(nameof(TestAccount.Gamertag)), 20), account => TestAccountText(account.Gamertag)),
        new(new("XUID", Camel(nameof(TestAccount.Xuid)), 20), account => TestAccountText(account.Xuid)),
        new(new("DELETED", Camel(nameof(TestAccount.IsDeleted)), 7), account => account.IsDeleted ? "yes" : "no"),
    ];

    /// <summary>
    /// The accounts of one batch for <c>test-account batch get</c>. The batch itself is summarized
    /// on stderr.
    /// </summary>
    public static TableView TestAccountBatchView { get; } = new(
        Camel(nameof(TestAccountBatchResponse.Accounts)),
        [.. TestAccountFields.Select(field => field.Column)]);

    internal static string TestAccountText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    /// <summary>The one row shown for a test account creation job.</summary>
    internal static IReadOnlyList<TableField<TestAccountJob>> TestAccountJobFields { get; } =
    [
        new(
            new("BATCH ID", Camel(nameof(TestAccountJob.BatchId)), 20),
            job => job.BatchId.ToString(CultureInfo.InvariantCulture)),
        new(new("JOB ID", Camel(nameof(TestAccountJob.JobId)), 40), job => TestAccountText(job.JobId)),
        new(new("STATE", Camel(nameof(TestAccountJob.State)), 10), job => TestAccountText(job.State)),
        new(
            new("TOTAL", Camel(nameof(TestAccountJob.Total)), 6),
            job => job.Total.ToString(CultureInfo.InvariantCulture)),
        new(
            new("SUCCEEDED", Camel(nameof(TestAccountJob.Succeeded)), 9),
            job => job.Succeeded.ToString(CultureInfo.InvariantCulture)),
        new(
            new("FAILED", Camel(nameof(TestAccountJob.Failed)), 6),
            job => job.Failed.ToString(CultureInfo.InvariantCulture)),
    ];

    public static TableView TestAccountJobView { get; } = TableView.Record(
        [.. TestAccountJobFields.Select(field => field.Column)]);

    /// <summary>The one row shown for a test account batch delete.</summary>
    internal static IReadOnlyList<TableField<TestAccountBatchDeletion>> TestAccountBatchDeletionFields { get; } =
    [
        new(
            new("BATCH ID", Camel(nameof(TestAccountBatchDeletion.BatchId)), 20),
            deletion => deletion.BatchId.ToString(CultureInfo.InvariantCulture)),
        new(
            new("JOB ID", Camel(nameof(TestAccountBatchDeletion.JobId)), 40),
            deletion => TestAccountText(deletion.JobId)),
        new(
            new("STATE", Camel(nameof(TestAccountBatchDeletion.State)), 10),
            deletion => TestAccountText(deletion.State)),
        new(
            new("EMAIL TEMPLATE", Camel(nameof(TestAccountBatchDeletion.EmailTemplate)), 40),
            deletion => TestAccountText(deletion.EmailTemplate)),
        new(
            new("SIZE", Camel(nameof(TestAccountBatchDeletion.BatchSize)), 6),
            deletion => deletion.BatchSize.ToString(CultureInfo.InvariantCulture)),
    ];

    public static TableView TestAccountBatchDeletionView { get; } = TableView.Record(
        [.. TestAccountBatchDeletionFields.Select(field => field.Column)]);

    /// <summary>
    /// The one row shown for a test account batch update. Sandboxes and keywords are lists, so they
    /// go to stderr notes rather than columns.
    /// </summary>
    internal static IReadOnlyList<TableField<TestAccountBatchUpdate>> TestAccountBatchUpdateFields { get; } =
    [
        new(
            new("BATCH ID", Camel(nameof(TestAccountBatchUpdate.BatchId)), 20),
            update => update.BatchId.ToString(CultureInfo.InvariantCulture)),
        new(
            new("JOB ID", Camel(nameof(TestAccountBatchUpdate.JobId)), 40),
            update => TestAccountText(update.JobId)),
        new(
            new("STATE", Camel(nameof(TestAccountBatchUpdate.State)), 10),
            update => TestAccountText(update.State)),
        new(
            new("EXPIRES (UTC)", Camel(nameof(TestAccountBatchUpdate.AccessExpirationDate)), 16),
            update => TestAccountDate(update.AccessExpirationDate)),
    ];

    public static TableView TestAccountBatchUpdateView { get; } = TableView.Record(
        [.. TestAccountBatchUpdateFields.Select(field => field.Column)]);

    internal static IReadOnlyList<TableField<TestAccountLookupResult>> TestAccountLookupFields { get; } =
    [
        new(
            new("EMAIL", $"{Camel(nameof(TestAccountLookupResult.Account))}.{Camel(nameof(TestAccount.Email))}", 60),
            result => TestAccountText(result.Account.Email)),
        new(
            new("GAMERTAG", $"{Camel(nameof(TestAccountLookupResult.Account))}.{Camel(nameof(TestAccount.Gamertag))}", 20),
            result => TestAccountText(result.Account.Gamertag)),
        new(
            new("BATCH ID", $"{Camel(nameof(TestAccountLookupResult.Batch))}.{Camel(nameof(TestAccountBatch.Id))}", 20),
            result => result.Batch.Id.ToString(CultureInfo.InvariantCulture)),
        new(
            new("EMAIL TEMPLATE", $"{Camel(nameof(TestAccountLookupResult.Batch))}.{Camel(nameof(TestAccountBatch.EmailTemplate))}", 40),
            result => TestAccountText(result.Batch.EmailTemplate)),
        new(
            new("EXPIRES (UTC)", $"{Camel(nameof(TestAccountLookupResult.Batch))}.{Camel(nameof(TestAccountBatch.AccessExpirationDate))}", 16),
            result => TestAccountDate(result.Batch.AccessExpirationDate)),
        new(
            new("EXPIRED", Camel(nameof(TestAccountLookupResult.Expired)), 7),
            result => result.Expired ? "yes" : "no"),
    ];

    public static TableView TestAccountLookupView { get; } = TableView.Record(
        [.. TestAccountLookupFields.Select(field => field.Column)]);

    internal static string TestAccountState(bool isComplete) => isComplete ? "complete" : "pending";

    internal static string TestAccountList(IReadOnlyList<string>? values) =>
        values is null || values.Count == 0 ? "-" : string.Join(", ", values);

    private static string TestAccountDate(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";

    private static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    public static CommandMetadata ReadOnlyMetadata(
        string scope,
        string output,
        IReadOnlyList<string> examples,
        string endpoint,
        TableView? table = null,
        IReadOnlyList<string>? learnMore = null) =>
        new(
            Safety: CommandSafety.ReadOnly,
            Scope: scope,
            Output: output,
            Examples: examples,
            LearnMore: learnMore ?? [$"Calls the XboxPartner gateway: GET {endpoint}"],
            Table: table);

    private static string FormatIfJson(string value, bool compact)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return Program.FormatOutput(value, compact, dataOnly: false);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return value;
        }
    }
}
