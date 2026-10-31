// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace XboxDevCLI.Services;

internal static class AchievementImageBlobUploader
{
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(5);

    public static HttpClient CreateClient() =>
        new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public static async Task UploadAsync(
        HttpClient client,
        Uri sasUri,
        Stream image,
        Action<long>? reportBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanRead || !image.CanSeek)
        {
            throw new InvalidDataException("The image upload file must be readable and seekable.");
        }

        if (client.DefaultRequestHeaders.Authorization is not null)
        {
            throw new InvalidOperationException(
                "The image storage client must not carry an authorization header.");
        }

        if (sasUri is null || !sasUri.IsAbsoluteUri ||
            sasUri.Scheme != Uri.UriSchemeHttps ||
            sasUri.UserInfo.Length != 0 ||
            sasUri.Fragment.Length != 0 ||
            sasUri.Query.Length == 0)
        {
            throw new InvalidDataException("The image upload destination was invalid.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(UploadTimeout);
        using var content = new ProgressFileContent(image, reportBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Put, sasUri)
        {
            Content = content,
        };
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Created)
            {
                throw new InvalidDataException(
                    $"Image upload to storage failed (HTTP {(int)response.StatusCode}).");
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Image upload to storage timed out.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new IOException("Image upload to storage failed before completion.", ex);
        }
    }

    private sealed class ProgressFileContent(Stream image, Action<long>? reportBytes) : HttpContent
    {
        private readonly long _length = image.Length - image.Position;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            long sent = 0;
            try
            {
                int read;
                while ((read = await image.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false)) != 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    sent += read;
                    reportBytes?.Invoke(sent);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
