// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XboxDevCLI.Services;

internal sealed partial class XboxPartnerClient
{
    internal const string TestAccountBatchesPath = "xboxpartner/v1/testaccounts/batches";

    public async Task<string> ListTestAccountBatchesAsync(
        long? sellerId,
        TestAccountBatchFilter filter,
        CancellationToken cancellationToken,
        Action<int>? pageRead = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateSellerId(sellerId);
        (string Name, string? Value)[] filters =
        [
            ("sellerId", Invariant(sellerId)),
            ("mine", filter.Mine ? "true" : null),
            ("createdBy", Invariant(filter.CreatedBy)),
            ("emailPrefix", filter.EmailPrefix),
            ("keyword", filter.Keyword),
            ("state", filter.State),
            ("top", Invariant(filter.Top)),
            ("includeSandboxes", filter.IncludeSandboxes ? "true" : null),
        ];
        IReadOnlyList<string> batches = await ReadEveryPageAsync(
            "batches",
            "test account batch list",
            token =>
            {
                var paging = token is null ? ("pageSize", Invariant(ListPageSize)) : ("continuationToken", token);
                return (HttpMethod.Get, TestAccountBatchesPath + BuildQueryString([.. filters, paging]), null);
            },
            cancellationToken,
            serviceName: null,
            pageRead: pageRead).ConfigureAwait(false);
        return "{\"batches\":[" + string.Join(',', batches) + "]}";
    }

    public async Task<string> GetTestAccountBatchAsync(
        long? sellerId,
        long batchId,
        CancellationToken cancellationToken)
    {
        ValidateSellerId(sellerId);
        if (batchId <= 0)
        {
            throw new ArgumentException("Batch ID must be a positive number.", nameof(batchId));
        }

        string path = $"{TestAccountBatchesPath}/{Invariant(batchId)}" +
            BuildQueryString(("sellerId", Invariant(sellerId)));
        return await SendAsync(
            HttpMethod.Get,
            path,
            content: null,
            cancellationToken,
            correlationId: Guid.NewGuid().ToString(),
            serviceName: "Test account").ConfigureAwait(false);
    }

