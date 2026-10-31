// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Safety;
using XboxDevCLI.Telemetry;

namespace XboxDevCLI.Services;

/// <summary>
/// HTTP client for calling PlayFab APIs using Entra ID Bearer token authentication.
/// Sends POST requests to the configured <c>PlayFab:BaseUrlTemplate</c> (shipped as
/// https://xboxdevcli.playfabapi.com by default), with API paths supplied as relative
/// request URIs. In the shipped configuration, the target title is carried
/// via <c>PlayFab:RequestHeaders</c> (e.g. an <c>X-PlayFab-Host</c> header templated as
/// <c>{titleId}.playfabapi.com</c>) rather than in the request host.
/// </summary>
public class PlayFabApiClient : IDisposable
{
    internal const long DefaultMaxResponseBytes = 4 * 1024 * 1024;

    private readonly HttpClient _httpClient;

    /// <summary>The only address requests are sent to, captured when the client is built.</summary>
    private readonly Uri _baseAddress;
    private readonly AuthenticationService _authService;
    private readonly bool _ownsHttpClient;
    private readonly bool _dependencyTelemetryEnabled;
    private readonly long _maxResponseBytes;
    private readonly AuditLogger _auditLogger;
    private readonly List<KeyValuePair<string, string>> _requestHeaders = new();

