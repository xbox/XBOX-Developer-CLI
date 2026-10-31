// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using System.Net.Http.Headers;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public sealed class AchievementImageBlobUploaderTests
{
    private static readonly Uri SasUri = new("https://example.blob.core.windows.net/images/image.png?sig=secret");

    [Fact]
    public void StorageClientUsesUploaderTimeoutRatherThanHttpClientTimeout()
    {
        using var client = AchievementImageBlobUploader.CreateClient();
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task UploadSendsOneAuthenticatedBySasPutWithBytesAndProgress()
    {
        byte[] image = Enumerable.Range(0, 200000).Select(i => (byte)i).ToArray();
        var progress = new List<long>();
        int sends = 0;
        using var handler = new CallbackHandler(async (request, token) =>
        {
            sends++;
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal(SasUri, request.RequestUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("BlockBlob", Assert.Single(request.Headers.GetValues("x-ms-blob-type")));
            Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType?.MediaType);
            Assert.Equal(image.Length, request.Content.Headers.ContentLength);
            Assert.Equal(image, await request.Content.ReadAsByteArrayAsync(token));
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        using var client = new HttpClient(handler);
        await using var source = new MemoryStream(image);

        await AchievementImageBlobUploader.UploadAsync(
            client, SasUri, source, progress.Add, CancellationToken.None);

        Assert.Equal(1, sends);
        Assert.Equal(image.Length, progress[^1]);
        Assert.True(progress.SequenceEqual(progress.Order()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task RejectionDoesNotRetryOrExposeSas(HttpStatusCode status)
    {
        int sends = 0;
        using var handler = new CallbackHandler((_, _) =>
        {
            sends++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("sig=secret"),
            });
        });
        using var client = new HttpClient(handler);
        await using var source = new MemoryStream([1, 2, 3]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AchievementImageBlobUploader.UploadAsync(
                client, SasUri, source, null, CancellationToken.None));

        Assert.Equal(1, sends);
        Assert.Contains(((int)status).ToString(), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportFailureDoesNotExposeSasOrRetry()
    {
        int sends = 0;
        using var handler = new CallbackHandler((_, _) =>
        {
            sends++;
            throw new HttpRequestException("Failed for " + SasUri);
        });
        using var client = new HttpClient(handler);
        await using var source = new MemoryStream([1, 2, 3]);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            AchievementImageBlobUploader.UploadAsync(
                client, SasUri, source, null, CancellationToken.None));

        Assert.Equal(1, sends);
        Assert.Equal("Image upload to storage failed before completion.", error.Message);
        Assert.DoesNotContain("sig=", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.blob.core.windows.net/image.png?sig=secret")]
    [InlineData("https://example.blob.core.windows.net/image.png")]
    [InlineData("https://user@example.blob.core.windows.net/image.png?sig=secret")]
    [InlineData("https://example.blob.core.windows.net/image.png?sig=secret#fragment")]
    public async Task InvalidUploadDestinationFailsBeforeDispatch(string destination)
    {
        using var handler = new CallbackHandler((_, _) =>
            throw new Xunit.Sdk.XunitException("Unexpected storage request"));
        using var client = new HttpClient(handler);
        await using var source = new MemoryStream([1, 2, 3]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AchievementImageBlobUploader.UploadAsync(
                client, new Uri(destination), source, null, CancellationToken.None));

        Assert.Equal("The image upload destination was invalid.", error.Message);
    }

    [Fact]
    public async Task GatewayBearerTokenCannotBeSentToBlobDestination()
    {
        using var handler = new CallbackHandler((_, _) =>
            throw new Xunit.Sdk.XunitException("Unexpected storage request"));
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "gateway-token");
        await using var source = new MemoryStream([1, 2, 3]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AchievementImageBlobUploader.UploadAsync(
                client, SasUri, source, null, CancellationToken.None));

        Assert.Contains("must not carry an authorization header", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationBeforeUploadDoesNotDispatch()
    {
        using var handler = new CallbackHandler((_, _) =>
            throw new Xunit.Sdk.XunitException("Unexpected storage request"));
        using var client = new HttpClient(handler);
        await using var source = new MemoryStream([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AchievementImageBlobUploader.UploadAsync(
                client, SasUri, source, null, cancellation.Token));
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }
}
