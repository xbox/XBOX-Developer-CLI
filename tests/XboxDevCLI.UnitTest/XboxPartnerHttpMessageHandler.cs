// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Captures Partner requests and returns sequential responses for paging and polling tests.
/// </summary>
internal class XboxPartnerHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses;

    public XboxPartnerHttpMessageHandler(params HttpResponseMessage[] responses)
    {
        _responses = new Queue<Func<HttpResponseMessage>>(
            responses.Select(response => (Func<HttpResponseMessage>)(() => response)));
    }

    /// <summary>Answers one request by calling <paramref name="response"/>, which may throw.</summary>
    public XboxPartnerHttpMessageHandler(Func<HttpResponseMessage> response)
    {
        _responses = new Queue<Func<HttpResponseMessage>>([response]);
    }

    public HttpRequestMessage? LastRequest { get; private set; }

    public List<CapturedHttpRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new CapturedHttpRequest(
            request.Method,
            request.RequestUri,
            body,
            request.Content?.Headers.ContentType?.MediaType,
            request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase)));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                "No mock HTTP response remains for this request.");
        }

        return _responses.Dequeue()();
    }
}

internal sealed record CapturedHttpRequest(
    HttpMethod Method,
    Uri? RequestUri,
    string? Body,
    string? ContentType,
    IReadOnlyDictionary<string, string[]> Headers);
