// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;

namespace XboxDevCLI.UnitTest;

/// <summary>A request as the handler received it, with its body read.</summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string[]> Headers,
    byte[]? Body)
{
    public string Path => Uri.AbsolutePath;

    public string Text => Body is null ? string.Empty : Encoding.UTF8.GetString(Body);

    public string? Header(string name) =>
        Headers.TryGetValue(name, out string[]? values) ? string.Join(",", values) : null;
}

/// <summary>
/// Records every request, including concurrent ones, and answers with <c>respond</c>, which may
/// throw to simulate a network failure.
/// </summary>
internal sealed class RoutingHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = [.. header.Value];
        }

        if (request.Content is not null)
        {
            // Computing the length adds Content-Length to the headers, as the transport would send it.
            _ = request.Content.Headers.ContentLength;
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = [.. header.Value];
            }
        }

        var recorded = new RecordedRequest(request.Method, request.RequestUri!, headers, body);
        lock (_gate)
        {
            _requests.Add(recorded);
        }

        return respond(recorded);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        response.Headers.Add("x-ms-correlation-id", "response-correlation");
        return response;
    }
}
