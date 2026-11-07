// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;

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
        cancellationToken.ThrowIfCancellationRequested();
        var token = await AcquireRequiredTokenAsync(cancellationToken);
        var response = await SendAuthorizedAsync(
            uri, token, sellerId, cancellationToken, method, jsonBody, Guid.NewGuid().ToString(), onSending);
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
        var token = await AcquireRequiredTokenAsync(cancellationToken);
        var requestUri = BuildRequestUri(baseUrl, "xboxpartner/v1/sellers");
        var body = await GetAuthorizedAsync(requestUri, token, null, cancellationToken);
        return new XboxPartnerSellersResult(body);
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

        // One correlation ID lets the gateway logs trace the listing as a whole.
        var correlationId = Guid.NewGuid().ToString();
        var games = new List<string>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = await AcquireRequiredTokenAsync(cancellationToken);
            var paging = continuationToken == null
                ? $"pageSize={GamesPageSize}"
                : $"continuationToken={Uri.EscapeDataString(continuationToken)}";
            var requestUri = BuildRequestUri(
                baseUrl,
                $"xboxpartner/v1/games?{string.Join('&', filters.Append(paging))}");
            var response = await SendAuthorizedAsync(
                requestUri,
                token,
                sellerId,
                cancellationToken,
                HttpMethod.Get,
                correlationId: correlationId);
            try
            {
                var page = ReadPage(response.Body, "games", "game");
                games.AddRange(page.Items);
                pageRead?.Invoke(page.Items.Count);

                // A Store ID identifies at most one game.
                if (storeId != null && page.Items.Count > 0)
                {
                    break;
                }

                continuationToken = page.ContinuationToken;
                if (continuationToken != null && !seenTokens.Add(continuationToken))
                {
                    throw new InvalidDataException(
                        "The game response repeated a continuation token.");
                }
            }
            catch (InvalidDataException exception)
            {
                // Carries the listing's correlation ID to the error output.
                throw new XboxPartnerResponseException(
                    response.Body, response.CorrelationId, exception);
            }
        }
        while (continuationToken != null);

        return "{\"games\":[" + string.Join(',', games) + "]}";
    }

    /// <summary>
    /// Reads every page of a list with one correlation ID and returns its items in order, as raw
    /// JSON. <paramref name="request"/> builds a page's request from its continuation token, which
    /// is null for the first page. <paramref name="readItem"/> sees each item as its page
    /// arrives; an <see cref="InvalidDataException"/> from it fails the listing with the
    /// correlation ID. <paramref name="pageRead"/> is called with each page's item count.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadEveryPageAsync(
        string collection,
        string description,
        Func<string?, (HttpMethod Method, string Path, HttpContent? Content)> request,
        CancellationToken cancellationToken,
        Action<string>? readItem = null,
        string? serviceName = null,
        Action<int>? pageRead = null)
    {
        string responseName = serviceName is null ? description : $"{serviceName} {description}";
        var correlationId = Guid.NewGuid().ToString();
        var items = new List<string>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;
        do
        {
            var (method, path, content) = request(continuationToken);
            var body = await SendAsync(
                method, path, content, cancellationToken, correlationId: correlationId,
                serviceName: serviceName ?? "The");
            try
            {
                var page = ReadPage(body, collection, description, serviceName);
                if (readItem != null)
                {
                    foreach (var item in page.Items)
                    {
                        readItem(item);
                    }
                }

                items.AddRange(page.Items);
                pageRead?.Invoke(page.Items.Count);
                continuationToken = page.ContinuationToken;
                if (continuationToken != null && !seenTokens.Add(continuationToken))
                {
                    throw new InvalidDataException(
                        $"The {responseName} response repeated a continuation token.");
                }
            }
            catch (InvalidDataException exception)
            {
                throw new XboxPartnerResponseException(body, correlationId, exception);
            }
        }
        while (continuationToken != null);

        return items;
    }

    private static ListPage ReadPage(string body, string collection, string description, string? serviceName = null)
    {
        string responseName = serviceName is null ? description : $"{serviceName} {description}";
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(collection, out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"The {responseName} response must contain the {collection} array.");
            }

            string? continuationToken = null;
            if (root.TryGetProperty("continuationToken", out var value)
                && value.ValueKind != JsonValueKind.Null)
            {
                continuationToken = value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : throw new InvalidDataException(
                        $"The {responseName} response contained a continuationToken that was "
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
                $"The {responseName} response was not valid JSON.",
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

    private async Task<string> GetAuthorizedAsync(
        Uri requestUri,
        AuthenticationResult token,
        string? sellerId,
        CancellationToken cancellationToken) =>
        (await SendAuthorizedAsync(
            requestUri, token, sellerId, cancellationToken, HttpMethod.Get)).Body;

    private async Task<GatewayResponse> SendAuthorizedAsync(
        Uri requestUri,
        AuthenticationResult token,
        string? sellerId,
        CancellationToken cancellationToken,
        HttpMethod method,
        string? jsonBody = null,
        string? correlationId = null,
        Action? onSending = null)
    {
        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(CliIdentity.UserAgent);
        if (correlationId != null)
        {
            request.Headers.Add(CorrelationIdHeader, correlationId);
        }

        if (jsonBody != null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
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

        // Everything above fails locally; from here the gateway may receive the request.
        onSending?.Invoke();
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        correlationId = ReadCorrelationId(response) ?? correlationId;

        // ResponseHeadersRead keeps the size bound, but HttpClient's timeout ends at the
        // headers. Apply the same bound while consuming the body.
        using var bodyTimeout = CreateResponseBodyTimeout(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // An error body that can't be read must not hide the status or the Retry-After wait.
            TimeSpan? retryAfter = ReadRetryAfter(response);
            string errorBody;
            try
            {
                errorBody = await ReadResponseBodyWithLimitAsync(
                    response.Content,
                    bodyTimeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException
                or HttpRequestException or InvalidOperationException)
            {
                throw new XboxPartnerApiException(
                    response.StatusCode, response.ReasonPhrase, string.Empty, requestUri, correlationId, retryAfter, ex);
            }

            throw new XboxPartnerApiException(
                response.StatusCode,
                response.ReasonPhrase,
                errorBody,
                requestUri,
                correlationId,
                retryAfter);
        }

        var responseBody = await ReadResponseBodyWithLimitAsync(
            response.Content,
            bodyTimeout.Token);

        return new GatewayResponse(responseBody, ReadRetryAfter(response), correlationId);
    }

    private sealed record GatewayResponse(string Body, TimeSpan? RetryAfter, string? CorrelationId);

    private async Task<AuthenticationResult> AcquireRequiredTokenAsync(CancellationToken cancellationToken)
    {
        AuthenticationService authenticationService = _authenticationService
            ?? throw new InvalidOperationException(
                "This client was constructed without an authentication service.");
        var token = await authenticationService.AcquireTokenSilentAsync(cancellationToken);
        if (token == null)
        {
            throw new InvalidOperationException(
                $"No cached authentication token. Run '{CliIdentity.CommandName} auth login' first.");
        }

        return token;
    }

    private async Task<string> AcquireRequiredAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessTokenFactory is null)
        {
            return (await AcquireRequiredTokenAsync(cancellationToken)).AccessToken;
        }

        // The injected factory takes no token, so a stalled call must not outlast the caller.
        string? accessToken = await _accessTokenFactory().WaitAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
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

    private async Task<HttpResponseMessage> SendResponseAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        string accept,
        CancellationToken cancellationToken,
        string? correlationId = null,
        Action? sending = null,
        string serviceName = "The")
    {
        string accessToken = await AcquireRequiredAccessTokenAsync(cancellationToken);
        Uri requestUri = BuildRequestUri(GetConfiguredBaseUrl(), relativePath);
        using var request = new HttpRequestMessage(method, requestUri)
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.ParseAdd(CliIdentity.UserAgent);
        if (correlationId != null)
        {
            request.Headers.Add(CorrelationIdHeader, correlationId);
        }

        // Everything above can fail without the request leaving the process; a caller that must
        // tell those failures apart from ones after sending learns the boundary here.
        cancellationToken.ThrowIfCancellationRequested();
        sending?.Invoke();
        HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        correlationId = ReadCorrelationId(response) ?? correlationId;

        HttpStatusCode statusCode = response.StatusCode;
        string? reasonPhrase = response.ReasonPhrase;
        TimeSpan? retryAfter = ReadRetryAfter(response);
        string responseBody;
        try
        {
            using var bodyTimeout = CreateResponseBodyTimeout(cancellationToken);
            responseBody = await ReadResponseBodyWithLimitAsync(
                response.Content,
                bodyTimeout.Token,
                serviceName);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException
            or HttpRequestException or InvalidOperationException)
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

    private CancellationTokenSource CreateResponseBodyTimeout(
        CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        return timeout;
    }

    private static async Task<byte[]> ReadResponseBytesWithLimitAsync(
        HttpContent responseContent,
        long maxResponseBytes,
        string configurationKey,
        CancellationToken cancellationToken)
    {
        if (responseContent.Headers.ContentLength is long contentLength
            && contentLength > maxResponseBytes)
        {
            throw ResponseTooLarge(maxResponseBytes, configurationKey);
        }

        await using Stream responseStream =
            await responseContent.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        long totalBytesRead = 0;
        while (true)
        {
            int bytesRead = await responseStream.ReadAsync(chunk, cancellationToken);
            if (bytesRead == 0)
            {
                return buffer.ToArray();
            }

            totalBytesRead += bytesRead;
            if (totalBytesRead > maxResponseBytes)
            {
                throw ResponseTooLarge(maxResponseBytes, configurationKey);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
        }
    }

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

    private async Task<string> ReadResponseBodyWithLimitAsync(
        HttpContent responseContent,
        CancellationToken cancellationToken,
        string serviceName = "The")
    {
        if (responseContent.Headers.ContentLength is long contentLength
            && contentLength > _maxResponseBytes)
        {
            throw ResponseTooLarge(serviceName);
        }

        await using var responseStream = await responseContent.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        long totalBytesRead = 0;

        while (true)
        {
            var bytesRead = await responseStream.ReadAsync(chunk, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
            if (totalBytesRead > _maxResponseBytes)
            {
                throw ResponseTooLarge(serviceName);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(
            buffer,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private InvalidOperationException ResponseTooLarge(string serviceName) =>
        ResponseTooLarge(_maxResponseBytes, "XboxPartner:MaxResponseBytes", serviceName);

    private static InvalidOperationException ResponseTooLarge(
        long maxResponseBytes,
        string configurationKey,
        string serviceName = "The") =>
        new(
            $"{serviceName} response exceeded the configured maximum size of {maxResponseBytes} bytes. "
            + $"Increase '{configurationKey}' to allow larger responses.");
}
