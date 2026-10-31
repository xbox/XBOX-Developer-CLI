// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// The answer to "is this account's batch expired?": the account, the batch that holds it, and
/// whether the batch's access expiry has passed.
/// </summary>
/// <remarks>
/// Expired means the batch's sandbox access expiry passed, the only expiry the test account
/// service reports. Mirrors the JSON the command writes, so table columns can use <c>nameof</c>.
/// </remarks>
public sealed record TestAccountLookupResult(
    TestAccount Account,
    TestAccountBatch Batch,
    bool Expired,
    DateTimeOffset? ExpiredOn);

/// <summary>What a lookup found; see <see cref="TestAccountLookup.FindAsync"/>.</summary>
internal sealed record TestAccountLookupOutcome(
    IReadOnlyList<TestAccountLookupResult> Matches,
    int CandidateCount,
    int BatchesRead);

/// <summary>An email address and the batch email templates that could hold it.</summary>
internal sealed record TestAccountEmail(string Address, IReadOnlyList<string> Templates);

/// <summary>
/// Finds the batch that holds a test account. XBOX names accounts
/// <c>{prefix}-{template}{5 digit index}@xboxtest.com</c> and a batch's email template is
/// usually <c>{prefix}-{template}</c>, so the address leads to the batches that could hold it, and
/// each candidate batch's accounts are then read to confirm.
/// </summary>
/// <remarks>
/// Some batches store the template without the prefix (<c>-{template}</c>) although their accounts
/// carry it, so XboxPartner's <c>emailPrefix</c> filter cannot find them. The lookup therefore lists
/// every batch and matches both forms itself.
/// </remarks>
internal static partial class TestAccountLookup
{
    internal const string Domain = "xboxtest.com";
    internal const int IndexDigits = 5;

    /// <summary>The most candidate batches read for one lookup.</summary>
    internal const int MaxCandidates = 10;

    /// <summary>The longest local part an email address may have.</summary>
    internal const int MaxLocalLength = 64;

    /// <summary>
    /// Microsoft account names: ASCII letters, digits, '_', '-' and single dots, starting with a
    /// letter or digit and not ending with a dot.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9_-]|\.(?!\.))*(?<!\.)$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPartPattern();

    /// <summary>Returns the problem with <paramref name="value"/>, or null and its parts.</summary>
    internal static string? TryParse(string? value, out TestAccountEmail? email)
    {
        email = null;
        string address = value?.Trim() ?? string.Empty;
        int at = address.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != address.LastIndexOf('@') || address.Any(char.IsWhiteSpace))
        {
            return $"email must look like ABCD-tester00000@{Domain}.";
        }

        if (!string.Equals(address[(at + 1)..], Domain, StringComparison.OrdinalIgnoreCase))
        {
            return $"email must end with @{Domain}, the domain of XBOX test accounts.";
        }

        string local = address[..at];
        if (local.Length > MaxLocalLength || !LocalPartPattern().IsMatch(local))
        {
            return $"the part before @{Domain} may contain only letters, digits, '_', '-' and single " +
                "dots, must start with a letter or digit, must not end with a dot, and must be at most " +
                FormattableString.Invariant($"{MaxLocalLength} characters.");
        }

        var bases = new List<string>();
        if (local.Length > IndexDigits &&
            local[^IndexDigits..].All(char.IsAsciiDigit))
        {
            bases.Add(local[..^IndexDigits]);
        }

        bases.Add(local);
        var templates = new List<string>(bases);
        foreach (string template in bases)
        {
            int hyphen = template.IndexOf('-', StringComparison.Ordinal);
            if (hyphen > 0)
            {
                templates.Add(template[hyphen..]);
            }
        }

