// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;

namespace XboxDevCLI.Services;

/// <summary>Reads a response body into memory, up to a limit.</summary>
internal static class ResponseBodyReader
{
    /// <summary>
    /// Reads the body as bytes. A body larger than <paramref name="maxBytes"/>, by its
    /// <c>Content-Length</c> or as it arrives, throws the exception <paramref name="tooLarge"/> returns.
    /// </summary>
    public static async Task<byte[]> ReadBytesAsync(
        HttpContent content,
        long maxBytes,
        Func<Exception> tooLarge,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
        {
            throw tooLarge();
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        long totalBytesRead = 0;
        while (true)
        {
            int bytesRead = await stream.ReadAsync(chunk, cancellationToken);
            if (bytesRead == 0)
            {
                return buffer.ToArray();
            }

            totalBytesRead += bytesRead;
            if (totalBytesRead > maxBytes)
            {
                throw tooLarge();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
        }
    }

    /// <summary>
    /// Reads the body as <see cref="ReadBytesAsync"/> does, and decodes it as UTF-8 unless it starts
    /// with a byte order mark that names another encoding.
    /// </summary>
    public static async Task<string> ReadTextAsync(
        HttpContent content,
        long maxBytes,
        Func<Exception> tooLarge,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadBytesAsync(content, maxBytes, tooLarge, cancellationToken);
        using var reader = new StreamReader(
            new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
