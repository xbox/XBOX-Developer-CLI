// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Telemetry;

namespace XboxDevCLI.Services;

internal sealed record XboxPartnerSellersResult(string ResponseBody);

internal interface IXboxPartnerClient : IDisposable
{
    Task<XboxPartnerSellersResult> GetSellersAsync(
        string baseUrl,
        CancellationToken cancellationToken = default);

    Task<string> GetGamesAsync(
        string baseUrl,
        string sellerId,
        string? titleId = null,
        string? storeId = null,
        string? keyword = null,
        CancellationToken cancellationToken = default,
        Action<int>? pageRead = null);

    /// <remarks>
    /// <paramref name="onSending"/> is called immediately before the request is handed to the
    /// network. A failure before it is called means the gateway never saw the request.
    /// </remarks>
    Task<XboxPartnerGameCreationJob> CreateGameAsync(
        string baseUrl,
        string sellerId,
        CreateXboxGameRequest request,
        CancellationToken cancellationToken = default,
        Action? onSending = null);

    Task<XboxPartnerGameCreationJob> GetGameCreationJobAsync(
        string baseUrl,
        string sellerId,
        string jobId,
        CancellationToken cancellationToken = default);
}

internal sealed partial class XboxPartnerClient : IXboxPartnerClient
{
    internal const long DefaultMaxResponseBytes = 4 * 1024 * 1024;
    internal const long DefaultMaxDocumentArchiveBytes = 50 * 1024 * 1024;
    internal const string SellerIdHeader = "X-DeveloperAccountId";
    internal const string CorrelationIdHeader = "x-ms-correlation-id";

    // The gateway's largest page. Partner Center reads a page of 100 as fast as a page of 50, about
    // 2.5 seconds, inside the 30-second ingress timeout, so a full listing makes half the requests.
    internal const int GamesPageSize = 100;
    internal const int AchievementImagesPageSize = 50;

    // The largest page the configuration and test account batch lists accept. The gateway may return
    // fewer: at most 20 configurations a page, and 10 batches with includeSandboxes.
    internal const int ListPageSize = 100;
    private static readonly JsonSerializerOptions CreateGameSerializerOptions = new()
    {
        Converters =
        {
            new JsonStringEnumConverter<XboxPartnerDeviceFamily>(
                JsonNamingPolicy.CamelCase, allowIntegerValues: false),
        },
    };
    private readonly AuthenticationService? _authenticationService;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly long _maxResponseBytes;
    private readonly long _maxDocumentArchiveBytes;
    private readonly TimeSpan _requestTimeout;
    private readonly string? _configuredBaseUrl;
    private readonly Func<Task<string?>>? _accessTokenFactory;

    public XboxPartnerClient(
        IConfiguration configuration,
        HttpClient? httpClient = null,
        AuthenticationService? authenticationService = null)
    {
        _configuredBaseUrl = configuration["XboxPartner:BaseUrl"];
        _maxResponseBytes = ResolveMaxResponseBytes(configuration);
        _maxDocumentArchiveBytes = ResolveMaxDocumentArchiveBytes(configuration);
        _requestTimeout = TimeSpan.FromSeconds(ResolveTimeoutSeconds(configuration));
        _authenticationService = authenticationService
            ?? AuthenticationService.Create(configuration, AutomationAuthenticationSettings.CaptureEnvironment());
        _ownsHttpClient = httpClient == null;
        // Following a 307 or 308 sends the request again, so a create could be submitted twice.
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _httpClient.Timeout = _requestTimeout;
    }

    internal XboxPartnerClient(
        IConfiguration configuration,
        HttpClient httpClient,
        Func<Task<string?>> accessTokenFactory)
    {
        _configuredBaseUrl = configuration["XboxPartner:BaseUrl"];
        _maxResponseBytes = ResolveMaxResponseBytes(configuration);
        _maxDocumentArchiveBytes = ResolveMaxDocumentArchiveBytes(configuration);
        _requestTimeout = TimeSpan.FromSeconds(ResolveTimeoutSeconds(configuration));
        _ownsHttpClient = false;
        _httpClient = httpClient;
        _httpClient.Timeout = _requestTimeout;
        _accessTokenFactory = accessTokenFactory
            ?? throw new ArgumentNullException(nameof(accessTokenFactory));
    }

