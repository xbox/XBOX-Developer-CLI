// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.UnitTest;

/// <summary>
/// A mock HTTP message handler that captures requests and returns a fixed response.
/// </summary>
internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;

    public MockHttpMessageHandler(HttpResponseMessage response)
    {
        _response = response;
    }

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastRequestBody { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    /// <summary>Runs as each request arrives, before the response is returned.</summary>
    public Action<CancellationToken>? OnSend { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastCancellationToken = cancellationToken;
        OnSend?.Invoke(cancellationToken);
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return _response;
    }
}
