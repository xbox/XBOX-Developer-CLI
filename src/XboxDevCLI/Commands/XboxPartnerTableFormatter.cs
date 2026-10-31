// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class XboxPartnerTableFormatter
{
    public static string FormatConfigurations(string responseBody)
    {
        IReadOnlyList<XboxLiveConfigurationSummary> configurations;
        try
        {
            configurations =
                JsonSerializer.Deserialize<IReadOnlyList<XboxLiveConfigurationSummary>>(
                    responseBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("The response was null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The XboxPartner configuration response was not a valid JSON array.",
                exception);
        }

        var rows = configurations.Select(CreateConfigurationRow);
        return Render(XboxPartnerCommandSupport.ConfigurationTableView, rows);
    }

    public static string FormatConfiguration(string responseBody)
    {
        XboxLiveConfigurationSummary configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<XboxLiveConfigurationSummary>(
                responseBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("The response was null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The XboxPartner configuration response was not a valid JSON object.",
                exception);
        }

        return Render(
            XboxPartnerCommandSupport.ConfigurationRecordTableView,
            [CreateConfigurationRow(configuration)]);
    }

    public static string FormatSellers(string responseBody)
    {
        using var document = Parse(responseBody, "seller");
        return FormatItems(document.RootElement, XboxPartnerCommandSupport.SellerTableView);
    }

    public static string FormatGames(string responseBody) =>
        FormatGamesWithCount(responseBody).Text;

    public static string FormatRows(TableView view, IEnumerable<string[]> rows) =>
        Render(view, rows);

    public static TableFormatResult FormatGamesWithCount(string responseBody)
    {
        using var document = Parse(responseBody, "game");
        var games = GetItems(document.RootElement, "games");
        var rows = games
            .Select((game, index) => new { Game = game, Index = index })
            .GroupBy(
                item =>
                {
                    var titleId = ReadTitleId(item.Game);
                    return titleId is not null
                        ? $"title:{titleId}"
                        : $"game:{item.Index}";
                },
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var gamesForTitle = group.Select(item => item.Game).ToArray();
                return new[]
                {
                    ReadTitleId(gamesForTitle[0]) ?? string.Empty,
                    JoinDistinct(gamesForTitle, "storeId"),
                    JoinDistinct(gamesForTitle, "name"),
                    JoinDistinct(gamesForTitle, "msaAppId"),
                    JoinDistinct(gamesForTitle, "packageFamilyName"),
                };
            })
            .OrderBy(row => row[0], StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row[1], StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row[2], StringComparer.OrdinalIgnoreCase);

        var projectedRows = rows.ToArray();
        return new(
            projectedRows.Length == 0
                ? TableFormatter.EmptyNotice
                : Render(XboxPartnerCommandSupport.GameTableView, projectedRows),
            games.Count);
    }

    internal static void ValidateTitleId(JsonElement game) => _ = ReadTitleId(game);

    internal static string? ReadTitleId(JsonElement game)
    {
        if (game.ValueKind != JsonValueKind.Object
            || !game.TryGetProperty("xboxTitleId", out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: 8 } titleId
            && titleId.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F'))
        {
            return titleId;
        }

        throw new InvalidDataException(
            "The XboxPartner game response contained an invalid 'xboxTitleId' value.");
    }

    /// <summary>
    /// Table text for stdout plus summary notes the command writes to stderr, so stdout holds
    /// only the requested table.
    /// </summary>
    internal sealed record TestAccountTable(string Text, IReadOnlyList<string> Notes);

    public static TestAccountTable FormatTestAccountBatches(string responseBody, TestAccountBatchFilter filter, bool expired = false)
    {
        ArgumentNullException.ThrowIfNull(filter);
        TestAccountBatchesResponse response = XboxPartnerCommandSupport.DeserializeResponse<TestAccountBatchesResponse>(
            responseBody,
            "test account batch list",
            serviceName: null);
        if (response.Batches is null || response.Batches.Any(batch => batch is null))
        {
            throw new InvalidDataException(
                "The test account batch list response must contain a 'batches' array.");
        }

        IReadOnlyList<XboxPartnerCommandSupport.TableField<TestAccountBatch>> fields = filter.IncludeSandboxes
            ? [.. XboxPartnerCommandSupport.TestAccountBatchFields, XboxPartnerCommandSupport.TestAccountSandboxesField]
            : XboxPartnerCommandSupport.TestAccountBatchFields;
        TableView view = XboxPartnerCommandSupport.TestAccountBatchesView with
        {
            Columns = [.. fields.Select(field => field.Column)],
        };
        var rows = response.Batches.Select(batch => fields.Select(field => field.Cell(batch)).ToArray());
        string kind = expired ? "expired " : string.Empty;
        return new(Render(view, rows), [FormattableString.Invariant($"Returned {response.Batches.Count} {kind}batches.")]);
    }

    public static TestAccountTable FormatTestAccountLookup(string responseBody)
    {
        var result = XboxPartnerCommandSupport.DeserializeResponse<TestAccountLookupResult>(
            responseBody,
            "test account lookup",
            serviceName: null);
        var fields = XboxPartnerCommandSupport.TestAccountLookupFields;
        var rows = new[] { fields.Select(field => field.Cell(result)).ToArray() };
        return new(Render(XboxPartnerCommandSupport.TestAccountLookupView, rows), []);
    }

    public static TestAccountTable FormatTestAccountBatch(string responseBody)
    {
        TestAccountBatchResponse response = XboxPartnerCommandSupport.DeserializeResponse<TestAccountBatchResponse>(
            responseBody,
            "test account batch",
            serviceName: null);
        if (response.Batch is null ||
            response.Accounts is null ||
            response.Accounts.Any(account => account is null))
        {
            throw new InvalidDataException(
                "The test account batch response must contain a 'batch' object and an " +
                "'accounts' array.");
        }

        TestAccountBatch batch = response.Batch;
        var notes = new List<string>
        {
            FormattableString.Invariant(
                $"Batch {batch.Id} ({NormalizeCell(XboxPartnerCommandSupport.TestAccountText(batch.EmailTemplate))}): {XboxPartnerCommandSupport.TestAccountState(batch.IsComplete)}, {response.Accounts.Count} accounts returned of batch size {batch.BatchSize}."),
        };
        if (batch.SandboxAccess is { Count: > 0 })
        {
            notes.Add($"Sandbox access: {NormalizeCell(XboxPartnerCommandSupport.TestAccountList(batch.SandboxAccess))}");
        }

        if (!batch.IsComplete && !string.IsNullOrWhiteSpace(batch.Progress))
        {
            notes.Add($"Progress: {NormalizeCell(batch.Progress)}");
        }

        var fields = XboxPartnerCommandSupport.TestAccountFields;
        var rows = response.Accounts
            .OrderBy(account => account.Email, StringComparer.OrdinalIgnoreCase)
            .Select(account => fields.Select(field => field.Cell(account)).ToArray());
        return new(Render(XboxPartnerCommandSupport.TestAccountBatchView, rows), notes);
    }

    /// <summary>
    /// One row for a test account creation job. The error code and progress go to stderr notes,
    /// since they are long free text that would not fit a row.
    /// </summary>
    public static TestAccountTable FormatTestAccountJob(TestAccountJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.Progress))
        {
            notes.Add($"Progress: {NormalizeCell(job.Progress)}");
        }

        if (!string.IsNullOrWhiteSpace(job.ErrorCode) || !string.IsNullOrWhiteSpace(job.ErrorMessage))
        {
            notes.Add(
                $"Error: {NormalizeCell(XboxPartnerCommandSupport.TestAccountText(job.ErrorCode))}: " +
                NormalizeCell(XboxPartnerCommandSupport.TestAccountText(job.ErrorMessage)));
        }

        var fields = XboxPartnerCommandSupport.TestAccountJobFields;
        return new(
            Render(XboxPartnerCommandSupport.TestAccountJobView, [fields.Select(field => field.Cell(job)).ToArray()]),
            notes);
    }

    /// <summary>One row for a test account batch delete.</summary>
    public static TestAccountTable FormatTestAccountBatchDeletion(TestAccountBatchDeletion deletion)
    {
        ArgumentNullException.ThrowIfNull(deletion);
        var fields = XboxPartnerCommandSupport.TestAccountBatchDeletionFields;
        return new(
            Render(
                XboxPartnerCommandSupport.TestAccountBatchDeletionView,
                [fields.Select(field => field.Cell(deletion)).ToArray()]),
            []);
    }

    /// <summary>One row for a test account batch update, with its sandboxes and keywords as stderr notes.</summary>
    public static TestAccountTable FormatTestAccountBatchUpdate(TestAccountBatchUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        string[] notes =
        [
            $"Sandboxes: {NormalizeCell(XboxPartnerCommandSupport.TestAccountList(update.Sandboxes))}",
            $"Keywords: {NormalizeCell(XboxPartnerCommandSupport.TestAccountList(update.Keywords))}",
        ];
        var fields = XboxPartnerCommandSupport.TestAccountBatchUpdateFields;
        return new(
            Render(
                XboxPartnerCommandSupport.TestAccountBatchUpdateView,
                [fields.Select(field => field.Cell(update)).ToArray()]),
            notes);
    }

    private static string FormatVersion(ulong? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string FormatTime(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString(
            "yyyy-MM-dd HH:mm:ss 'Z'",
            CultureInfo.InvariantCulture) ?? string.Empty;

    private static string[] CreateConfigurationRow(
        XboxLiveConfigurationSummary configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Sandbox))
        {
            throw new InvalidDataException(
                "The XboxPartner configuration response contained an invalid sandbox.");
        }

        return
        [
            configuration.Sandbox,
            FormatVersion(configuration.WorkingVersion),
            FormatTime(configuration.WorkingCommitTime),
            FormatVersion(configuration.PublishedVersion),
            FormatTime(configuration.PublishedCommitTime),
            configuration.HasUnpublishedChanges ? "Yes" : "No",
        ];
    }

    private static string JoinDistinct(IEnumerable<JsonElement> games, string propertyName) =>
        string.Join(
            ", ",
            games
                .Select(game => XboxPartnerCommandSupport.ReadString(game, propertyName))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    private static JsonDocument Parse(string responseBody, string resource)
    {
        try
        {
            return JsonDocument.Parse(responseBody);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException(
                $"The XboxPartner {resource} response was not valid JSON.",
                exception);
        }
    }

    private static IReadOnlyList<JsonElement> GetItems(
        JsonElement root,
        string collectionProperty)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The XboxPartner response must be a JSON object.");
        }

        if (!root.TryGetProperty(collectionProperty, out var collection)
            || collection.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"The XboxPartner response property '{collectionProperty}' must be an array.");
        }

        var items = collection.EnumerateArray().ToArray();
        if (Array.Exists(items, item => item.ValueKind != JsonValueKind.Object))
        {
            throw new InvalidDataException(
                $"The XboxPartner response property '{collectionProperty}' must contain only objects.");
        }

        return items;
    }

    private static string FormatItems(JsonElement root, TableView view)
    {
        var sellers = GetItems(root, view.Collection);
        var rows = sellers.Select(seller => view.Columns
            .Select(column => XboxPartnerCommandSupport.ReadString(
                seller,
                column.Property,
                required: column.Property == "sellerId"))
            .ToArray());

        return Render(view, rows);
    }

    /// <summary>
    /// Sanitizes extracted rows and lays them out with the PlayFab renderer.
    /// </summary>
    /// <remarks>
    /// Every gateway surface ends here, so the layout and the sanitizing exist once. Only the
    /// extraction differs between them, and when the arithmetic was written twice the seller
    /// tables were free to drift from every other table one fix at a time — which is exactly what
    /// measuring widths in terminal cells would otherwise have done. A surface that renders
    /// itself also silently skips <see cref="NormalizeCell"/>, so routing through here is what
    /// keeps a gateway-supplied value from breaking the columns it is printed in.
    /// </remarks>
    private static string Render(TableView view, IEnumerable<string[]> rows)
    {
        var normalized = rows
            .Select(row => row.Select(NormalizeCell).ToArray())
            .ToArray();

        return normalized.Length == 0
            ? TableFormatter.EmptyNotice
            : TableFormatter.Render(view.Columns, normalized);
    }

    // Uses the same sanitizer as PlayFab table output so gateway-supplied names cannot break or
    // visually reorder the fixed columns. Trimmed as well, because a gateway name padded with
    // spaces would otherwise be sized and aligned as though the padding were content.
    internal static string NormalizeCell(string value) =>
        TableFormatter.NormalizeCell(value).Trim();

    internal sealed record TableFormatResult(string Text, int ResultCount);
}