    public Task<XboxPartnerGameCreationJob> CreateGameAsync(
        string baseUrl,
        string sellerId,
        CreateXboxGameRequest request,
        CancellationToken cancellationToken = default,
        Action? onSending = null) =>
        SendJobAsync<XboxPartnerGameCreationJob>(
            HttpMethod.Post,
            baseUrl,
            "xboxpartner/v1/games",
            sellerId,
            JsonSerializer.Serialize(request with { Name = request.Name.Trim() }, CreateGameSerializerOptions),
            cancellationToken,
            onSending);

    public Task<XboxPartnerGameCreationJob> GetGameCreationJobAsync(
        string baseUrl,
        string sellerId,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return SendJobAsync<XboxPartnerGameCreationJob>(
            HttpMethod.Get,
            baseUrl,
            $"xboxpartner/v1/jobs/{Uri.EscapeDataString(jobId)}",
            sellerId,
            null,
            cancellationToken);
    }

    private async Task<TJob> SendJobAsync<TJob>(
        HttpMethod method,
        string baseUrl,
        string path,
        string sellerId,
        string? jsonBody,
        CancellationToken cancellationToken,
        Action? onSending = null)
        where TJob : XboxPartnerCreationJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        sellerId = sellerId.Trim();
        var uri = BuildRequestUri(baseUrl, path);
        var response = await SendAuthorizedAsync(
            uri, sellerId, cancellationToken, method, jsonBody, Guid.NewGuid().ToString(), onSending);
        try
        {
            XboxPartnerCreationJob? job = JsonSerializer.Deserialize<TJob>(response.Body);
            if (job is null || string.IsNullOrWhiteSpace(job.JobId))
            {
                throw new InvalidDataException(
                    "The response did not contain a 'jobId' string with a value.");
            }

            // Keep the original payload for output so omitted and unknown fields are preserved. The
            // copy keeps the job's runtime type, since records clone through a virtual method.
            return (TJob)(job with
            {
                RawResponse = response.Body,
                RetryAfter = response.RetryAfter,
                CorrelationId = response.CorrelationId,
            });
        }
        catch (Exception exception)
            when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            throw new XboxPartnerResponseException(
                response.Body, response.CorrelationId, exception);
        }
    }

    public async Task<XboxPartnerSellersResult> GetSellersAsync(
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var requestUri = BuildRequestUri(baseUrl, "xboxpartner/v1/sellers");
        var response = await SendAuthorizedAsync(requestUri, null, cancellationToken, HttpMethod.Get);
        return new XboxPartnerSellersResult(response.Body);
    }

    /// <summary>
    /// Reads every page of the seller's games and returns them as one <c>games</c> collection.
    /// <paramref name="pageRead"/> is called with each page's game count.
    /// </summary>
    /// <remarks>
    /// Partner Center can't filter by title ID, Store ID, or name, so the gateway filters each
    /// page, and a page can be empty yet still carry a token.
    /// </remarks>
    public async Task<string> GetGamesAsync(
        string baseUrl,
        string sellerId,
        string? titleId = null,
        string? storeId = null,
        string? keyword = null,
        CancellationToken cancellationToken = default,
        Action<int>? pageRead = null)
    {
        var filters = new List<string>();
        AddQueryValue(filters, "titleId", titleId);
        AddQueryValue(filters, "storeId", storeId);
        AddQueryValue(filters, "keyword", keyword);
        IReadOnlyList<string> games = await ReadEveryPageAsync(
            "games",
            "game",
            token =>
            {
                var paging = token == null
                    ? $"pageSize={GamesPageSize}"
                    : $"continuationToken={Uri.EscapeDataString(token)}";
                return (HttpMethod.Get, $"xboxpartner/v1/games?{string.Join('&', filters.Append(paging))}", null);
            },
            cancellationToken,
            pageRead: pageRead,
            baseUrl: baseUrl,
            sellerId: sellerId,
            // A Store ID identifies at most one game.
            stopAtFirstMatch: storeId != null);
        return "{\"games\":[" + string.Join(',', games) + "]}";
    }

    /// <summary>
    /// Reads every page of a list with one correlation ID and returns its items in order, as raw
    /// JSON. <paramref name="request"/> builds a page's request from its continuation token, which
    /// is null for the first page, with a path relative to <paramref name="baseUrl"/>, or to the
    /// configured base URL when that's null. <paramref name="sellerId"/>, when given, is sent in the
    /// seller header. <paramref name="readItem"/> sees each item as its page arrives; an
    /// <see cref="InvalidDataException"/> from it fails the listing with the correlation ID.
    /// <paramref name="pageRead"/> is called with each page's item count.
    /// </summary>
    /// <remarks>
    /// A repeated token would loop forever, so it fails the listing, and so does a listing that
    /// doesn't end within <paramref name="maxPages"/> pages. With <paramref name="stopAtFirstMatch"/>,
    /// the first page that has items ends the listing.
    /// </remarks>
    private async Task<IReadOnlyList<string>> ReadEveryPageAsync(
        string collection,
        string description,
        Func<string?, (HttpMethod Method, string Path, HttpContent? Content)> request,
        CancellationToken cancellationToken,
        Action<string>? readItem = null,
        Action<int>? pageRead = null,
        Action<HttpResponseMessage, string>? responseReceived = null,
        string? baseUrl = null,
        string? sellerId = null,
        bool stopAtFirstMatch = false,
        int maxPages = int.MaxValue)
    {
        var correlationId = Guid.NewGuid().ToString();
        var items = new List<string>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;
        var pages = 0;
        do
        {
            pages++;
            var (method, path, content) = request(continuationToken);
            using HttpResponseMessage response = await SendResponseAsync(
                method, BuildRequestUri(baseUrl ?? GetConfiguredBaseUrl(), path), content, "application/json",
                cancellationToken, correlationId, sellerId: sellerId);
            responseReceived?.Invoke(response, correlationId);
            using var bodyTimeout = CreateResponseBodyTimeout(cancellationToken);
            string body = await ReadResponseBodyWithLimitAsync(response.Content, bodyTimeout.Token);
            try
            {
                var page = ReadPage(body, collection, description);
                if (readItem != null)
                {
                    foreach (var item in page.Items)
                    {
                        readItem(item);
                    }
                }

                items.AddRange(page.Items);
                pageRead?.Invoke(page.Items.Count);
                if (stopAtFirstMatch && page.Items.Count > 0)
                {
                    break;
                }

                continuationToken = page.ContinuationToken;
                if (continuationToken != null && !seenTokens.Add(continuationToken))
                {
                    throw new InvalidDataException(
                        $"The {description} response repeated a continuation token.");
                }

                if (continuationToken != null && pages >= maxPages)
                {
                    throw new InvalidDataException(
                        $"The {description} did not end after {maxPages} pages, so it was stopped.");
                }
            }
            catch (InvalidDataException exception)
            {
                throw new XboxPartnerResponseException(
                    body, ReadCorrelationId(response) ?? correlationId, exception);
            }
        }
        while (continuationToken != null);

        return items;
    }

    private static ListPage ReadPage(string body, string collection, string description)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(collection, out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"The {description} response must contain the {collection} array.");
            }

            string? continuationToken = null;
            if (root.TryGetProperty("continuationToken", out var value)
                && value.ValueKind != JsonValueKind.Null)
            {
                continuationToken = value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : throw new InvalidDataException(
                        $"The {description} response contained a continuationToken that was "
                        + "not a string.");
            }

            // Raw text keeps each item exactly as the gateway sent it.
            return new ListPage(
                items.EnumerateArray().Select(item => item.GetRawText()).ToArray(),
                string.IsNullOrEmpty(continuationToken) ? null : continuationToken);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException(
                $"The {description} response was not valid JSON.",
                exception);
        }
    }

    private sealed record ListPage(IReadOnlyList<string> Items, string? ContinuationToken);

    private static void AddQueryValue(
        ICollection<string> query,
        string name,
        string? value)
    {
        if (value != null)
        {
            query.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    /// Combines the configured gateway base URL with a relative path, rejecting any base URL
    /// that would transmit the caller's bearer token insecurely.
    /// </summary>
    /// <remarks>
    /// The gateway host is deliberately not pinned here; it is configuration
    /// (<c>XboxPartner:BaseUrl</c>), so the CLI can target a different deployment without a
    /// rebuild. What is enforced is the transport: every request carries a Microsoft Entra
    /// access token, so the URL must be HTTPS. Plaintext HTTP is permitted only for a loopback
    /// address, where the traffic never leaves the machine and local development would
    /// otherwise be impossible.
    /// </remarks>
    internal static Uri BuildRequestUri(string baseUrl, string relativePath)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "XboxPartner:BaseUrl must be an absolute HTTP or HTTPS URL.");
        }

        if (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback)
        {
            throw new InvalidOperationException(
                "XboxPartner:BaseUrl must use HTTPS. Plaintext HTTP is allowed only for a "
                + "loopback address during local development, because the request carries a "
                + "Microsoft Entra access token.");
        }

        return new Uri($"{baseUrl.TrimEnd('/')}/{relativePath}", UriKind.Absolute);
    }

    /// <summary>
    /// Sends a JSON request and reads the success response's body, <c>Retry-After</c>, and correlation ID.
    /// </summary>
    private async Task<GatewayResponse> SendAuthorizedAsync(
        Uri requestUri,
        string? sellerId,
        CancellationToken cancellationToken,
        HttpMethod method,
        string? jsonBody = null,
        string? correlationId = null,
        Action? onSending = null,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        using HttpResponseMessage response = await SendResponseAsync(
            method,
            requestUri,
            jsonBody is null ? null : new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            "application/json",
            cancellationToken,
            correlationId,
            onSending,
            sellerId: sellerId,
            headers: headers);
        using var bodyTimeout = CreateResponseBodyTimeout(cancellationToken);
        string body = await ReadResponseBodyWithLimitAsync(response.Content, bodyTimeout.Token);
        return new GatewayResponse(body, ReadRetryAfter(response), ReadCorrelationId(response) ?? correlationId);
    }

    private sealed record GatewayResponse(string Body, TimeSpan? RetryAfter, string? CorrelationId);

    private async Task<string> AcquireRequiredAccessTokenAsync(CancellationToken cancellationToken)
    {
        string? accessToken = null;
        if (_authenticationService is not null)
        {
            accessToken = (await _authenticationService.AcquireTokenSilentAsync(cancellationToken))?.AccessToken;
        }
        else if (_accessTokenFactory is not null)
        {
            // The injected factory takes no token, so a stalled call must not outlast the caller.
            accessToken = await _accessTokenFactory().WaitAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            TelemetryRecorder.RecordError(TelemetryErrorTypes.Auth);
            throw new InvalidOperationException(
                $"No cached authentication token. Run '{CliIdentity.CommandName} auth login' first.");
        }

        return accessToken;
    }

    private string GetConfiguredBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(_configuredBaseUrl))
        {
            throw new InvalidOperationException(
                "No service URL. Set the XBOX_XBOXPARTNER__BASEURL environment variable.");
        }

        return _configuredBaseUrl.TrimEnd('/');
    }

    private async Task<string> SendAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        CancellationToken cancellationToken,
        Action? beforeDispatch = null,
        string? correlationId = null,
        string serviceName = "The")
    {
        using HttpResponseMessage response = await SendResponseAsync(
            method,
            relativePath,
            content,
            "application/json",
            cancellationToken,
            correlationId,
            sending: beforeDispatch,
            serviceName: serviceName);
        using var bodyTimeout = CreateResponseBodyTimeout(cancellationToken);
        return await ReadResponseBodyWithLimitAsync(
            response.Content,
            bodyTimeout.Token,
            serviceName);
    }

    private Task<HttpResponseMessage> SendResponseAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        string accept,
        CancellationToken cancellationToken,
        string? correlationId = null,
        Action? sending = null,
        string serviceName = "The") =>
        SendResponseAsync(
            method,
            BuildRequestUri(GetConfiguredBaseUrl(), relativePath),
            content,
            accept,
            cancellationToken,
            correlationId,
            sending,
            serviceName);

    /// <summary>
    /// Sends one request and returns its success response, for the caller to read and dispose. A failure
    /// response is thrown as an <see cref="XboxPartnerApiException"/>.
    /// </summary>
    private async Task<HttpResponseMessage> SendResponseAsync(
        HttpMethod method,
        Uri requestUri,
        HttpContent? content,
        string accept,
        CancellationToken cancellationToken,
        string? correlationId = null,
        Action? sending = null,
        string serviceName = "The",
        string? sellerId = null,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, requestUri)
        {
            Content = content,
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.ParseAdd(CliIdentity.UserAgent);
        if (correlationId != null)
        {
            request.Headers.Add(CorrelationIdHeader, correlationId);
        }

        foreach (var (name, value) in headers ?? [])
        {
            // Added without validation, so a header such as If-Match reaches the service exactly as given.
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (sellerId != null)
        {
            if (sellerId.Contains('\r', StringComparison.Ordinal)
                || sellerId.Contains('\n', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The Partner Center seller ID must not contain line breaks.");
            }

            // The HTTP stack rejects other header values only after the request has started.
            if (!sellerId.All(character => character is >= ' ' and <= '~'))
            {
                throw new InvalidOperationException(
                    "The Partner Center seller ID must contain only printable ASCII characters.");
            }

            request.Headers.Add(SellerIdHeader, sellerId);
        }

        // Acquired for every request, so MSAL can refresh the token during a long listing.
        string accessToken = await AcquireRequiredAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Everything above can fail without the request leaving the process; a caller that must
        // tell those failures apart from ones after sending learns the boundary here.
        cancellationToken.ThrowIfCancellationRequested();
        sending?.Invoke();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            TelemetryRecorder.RecordError(TelemetryErrorTypes.Transport);
            throw;
        }
        // Many commands catch and format the gateway's failure themselves, so its reason is recorded here.
        TelemetryRecorder.RecordResponseStatus((int)response.StatusCode);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        correlationId = ReadCorrelationId(response) ?? correlationId;

        HttpStatusCode statusCode = response.StatusCode;
        string? reasonPhrase = response.ReasonPhrase;
        TimeSpan? retryAfter = ReadRetryAfter(response);
        string responseBody;

        // An error body that can't be read must not hide the status or the Retry-After wait.
        try
        {
            using var bodyTimeout = CreateResponseBodyTimeout(cancellationToken);
            responseBody = await ReadResponseBodyWithLimitAsync(
                response.Content,
                bodyTimeout.Token,
                serviceName,
                recordFailure: false);
        }
        // The caller's own cancellation isn't a failure of the request, so it propagates as is.
        catch (Exception ex) when ((ex is OperationCanceledException or IOException
            or HttpRequestException or InvalidOperationException)
            && !cancellationToken.IsCancellationRequested)
        {
            throw new XboxPartnerApiException(
                statusCode, reasonPhrase, string.Empty, requestUri, correlationId, retryAfter, ex);
        }
        finally
        {
            response.Dispose();
        }

        throw new XboxPartnerApiException(
            statusCode,
            reasonPhrase,
            responseBody,
            requestUri,
            correlationId,
            retryAfter);
    }

    private static string? ReadCorrelationId(HttpResponseMessage response) =>
        response.Headers.TryGetValues(CorrelationIdHeader, out var values)
            ? values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : null;

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    private async Task<byte[]> SendBytesAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendResponseAsync(
            HttpMethod.Get,
            relativePath,
            content: null,
            "application/xml",
            cancellationToken);
        using var bodyTimeout = CreateResponseBodyTimeout(cancellationToken);
        return await ReadResponseBytesWithLimitAsync(
            response.Content,
            _maxResponseBytes,
            "XboxPartner:MaxResponseBytes",
            bodyTimeout.Token);
    }

    // ResponseHeadersRead keeps the size bound, but HttpClient's timeout ends at the
    // headers. Apply the same bound while consuming the body.
    private CancellationTokenSource CreateResponseBodyTimeout(
        CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        return timeout;
    }

    private static Task<byte[]> ReadResponseBytesWithLimitAsync(
        HttpContent responseContent,
        long maxResponseBytes,
        string configurationKey,
        CancellationToken cancellationToken) =>
        ResponseBodyReader.ReadBytesAsync(
            responseContent,
            maxResponseBytes,
            () => ResponseTooLarge(maxResponseBytes, configurationKey),
            cancellationToken);

    private static long ResolveMaxResponseBytes(IConfiguration configuration)
    {
        var maxResponseBytes =
            configuration.GetValue<long?>("XboxPartner:MaxResponseBytes")
            ?? DefaultMaxResponseBytes;
        if (maxResponseBytes <= 0)
        {
            throw new InvalidOperationException(
                "Configuration key 'XboxPartner:MaxResponseBytes' must be a positive number of bytes.");
        }

        return maxResponseBytes;
    }

    private static long ResolveMaxDocumentArchiveBytes(IConfiguration configuration)
    {
        long maxResponseBytes =
            configuration.GetValue<long?>("XboxPartner:MaxDocumentArchiveBytes")
            ?? DefaultMaxDocumentArchiveBytes;
        if (maxResponseBytes <= 0)
        {
            throw new InvalidOperationException(
                "Configuration key 'XboxPartner:MaxDocumentArchiveBytes' must be a positive " +
                "number of bytes.");
        }

        return maxResponseBytes;
    }

    private static double ResolveTimeoutSeconds(IConfiguration configuration)
    {
        var timeoutSeconds = configuration.GetValue<double?>("XboxPartner:TimeoutSeconds") ?? 120;
        if (!double.IsFinite(timeoutSeconds)
            || timeoutSeconds <= 0
            || timeoutSeconds > int.MaxValue / 1000.0
            || TimeSpan.FromSeconds(timeoutSeconds) <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Configuration key 'XboxPartner:TimeoutSeconds' must be a positive number of "
                + "seconds within the supported timeout range.");
        }

        return timeoutSeconds;
    }

    // A body that doesn't arrive is recorded as a transport failure, unless recordFailure is false,
    // as for an error response, whose status already gave the reason.
    private async Task<string> ReadResponseBodyWithLimitAsync(
        HttpContent responseContent,
        CancellationToken cancellationToken,
        string serviceName = "The",
        bool recordFailure = true)
    {
        try
        {
            return await ResponseBodyReader.ReadTextAsync(
                responseContent,
                _maxResponseBytes,
                () => ResponseTooLarge(_maxResponseBytes, "XboxPartner:MaxResponseBytes", serviceName),
                cancellationToken);
        }
        // Ctrl+C is recorded as cancelled where the command ends, which takes precedence.
        catch (Exception ex) when (recordFailure
            && ex is OperationCanceledException or IOException or HttpRequestException)
        {
            TelemetryRecorder.RecordError(TelemetryErrorTypes.Transport);
            throw;
        }
    }

    private static InvalidOperationException ResponseTooLarge(
        long maxResponseBytes,
        string configurationKey,
        string serviceName = "The") =>
        new(
            $"{serviceName} response exceeded the configured maximum size of {maxResponseBytes} bytes. "
            + $"Increase '{configurationKey}' to allow larger responses.");
}