    private static string? Invariant(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions CreateTestAccountsSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Starts a test account creation job. Sent exactly once: the create is not idempotent, so a
    /// failure after the request may have reached the server is the caller's to classify, never
    /// a reason to send it again. <paramref name="sending"/> runs just before the request leaves
    /// the process, so the caller knows whether a failure could have followed the server seeing it.
    /// </summary>
    public async Task<TestAccountJobResult> CreateTestAccountBatchAsync(
        CreateTestAccountBatchRequest request,
        CancellationToken cancellationToken,
        Action? sending = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSellerId(request.SellerId);
        ArgumentException.ThrowIfNullOrEmpty(request.Password);
        ArgumentException.ThrowIfNullOrEmpty(request.SecurityAnswer);
        string body = JsonSerializer.Serialize(request, CreateTestAccountsSerializerOptions);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendTestAccountJobAsync(HttpMethod.Post, TestAccountBatchesPath, content, cancellationToken, sending)
            .ConfigureAwait(false);
    }

    /// <summary>Reads a test account creation job once. XboxPartner never polls.</summary>
    public async Task<TestAccountJobResult> GetTestAccountJobAsync(
        long? sellerId,
        long batchId,
        string jobId,
        CancellationToken cancellationToken)
    {
        ValidateSellerId(sellerId);
        if (batchId <= 0)
        {
            throw new ArgumentException("Batch ID must be a positive number.", nameof(batchId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        string path = $"{TestAccountBatchesPath}/{Invariant(batchId)}/jobs/{Uri.EscapeDataString(jobId.Trim())}" +
            BuildQueryString(("sellerId", Invariant(sellerId)));
        return await SendTestAccountJobAsync(HttpMethod.Get, path, content: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Starts deleting a test account batch. Sent exactly once, like the create: a failure after
    /// the request may have reached the server is the caller's to classify by reading the batch.
    /// <paramref name="allowNotMine"/> lets the caller delete a batch another developer created.
    /// </summary>
    public async Task<TestAccountBatchDeletionResult> DeleteTestAccountBatchAsync(
        long? sellerId,
        long batchId,
        bool allowNotMine,
        CancellationToken cancellationToken,
        Action? sending = null)
    {
        ValidateSellerId(sellerId);
        if (batchId <= 0)
        {
            throw new ArgumentException("Batch ID must be a positive number.", nameof(batchId));
        }

        string path = $"{TestAccountBatchesPath}/{Invariant(batchId)}" +
            BuildQueryString(
                ("sellerId", Invariant(sellerId)),
                ("allowNotMine", allowNotMine ? "true" : null));
        var (body, deletion, retryAfter, correlationId) = await SendTestAccountOperationAsync<TestAccountBatchDeletion>(
            HttpMethod.Delete,
            path,
            content: null,
            "delete",
            deletion => deletion.BatchId > 0 &&
                !string.IsNullOrWhiteSpace(deletion.JobId) &&
                !string.IsNullOrWhiteSpace(deletion.State),
            "a positive 'batchId', and a 'jobId' and 'state' that are not empty",
            cancellationToken,
            sending).ConfigureAwait(false);
        return new TestAccountBatchDeletionResult(body, deletion, retryAfter, correlationId);
    }

    /// <summary>
    /// Changes a test account batch's sandboxes, sandbox access expiry, or keywords. Sent exactly
    /// once: XboxPartner forwards it once with no retry, so a failure after the request may
    /// have reached the server is the caller's to classify by reading the batch.
    /// <paramref name="allowNotMine"/> lets the caller change a batch another developer created.
    /// </summary>
    public async Task<TestAccountBatchUpdateResult> UpdateTestAccountBatchAsync(
        long? sellerId,
        long batchId,
        UpdateTestAccountBatchRequest request,
        bool allowNotMine,
        CancellationToken cancellationToken,
        Action? sending = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSellerId(sellerId);
        if (batchId <= 0)
        {
            throw new ArgumentException("Batch ID must be a positive number.", nameof(batchId));
        }

        string path = $"{TestAccountBatchesPath}/{Invariant(batchId)}" +
            BuildQueryString(
                ("sellerId", Invariant(sellerId)),
                ("allowNotMine", allowNotMine ? "true" : null));
        string json = JsonSerializer.Serialize(request, CreateTestAccountsSerializerOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var (body, update, retryAfter, correlationId) = await SendTestAccountOperationAsync<TestAccountBatchUpdate>(
            HttpMethod.Patch,
            path,
            content,
            "update",
            update => update.BatchId > 0 &&
                !string.IsNullOrWhiteSpace(update.State) &&
                (update.IsUnchanged || (!string.IsNullOrWhiteSpace(update.JobId) && EchoesRequest(update, request))),
            "a positive 'batchId', a 'state' that is not empty, and unless the state is unchanged, a 'jobId' " +
                "and the 'sandboxes', 'accessExpirationDate' and 'keywords' the update changes",
            cancellationToken,
            sending).ConfigureAwait(false);
        return new TestAccountBatchUpdateResult(body, update, retryAfter, correlationId);
    }

    // The waiter checks the batch against these values, so a reply without them could not be verified.
    private static bool EchoesRequest(TestAccountBatchUpdate update, UpdateTestAccountBatchRequest request) =>
        (update.Sandboxes is not null || (request.AddSandboxes is not { Count: > 0 } && request.RemoveSandboxes is not { Count: > 0 })) &&
        (update.AccessExpirationDate is not null || request.AccessExpirationDate is null) &&
        (update.Keywords is not null || request.Keywords is null);

    private async Task<TestAccountJobResult> SendTestAccountJobAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken,
        Action? sending = null)
    {
        var (body, job, retryAfter, correlationId) = await SendTestAccountOperationAsync<TestAccountJob>(
            method,
            path,
            content,
            "job",
            job => job.BatchId > 0 && !string.IsNullOrWhiteSpace(job.JobId) && !string.IsNullOrWhiteSpace(job.State),
            "a positive 'batchId', and a 'jobId' and 'state' that are not empty",
            cancellationToken,
            sending).ConfigureAwait(false);
        return new TestAccountJobResult(body, job, retryAfter, correlationId);
    }

    /// <summary>
    /// Sends a request whose answer starts or describes a background test account operation, and
    /// reads the body, <c>Retry-After</c>, and correlation ID. A success body that cannot be read
    /// or fails <paramref name="isValid"/> is a <see cref="XboxPartnerResponseException"/>, since
    /// the server may have acted on the request.
    /// </summary>
    private async Task<(string Body, T Value, TimeSpan? RetryAfter, string CorrelationId)> SendTestAccountOperationAsync<T>(
        HttpMethod method,
        string path,
        HttpContent? content,
        string responseName,
        Func<T, bool> isValid,
        string requiredFields,
        CancellationToken cancellationToken,
        Action? sending)
        where T : class
    {
        string correlationId = Guid.NewGuid().ToString();
        using HttpResponseMessage response = await SendResponseAsync(
            method,
            path,
            content,
            "application/json",
            cancellationToken,
            correlationId,
            sending,
            serviceName: "Test account").ConfigureAwait(false);
        correlationId = ReadCorrelationId(response) ?? correlationId;
        TimeSpan? retryAfter = ReadRetryAfter(response);
        string body;
        try
        {
            using var bodyTimeout = CreateResponseBodyTimeout(cancellationToken);
            body = await ReadResponseBodyWithLimitAsync(
                response.Content, bodyTimeout.Token, serviceName: "Test account").ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            // An oversized success body is still a success the caller could not read.
            throw new XboxPartnerResponseException(string.Empty, correlationId, exception);
        }

        try
        {
            T value = JsonSerializer.Deserialize<T>(
                    body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The response was null.");
            if (!isValid(value))
            {
                throw new InvalidDataException(
                    $"The test account {responseName} response must contain {requiredFields}.");
            }

            return (body, value, retryAfter, correlationId);
        }
        catch (JsonException exception)
        {
            throw new XboxPartnerResponseException(
                body,
                correlationId,
                new InvalidDataException(
                    $"The test account {responseName} response was not valid JSON.",
                    exception));
        }
        catch (InvalidDataException exception)
        {
            throw new XboxPartnerResponseException(body, correlationId, exception);
        }
    }

    /// <summary>Builds an escaped query string, skipping unset or blank values.</summary>
    private static string BuildQueryString(params (string Name, string? Value)[] parameters)
    {
        var pairs = parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
            .Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(parameter.Value!.Trim())}")
            .ToList();
        return pairs.Count == 0 ? string.Empty : "?" + string.Join('&', pairs);
    }
}

/// <summary>
/// Filters for listing test account batches. Only <see cref="Keyword"/> is applied when XboxPartner
/// reads the seller's batches; XboxPartner applies the rest afterwards.
/// </summary>
public sealed record TestAccountBatchFilter(
    bool Mine = false,
    long? CreatedBy = null,
    string? EmailPrefix = null,
    string? Keyword = null,
    string? State = null,
    int? Top = null,
    bool IncludeSandboxes = false);

/// <summary>
/// A test account batch as returned by XboxPartner. Mirrors the server's contract so table
/// columns can be named with <c>nameof</c>. <see cref="Keywords"/> and
/// <see cref="SandboxAccess"/> are null on the list unless <c>includeSandboxes</c> is set, and
/// <see cref="IsComplete"/> means the provisioning job stopped, not that it succeeded.
/// </summary>
public sealed record TestAccountBatch(
    long Id,
    string? EmailTemplate,
    int BatchSize,
    long CreatedBy,
    string? FamilyPermission,
    DateTimeOffset? CreationDate,
    DateTimeOffset? AccessExpirationDate,
    string? Locale,
    IReadOnlyList<string>? Keywords,
    IReadOnlyList<string>? SandboxAccess,
    bool IsComplete,
    string? JobId,
    string? Progress);

/// <summary>One test account in a batch. XboxPartner never returns passwords.</summary>
public sealed record TestAccount(
    string? Xuid,
    string? Email,
    string? Gamertag,
    bool IsDeleted);

/// <summary>Response for the batch list: every matching batch, newest first.</summary>
public sealed record TestAccountBatchesResponse(IReadOnlyList<TestAccountBatch> Batches);

/// <summary>Response for one batch and its accounts.</summary>
public sealed record TestAccountBatchResponse(
    TestAccountBatch Batch,
    IReadOnlyList<TestAccount> Accounts);

/// <summary>
/// The body of a test account create request. A class rather than a record, so formatting it
/// never prints <see cref="Password"/> or <see cref="SecurityAnswer"/>.
/// </summary>
public sealed class CreateTestAccountBatchRequest
{
    public long? SellerId { get; set; }

    public required int Count { get; init; }

    public int? ChildCount { get; init; }

    public required string EmailPrefix { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public required string Password { get; set; }

    public required string SecurityQuestion { get; init; }

    public required string SecurityAnswer { get; set; }

    public required IReadOnlyList<string> Sandboxes { get; init; }

    public string? AlternateEmail { get; init; }

    public DateTimeOffset? AccessExpirationDate { get; init; }

    public IReadOnlyList<string>? Keywords { get; init; }

    public string? Locale { get; init; }

    public string? Subscription { get; init; }
}

/// <summary>The states <see cref="TestAccountJob.State"/> can take.</summary>
public static class TestAccountJobState
{
    public const string Running = "running";

    public const string Succeeded = "succeeded";

    public const string Failed = "failed";
}

/// <summary>
/// A test account creation job, as the create response and every job read return it. The create
/// response also carries <c>sandboxes</c>, <c>locale</c>, and <c>subscription</c>, which the raw
/// JSON output keeps.
/// </summary>
public sealed record TestAccountJob(
    long BatchId,
    string JobId,
    string State,
    int Total,
    int Succeeded,
    int Failed,
    string? ErrorCode,
    string? ErrorMessage,
    string? Progress)
{
    public bool IsRunning => !IsSucceeded && !IsFailed;

    public bool IsSucceeded => string.Equals(State, TestAccountJobState.Succeeded, StringComparison.OrdinalIgnoreCase);

    public bool IsFailed => string.Equals(State, TestAccountJobState.Failed, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A job read with the raw body and the headers the CLI acts on.</summary>
internal sealed record TestAccountJobResult(
    string RawResponse,
    TestAccountJob Job,
    TimeSpan? RetryAfter,
    string? CorrelationId);

/// <summary>
/// The answer to a batch delete: the background job XboxPartner started. The job endpoint cannot read
/// delete jobs, so the CLI follows a delete by reading the batch until it is gone.
/// </summary>
public sealed record TestAccountBatchDeletion(
    long BatchId,
    string JobId,
    string State,
    string? EmailTemplate,
    int BatchSize);

/// <summary>A delete answer with the raw body and the headers the CLI acts on.</summary>
internal sealed record TestAccountBatchDeletionResult(
    string RawResponse,
    TestAccountBatchDeletion Deletion,
    TimeSpan? RetryAfter,
    string? CorrelationId);

/// <summary>
/// The body of a batch update. Every field is optional, but the service needs at least one.
/// Null fields are left out of the request. To remove every keyword, send an empty
/// <see cref="Keywords"/> with <see cref="ReplaceKeywords"/> true; an empty list alone adds nothing.
/// </summary>
public sealed class UpdateTestAccountBatchRequest
{
    public IReadOnlyList<string>? AddSandboxes { get; init; }

    public IReadOnlyList<string>? RemoveSandboxes { get; init; }

    /// <summary>When the accounts' sandbox access expires, not when the accounts expire.</summary>
    public DateTimeOffset? AccessExpirationDate { get; init; }

    /// <summary>
    /// Keywords to add to the batch's keywords, or with <see cref="ReplaceKeywords"/> its new
    /// keywords. The service keeps the batch's CreatedWith marker either way.
    /// </summary>
    public IReadOnlyList<string>? Keywords { get; init; }

    /// <summary>
    /// True to replace the batch's keywords with <see cref="Keywords"/> instead of adding to them.
    /// Null is left out of the body, so the service adds.
    /// </summary>
    public bool? ReplaceKeywords { get; init; }
}

/// <summary>The states <see cref="TestAccountBatchUpdate.State"/> can take.</summary>
public static class TestAccountBatchUpdateState
{
    public const string Updating = "updating";

    public const string Unchanged = "unchanged";
}

/// <summary>
/// The answer to a batch update: the values the batch will have once the update job
/// completes. The job endpoint cannot read update jobs, so the CLI follows one by reading the
/// batch until its <c>jobId</c> clears. <see cref="JobId"/> is null when nothing changed.
/// </summary>
public sealed record TestAccountBatchUpdate(
    long BatchId,
    string? JobId,
    string State,
    IReadOnlyList<string>? Sandboxes,
    DateTimeOffset? AccessExpirationDate,
    IReadOnlyList<string>? Keywords)
{
    public bool IsUnchanged =>
        string.Equals(State, TestAccountBatchUpdateState.Unchanged, StringComparison.OrdinalIgnoreCase);
}

/// <summary>An update answer with the raw body and the headers the CLI acts on.</summary>
internal sealed record TestAccountBatchUpdateResult(
    string RawResponse,
    TestAccountBatchUpdate Update,
    TimeSpan? RetryAfter,
    string? CorrelationId);
