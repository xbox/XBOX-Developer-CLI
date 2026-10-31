// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Captures Partner requests and returns sequential responses for paging and polling tests.
/// </summary>
internal class XboxPartnerHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;

    public XboxPartnerHttpMessageHandler(params HttpResponseMessage[] responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        if (responses.Length == 0)
        {
            throw new ArgumentException(
                "At least one response is required.",
                nameof(responses));
        }

        _responses = new Queue<HttpResponseMessage>(responses);
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

        return _responses.Dequeue();
    }
}

internal sealed record CapturedHttpRequest(
    HttpMethod Method,
    Uri? RequestUri,
    string? Body,
    string? ContentType,
    IReadOnlyDictionary<string, string[]> Headers);