        email = new TestAccountEmail(address, templates);
        return null;
    }

    /// <summary>
    /// Lists the seller's batches whose template could hold the email, then reads each (newest
    /// first, up to <see cref="MaxCandidates"/>) for the account. Every candidate is read, so an
    /// address found in more than one batch is reported rather than hidden.
    /// </summary>
    internal static async Task<TestAccountLookupOutcome> FindAsync(
        XboxPartnerClient client,
        long? sellerId,
        TestAccountEmail email,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string listJson = await client.ListTestAccountBatchesAsync(
            sellerId,
            new TestAccountBatchFilter(),
            cancellationToken).ConfigureAwait(false);
        var list = XboxPartnerCommandSupport.DeserializeResponse<TestAccountBatchesResponse>(
            listJson,
            "test account batch list",
            serviceName: null);
        if (list.Batches is null || list.Batches.Any(batch => batch is null))
        {
            throw new InvalidDataException(
                "The test account batch list response must contain a 'batches' array.");
        }

        var candidates = list.Batches
            .Where(batch => email.Templates.Contains(batch.EmailTemplate ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var matches = new List<TestAccountLookupResult>();
        int read = 0;
        foreach (TestAccountBatch candidate in candidates.Take(MaxCandidates))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string json = await client.GetTestAccountBatchAsync(sellerId, candidate.Id, cancellationToken)
                .ConfigureAwait(false);
            var detail = XboxPartnerCommandSupport.DeserializeResponse<TestAccountBatchResponse>(
                json,
                "test account batch",
                serviceName: null);
            if (detail.Batch is null || detail.Accounts is null || detail.Accounts.Any(account => account is null))
            {
                throw new InvalidDataException(
                    "The test account batch response must contain a 'batch' object and an " +
                    "'accounts' array.");
            }

            read++;
            foreach (TestAccount account in detail.Accounts.Where(account =>
                string.Equals(account.Email, email.Address, StringComparison.OrdinalIgnoreCase)))
            {
                matches.Add(Describe(account, detail.Batch, now));
            }
        }

        // A deleted account in one batch must not hide the live account in another.
        if (matches.Any(match => !match.Account.IsDeleted))
        {
            matches = [.. matches.Where(match => !match.Account.IsDeleted)];
        }

        return new TestAccountLookupOutcome(matches, candidates.Count, read);
    }

    internal static TestAccountLookupResult Describe(TestAccount account, TestAccountBatch batch, DateTimeOffset now)
    {
        bool expired = IsExpired(batch, now);
        return new TestAccountLookupResult(account, batch, expired, expired ? batch.AccessExpirationDate : null);
    }

    /// <summary>A batch with no expiry date is never expired.</summary>
    internal static bool IsExpired(TestAccountBatch batch, DateTimeOffset now) =>
        IsExpired(batch.AccessExpirationDate, now);

    internal static bool IsExpired(DateTimeOffset? expiry, DateTimeOffset now) =>
        expiry is { } date && date < now;

    /// <summary>
    /// Keeps only the batches in a list response whose access expiry has passed, and applies
    /// <paramref name="top"/> afterwards, so it means "the newest N expired batches" rather than
    /// "the expired ones among the newest N".
    /// </summary>
    internal static string FilterExpired(string listJson, DateTimeOffset now, int? top)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(listJson);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The test account batch list response was not valid JSON.",
                exception);
        }

        if (root is not JsonObject response || response["batches"] is not JsonArray batches)
        {
            throw new InvalidDataException(
                "The test account batch list response must contain a 'batches' array.");
        }

        var kept = new List<JsonNode?>();
        foreach (JsonNode? batch in batches)
        {
            if (batch is not JsonObject item)
            {
                throw new InvalidDataException(
                    "The test account batch list response contains a batch that is not an object.");
            }

            if (IsExpired(ReadExpiry(item), now))
            {
                kept.Add(batch);
            }
        }

        if (top is { } limit)
        {
            kept = [.. kept.Take(limit)];
        }

        var filtered = new JsonArray();
        foreach (JsonNode? batch in kept)
        {
            filtered.Add(batch?.DeepClone());
        }

        response["batches"] = filtered;
        return response.ToJsonString();
    }

    /// <summary>A missing or null expiry means none; any other unreadable value is an error.</summary>
    private static DateTimeOffset? ReadExpiry(JsonObject batch)
    {
        if (batch["accessExpirationDate"] is not { } node)
        {
            return null;
        }

        if (node is JsonValue value &&
            value.TryGetValue(out string? text) &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiry))
        {
            return expiry;
        }

        throw new InvalidDataException(
            "The test account batch list response has a batch whose 'accessExpirationDate' is not a date.");
    }
}