    /// <summary>
    /// Header names that must not be configured via <c>PlayFab:RequestHeaders</c>
    /// because they are managed by the client or could weaken security.
    /// </summary>
    private static readonly HashSet<string> DeniedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Host",
        "Cookie",
        "Set-Cookie",
        "X-SecretKey",
        "X-EntityToken",
    };

    private static readonly JsonSerializerOptions PrettyPrintOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = null, // PlayFab uses PascalCase
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public PlayFabApiClient(IConfiguration configuration, AuthenticationService authService)
        : this(configuration, authService, httpClient: null, auditLogPath: null)
    {
    }

    /// <summary>
    /// Constructor that accepts an externally-provided <see cref="HttpClient"/>
    /// for testability. When <paramref name="httpClient"/> is <c>null</c>, a new
    /// instance is created and owned (disposed) by this client.
    /// </summary>
    internal PlayFabApiClient(
        IConfiguration configuration,
        AuthenticationService authService,
        HttpClient? httpClient,
        string? auditLogPath = null)
    {
        _authService = authService;
        _ownsHttpClient = httpClient == null;
        // Resolve telemetry state through TelemetryOptions so the client honors every
        // opt-out path (the --disable-telemetry flag maps to Telemetry:Enabled=false, and
        // the XBOX_TELEMETRY_OPTOUT environment variable is applied here too). Reading
        // the raw config keys directly would miss the env-var opt-out.
        var telemetryOptions = TelemetryOptions.FromConfiguration(configuration);
        _dependencyTelemetryEnabled =
            telemetryOptions.Enabled && telemetryOptions.DependencyTracingEnabled;
        _maxResponseBytes = ResolveMaxResponseBytes(configuration);

        var titleId = configuration["PlayFab:TitleId"];
        if (string.IsNullOrWhiteSpace(titleId))
        {
            throw new InvalidOperationException(
                "No playfab-title-id is set. Pass --playfab-title-id, set XBOX_PLAYFAB_TITLE_ID, or run " +
                $"'{CliIdentity.CommandName} context set playfab-title-id <value>'.");
        }

        _auditLogger = new AuditLogger(configuration, titleId, auditLogPath);

        var baseUrlTemplate = EndpointOverrideWarning.GetEffectiveBaseUrlTemplate(
            configuration[EndpointOverrideWarning.ConfigurationKey]);
        var baseUrl = string.Format(baseUrlTemplate, titleId);

        var headerSection = configuration.GetSection("PlayFab:RequestHeaders").GetChildren();
        foreach (var entry in headerSection)
        {
            var name = entry["Name"];
            var valueTemplate = entry["Value"];

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    "PlayFab:RequestHeaders contains an entry with a missing or empty Name.");
            }

            if (DeniedHeaders.Contains(name))
            {
                throw new InvalidOperationException(
                    $"PlayFab:RequestHeaders contains a denied header '{name}'.");
            }

            var value = (valueTemplate ?? string.Empty).Replace("{titleId}", titleId);
            _requestHeaders.Add(new KeyValuePair<string, string>(name, value));
        }

        _httpClient = httpClient ?? new HttpClient();

        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        }

        _baseAddress = _httpClient.BaseAddress;
    }

    /// <summary>
    /// Sends an authenticated POST request to the specified PlayFab API path.
    /// </summary>
    /// <param name="apiPath">
    /// The API path, e.g. "/Admin/GetTitleData" or "/Catalog/SearchItems".
    /// A leading slash is added if not present.
    /// </param>
    /// <param name="jsonBody">The JSON request body; "{}" is sent only when null.</param>
    /// <param name="cancellationToken">Cancels token acquisition, the request, and the response read.</param>
    /// <param name="confirmedDestructive">
    /// Must be <c>true</c> to allow a destructive, irreversible operation (any
    /// <c>Delete*</c> / <c>Shutdown*</c> endpoint). This is the fail-closed enforcement
    /// point: a destructive call with this left <c>false</c> is refused before any token
    /// is acquired or request is sent, regardless of which caller or overload invoked it.
    /// The CLI sets this to <c>true</c> only after <c>--confirm</c> or an interactive prompt.
    /// </param>
    /// <returns>The raw JSON response string.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="apiPath"/> isn't a relative path, contains a backslash, or
    /// resolves to a scheme, host, or port other than the configured PlayFab address, before
    /// anything is attempted.
    /// </exception>
    /// <exception cref="DestructiveOperationNotConfirmedException">
    /// Thrown when <paramref name="apiPath"/> is destructive and
    /// <paramref name="confirmedDestructive"/> is <c>false</c>, before anything is attempted.
    /// </exception>
    /// <exception cref="PlayFabApiException">
    /// Thrown for every other failure. <see cref="PlayFabApiException.Outcome"/> says how far the
    /// call got, and <see cref="PlayFabApiException.OutcomeWarning"/> says when a call that isn't
    /// <c>Get*</c>, <c>List*</c>, or <c>Search*</c> may have been applied.
    /// </exception>
    public async Task<string> CallApiAsync(
        string apiPath,
        string? jsonBody,
        CancellationToken cancellationToken,
        bool confirmedDestructive = false) =>
        (await CallApiWithStatusAsync(apiPath, jsonBody, confirmedDestructive, cancellationToken)).Body;

    private readonly record struct PlayFabResponse(int StatusCode, string Status, string Body);

    private async Task<PlayFabResponse> CallApiWithStatusAsync(
        string apiPath,
        string? jsonBody,
        bool confirmedDestructive,
        CancellationToken cancellationToken)
    {
        using var dependency = TelemetryInstrumentation.StartPlayFabDependency(
            apiPath,
            _dependencyTelemetryEnabled);
        var exitStatus = 1;

        try
        {
            var response = await CallApiCoreAsync(apiPath, jsonBody, confirmedDestructive, cancellationToken);
            dependency.SetSuccess(response.StatusCode);
            exitStatus = 0;
            return response;
        }
        catch (PlayFabApiException exception)
        {
            dependency.SetFailure(exception, exception.HttpStatusCode);
            throw;
        }
        catch (Exception exception)
        {
            dependency.SetFailure(exception);
            throw;
        }
        finally
        {
            _auditLogger.WriteInvocation(apiPath, exitStatus);
        }
    }

    /// <summary>
    /// The one place that decides how far a call got (see <see cref="PlayFabCallOutcome"/>):
    /// every failure after the path and destructive checks becomes a
    /// <see cref="PlayFabApiException"/> classified by the stage it failed in.
    /// </summary>
    private async Task<PlayFabResponse> CallApiCoreAsync(
        string apiPath,
        string? jsonBody,
        bool confirmedDestructive,
        CancellationToken cancellationToken)
    {
        // The request carries the user's bearer token, so it must never leave the configured
        // PlayFab address. Every check runs before a token is acquired, and none echoes the path.
        // Validate raw input before trimming so //evil after TrimStart('/') doesn't slip through.
        if (apiPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "API path must be a relative path (e.g. Admin/GetTitleData), not an absolute or protocol-relative URI.",
                nameof(apiPath));
        }

        // A relative path combined with a base address treats '\' as '/', so \/host is //host.
        if (apiPath.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "API path must not contain a backslash. Separate segments with '/' (e.g. Admin/GetTitleData).",
                nameof(apiPath));
        }

        var effectivePath = apiPath.TrimStart('/');

        if (string.IsNullOrWhiteSpace(effectivePath) ||
            !Uri.TryCreate(effectivePath, UriKind.Relative, out _))
        {
            throw new ArgumentException(
                "API path must be a non-empty relative path (e.g. Admin/GetTitleData).",
                nameof(apiPath));
        }

        // Resolve the request URI here and send exactly this, so what is checked is what is sent.
        if (!Uri.TryCreate(_baseAddress, effectivePath, out var requestUri) ||
            !IsSameOrigin(requestUri, _baseAddress))
        {
            throw new ArgumentException(
                "API path must resolve to the configured PlayFab address; it can't name another scheme, host, or port.",
                nameof(apiPath));
        }

        // Fail-closed destructive gate at the single API chokepoint. Every caller — playfab api,
        // mps, the typed overload, and any future curated command — funnels through here, so
        // a destructive op cannot leave the process unconfirmed even if a caller forgets to
        // route through the CLI's confirmation flow.
        if (DestructiveOperations.IsDestructive(apiPath) && !confirmedDestructive)
        {
            throw new DestructiveOperationNotConfirmedException(apiPath);
        }

        // Before sending: nothing has left, whatever goes wrong.
        HttpRequestMessage request;
        try
        {
            var token = await AcquireTokenAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            request = BuildRequest(requestUri, token.AccessToken, jsonBody);
        }
        catch (Exception exception)
        {
            throw PlayFabApiException.NotSent(apiPath, exception);
        }

        // Sending: unless the failure proves the request never left, it may have reached PlayFab.
        HttpResponseMessage response;
        using (request)
        {
            try
            {
                // ResponseHeadersRead keeps the status even if the body then fails to arrive.
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception exception) when (ProvesNotSent(exception))
            {
                throw PlayFabApiException.NotSent(apiPath, exception);
            }
            catch (Exception exception)
            {
                throw PlayFabApiException.NoResponse(apiPath, DescribeNoResponse(exception, cancellationToken), exception);
            }
        }

        // Answered: the status alone decides the outcome, whether or not the body can be read.
        using (response)
        {
            var statusCode = (int)response.StatusCode;
            var status = response.StatusCode.ToString();
            string body;
            try
            {
                // HttpClient's timeout ends at the headers, so bound the body read too.
                using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bodyTimeout.CancelAfter(_httpClient.Timeout);
                body = await ReadResponseBodyWithLimitAsync(response.Content, bodyTimeout.Token);
            }
            catch (Exception exception)
            {
                throw new PlayFabApiException(
                    apiPath,
                    statusCode,
                    status,
                    rawBody: string.Empty,
                    DescribeBodyReadFailure(exception, cancellationToken),
                    exception);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new PlayFabApiException(apiPath, statusCode, status, body, bodyFailure: null, innerException: null);
            }

            return new PlayFabResponse(statusCode, status, body);
        }
    }

    /// <summary>
    /// Whether <paramref name="requestUri"/> goes to the same scheme, host, and port as
    /// <paramref name="baseAddress"/>, with no user information the base doesn't have.
    /// </summary>
    private static bool IsSameOrigin(Uri requestUri, Uri baseAddress) =>
        requestUri.IsAbsoluteUri &&
        string.Equals(requestUri.Scheme, baseAddress.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(requestUri.IdnHost, baseAddress.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        requestUri.Port == baseAddress.Port &&
        string.Equals(requestUri.UserInfo, baseAddress.UserInfo, StringComparison.Ordinal);

    private HttpRequestMessage BuildRequest(Uri requestUri, string accessToken, string? jsonBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.UserAgent.ParseAdd(CliIdentity.UserAgent);

            foreach (var header in _requestHeaders)
            {
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    throw new InvalidOperationException(
                        $"Configured header '{header.Key}' could not be added to the request. The header may be invalid or unsupported as a request header.");
                }
            }

            var body = jsonBody ?? "{}";
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether a failed send proves the request never left: a failed name lookup or TLS
    /// handshake, or HttpClient refusing the request itself. Anything else may have delivered
    /// it, including a dropped connection, because the handler can resend on a new connection
    /// after a pooled one fails.
    /// </summary>
    private static bool ProvesNotSent(Exception exception) => exception is
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError }
        or InvalidOperationException
        or NotSupportedException;

    private static string DescribeNoResponse(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => "the request was canceled",
        OperationCanceledException => "the request timed out",
        _ => $"the connection failed ({exception.Message})",
    };

    private static string DescribeBodyReadFailure(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        ResponseTooLargeException => exception.Message,
        OperationCanceledException when cancellationToken.IsCancellationRequested => "reading the response body was canceled",
        OperationCanceledException => "reading the response body timed out",
        _ => $"the response body could not be read ({exception.Message})",
    };

    /// <summary>The response body exceeded <c>PlayFab:MaxResponseBytes</c>.</summary>
    private sealed class ResponseTooLargeException(long maxResponseBytes) : InvalidOperationException(
        $"PlayFab response exceeded the configured maximum size of {maxResponseBytes} bytes. " +
        "Increase 'PlayFab:MaxResponseBytes' to allow larger responses.");

    /// <summary>
    /// Sends a typed, authenticated POST request to the specified PlayFab API path.
    /// Serializes <paramref name="request"/> to JSON and deserializes the response
    /// <c>data</c> field into <typeparamref name="TResponse"/>.
    /// </summary>
    /// <param name="apiPath">The API path, e.g. "/MultiplayerServer/GetBuild".</param>
    /// <param name="request">The request object to serialize as the JSON body.</param>
    /// <param name="cancellationToken">Cancels token acquisition, the request, and the response read.</param>
    /// <param name="confirmedDestructive">
    /// Forwarded to <see cref="CallApiAsync(string, string?, CancellationToken, bool)"/>; must be <c>true</c>
    /// for destructive operations. Defaults to <c>false</c> so this overload is also
    /// fail-closed for destructive paths.
    /// </param>
    /// <exception cref="PlayFabApiException">
    /// As for <see cref="CallApiAsync(string, string?, CancellationToken, bool)"/>; a response that
    /// can't be deserialized is <see cref="PlayFabCallOutcome.Succeeded"/>, because the call took effect.
    /// </exception>
    public async Task<TResponse> CallApiAsync<TRequest, TResponse>(
        string apiPath,
        TRequest request,
        CancellationToken cancellationToken,
        bool confirmedDestructive = false)
        where TRequest : class
        where TResponse : class
    {
        var jsonBody = JsonSerializer.Serialize(request, SerializerOptions);
        var response = await CallApiWithStatusAsync(apiPath, jsonBody, confirmedDestructive, cancellationToken);

        try
        {
            return DeserializeResponse<TResponse>(response.Body);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            throw new PlayFabApiException(
                apiPath,
                response.StatusCode,
                response.Status,
                rawBody: string.Empty,
                $"the response could not be parsed ({exception.Message})",
                exception);
        }
    }

    private static TResponse DeserializeResponse<TResponse>(string responseJson)
        where TResponse : class
    {
        // PlayFab responses wrap the payload in { "code": 200, "status": "OK", "data": { ... } }
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty("data", out var dataElement))
        {
            return JsonSerializer.Deserialize<TResponse>(dataElement.GetRawText(), SerializerOptions)
                ?? throw new InvalidOperationException("PlayFab response 'data' field deserialized to null.");
        }

        // Some responses may not have a "data" wrapper — try deserializing the whole response
        return JsonSerializer.Deserialize<TResponse>(responseJson, SerializerOptions)
            ?? throw new InvalidOperationException("PlayFab response deserialized to null.");
    }

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Formats a JSON string for display.
    /// Returns the original string if it is not valid JSON.
    /// </summary>
    /// <param name="json">The JSON string to format.</param>
    /// <param name="compact">
    /// When <c>true</c>, returns single-line JSON with no indentation (suitable for piping).
    /// When <c>false</c> (default), returns pretty-printed, indented JSON.
    /// </param>
    public static string FormatJson(string json, bool compact = false)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return compact
                ? JsonSerializer.Serialize(doc, CompactOptions)
                : JsonSerializer.Serialize(doc, PrettyPrintOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Not valid JSON (malformed, or a string that cannot be transcoded to UTF-8,
            // e.g. an unpaired UTF-16 surrogate) — return the original string unchanged.
            return json;
        }
    }

    private async Task<Microsoft.Identity.Client.AuthenticationResult> AcquireTokenAsync(
        CancellationToken cancellationToken)
    {
        var token = await _authService.AcquireTokenSilentAsync(cancellationToken);
        if (token == null)
        {
            throw new InvalidOperationException(
                $"No cached authentication token. Run '{CliIdentity.CommandName} auth login' first.");
        }

        return token;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        // Flushes and releases the audit file handle.
        _auditLogger.Dispose();
    }

    private static long ResolveMaxResponseBytes(IConfiguration configuration)
    {
        var configuredMax = configuration.GetValue<long?>("PlayFab:MaxResponseBytes");
        var maxResponseBytes = configuredMax ?? DefaultMaxResponseBytes;
        if (maxResponseBytes <= 0)
        {
            throw new InvalidOperationException(
                "Configuration key 'PlayFab:MaxResponseBytes' must be a positive number of bytes.");
        }

        return maxResponseBytes;
    }

    private async Task<string> ReadResponseBodyWithLimitAsync(
        HttpContent responseContent,
        CancellationToken cancellationToken)
    {
        if (responseContent.Headers.ContentLength is long contentLength && contentLength > _maxResponseBytes)
        {
            throw new ResponseTooLargeException(_maxResponseBytes);
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
                throw new ResponseTooLargeException(_maxResponseBytes);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
