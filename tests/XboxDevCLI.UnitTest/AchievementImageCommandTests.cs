// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Output;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public sealed class AchievementImageCommandTests
{
    private const string Scid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string AssetId = "11111111-2222-3333-4444-555555555555";
    private const string Image = """
        {"assetId":"11111111-2222-3333-4444-555555555555","scid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","cdnUrl":"https://cdn.example/badge.png","widthInPixels":128,"heightInPixels":96,"imageType":"png","isPublic":true}
        """;
    private static readonly string ImageWithIconImageId =
        Image[..^1] + ",\"iconImageId\":\"" + AssetId + "\"}";
    private const string ImageList = "{\"images\":[" + Image + "]}";
    private const string InitializeImage = """
        {"uploadUri":"https://example.blob.core.windows.net/images/badge.png?sig=secret","xfusId":"11111111-2222-3333-4444-555555555555","uploadToken":"opaque-upload-secret","expiresAt":"2099-01-01T00:00:00Z"}
        """;
    private static readonly byte[] Png = CreatePngHeader(1920, 1080);

    [Fact]
    public async Task Upload_RegistersDirectBlobAndPrintsReturnedImage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image);
            using var blob = new BlobHandler();
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path, "--output", "json-compact"],
                handler, uploadHandler: blob);

            Assert.Equal(0, result.Code);
            Assert.Equal(ImageWithIconImageId + Environment.NewLine, result.Out);
            Assert.Empty(result.Error);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
            Assert.Equal(
                "/xboxpartner/v1/xboxlive/achievement-images/upload/initialize",
                handler.Requests[0].Path);
            Assert.Equal("Bearer", handler.Requests[0].Auth?.Scheme);
            Assert.Equal("caller-token", handler.Requests[0].Auth?.Parameter);
            Assert.Equal("application/json", handler.Requests[0].MediaType);
            using var initialBody = JsonDocument.Parse(handler.Requests[0].Body!);
            Assert.Equal(12345, initialBody.RootElement.GetProperty("sellerId").GetInt64());
            Assert.Equal(Scid, initialBody.RootElement.GetProperty("scid").GetString());
            Assert.Equal(Path.GetFileName(path), initialBody.RootElement.GetProperty("fileName").GetString());
            Assert.Equal(Png.Length, initialBody.RootElement.GetProperty("sizeBytes").GetInt64());
            Assert.Equal("/xboxpartner/v1/xboxlive/achievement-images/upload/register",
                handler.Requests[1].Path);
            using var registerBody = JsonDocument.Parse(handler.Requests[1].Body!);
            Assert.Equal(AssetId, registerBody.RootElement.GetProperty("xfusId").GetString());
            Assert.Equal("opaque-upload-secret",
                registerBody.RootElement.GetProperty("uploadToken").GetString());
            Assert.Equal(12345, registerBody.RootElement.GetProperty("sellerId").GetInt64());
            Assert.Equal(Scid, registerBody.RootElement.GetProperty("scid").GetString());
            var sent = Assert.Single(blob.Requests);
            Assert.Equal(HttpMethod.Put, sent.Method);
            Assert.Equal(Png, sent.Bytes);
            Assert.Equal("BlockBlob", sent.BlobType);
            Assert.Null(sent.Auth);
            Assert.DoesNotContain("opaque-upload-secret", result.Error + result.Out,
                StringComparison.Ordinal);
            Assert.DoesNotContain("sig=secret", result.Error + result.Out,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_TableRendersRegisteredImageWithoutRetry()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image);
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path, "--output", "table"], handler);

            Assert.Equal(0, result.Code);
            Assert.Empty(result.Error);
            AssertImageTable(result.Out, AssetId);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("/xboxpartner/v1/xboxlive/achievement-images/upload/register",
                handler.Requests[1].Path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Upload_GatewayFailureDoesNotRetryRegistration(HttpStatusCode status)
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image)
            {
                RegisterStatus = status,
                RegisterResponse = """{"title":"Registration outcome unknown"}""",
            };
            using var blob = new BlobHandler();
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path, "--output", "json-compact"],
                handler, uploadHandler: blob);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("Upload outcome unknown.", result.Error, StringComparison.Ordinal);
            Assert.Contains($"xbox xbl achievement-image list --scid {Scid} --seller-id 12345",
                result.Error, StringComparison.Ordinal);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Single(blob.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_TransportDropDoesNotRetryRegistration()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var gateway = new ImageHandler(HttpStatusCode.OK, Image)
            {
                RegisterFailure = new HttpRequestException("Connection dropped"),
            };
            using var blob = new BlobHandler();
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], gateway, uploadHandler: blob);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("Upload outcome unknown.", result.Error, StringComparison.Ordinal);
            Assert.Contains($"xbox xbl achievement-image list --scid {Scid} --seller-id 12345",
                result.Error, StringComparison.Ordinal);
            Assert.Equal(2, gateway.Requests.Count);
            Assert.Single(blob.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_TransportErrorRedactsSessionSecrets()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image)
            {
                RegisterFailure = new HttpRequestException(
                    "Connection dropped for opaque-upload-secret at https://example.blob.core.windows.net/images/badge.png?sig=secret"),
            };
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("Upload outcome unknown.", result.Error, StringComparison.Ordinal);
            Assert.Contains("[redacted]", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("opaque-upload-secret", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("sig=secret", result.Error, StringComparison.Ordinal);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_RestartsOnceForExplicitSessionConflict()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            const string secondId = "22222222-3333-4444-5555-666666666666";
            var handler = new ImageHandler(HttpStatusCode.OK, Image)
            {
                InitializeResponses = new Queue<string>([InitializeImage,
                    $$$"""{"uploadUri":"https://example.blob.core.windows.net/images/second.png?sig=second-secret","xfusId":"{{{secondId}}}","uploadToken":"second-upload-secret","expiresAt":"2099-01-01T00:00:00Z"}"""]),
                RegisterStatuses = new Queue<HttpStatusCode>(
                    [HttpStatusCode.Conflict, HttpStatusCode.OK]),
                RegisterResponses = new Queue<string>(
                    ["""{"code":"image_upload_restart_required","detail":"Session expired"}"""]),
            };
            using var blob = new BlobHandler();
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path, "--output", "json-compact"],
                handler, uploadHandler: blob);

            Assert.Equal(0, result.Code);
            Assert.Equal(ImageWithIconImageId + Environment.NewLine, result.Out);
            Assert.Empty(result.Error);
            Assert.Equal(4, handler.Requests.Count);
            Assert.Equal("/xboxpartner/v1/xboxlive/achievement-images/upload/initialize",
                handler.Requests[2].Path);
            Assert.Equal("/xboxpartner/v1/xboxlive/achievement-images/upload/register",
                handler.Requests[3].Path);
            using var first = JsonDocument.Parse(handler.Requests[1].Body!);
            using var second = JsonDocument.Parse(handler.Requests[3].Body!);
            Assert.Equal(AssetId, first.RootElement.GetProperty("xfusId").GetString());
            Assert.Equal(secondId, second.RootElement.GetProperty("xfusId").GetString());
            Assert.Equal("second-upload-secret", second.RootElement.GetProperty("uploadToken").GetString());
            Assert.Equal(2, blob.Requests.Count);
            Assert.All(blob.Requests, request => Assert.Equal(Png, request.Bytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("""{"code":"image_upload_restart_required","detail":"Session expired"}""", 4,
        "Rerun upload to start a new image.")]
    [InlineData("""{"code":"image_upload_png_verification_failed","detail":"Invalid PNG"}""", 2,
        "The uploaded blob is not a valid PNG.")]
    [InlineData("""{"code":"unrelated_conflict","detail":"Other conflict"}""", 2,
        "Image registration was rejected.")]
    public async Task Upload_RegistrationCodeControlsRestartAndResolution(
        string errorBody, int expectedRequests, string resolution)
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            bool pngError = errorBody.Contains("png_verification_failed", StringComparison.Ordinal);
            var handler = new ImageHandler(HttpStatusCode.OK, Image)
            {
                RegisterStatus = pngError ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict,
                RegisterResponse = errorBody,
            };
            using var blob = new BlobHandler();
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler, uploadHandler: blob);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains(resolution, result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("Upload outcome unknown.", result.Error, StringComparison.Ordinal);
            Assert.Equal(expectedRequests, handler.Requests.Count);
            Assert.Equal(expectedRequests / 2, blob.Requests.Count);
            Assert.DoesNotContain("opaque-upload-secret", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("sig=secret", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_InitializeBadGatewayDoesNotStartBlobTransfer()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image)
            {
                InitializeStatus = HttpStatusCode.BadGateway,
                InitializeResponse = """{"code":"xfus_unavailable","detail":"Initialization failed"}""",
            };
            using var blob = new BlobHandler();
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler, uploadHandler: blob);
            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("Initialization failed", result.Error, StringComparison.Ordinal);
            Assert.Single(handler.Requests);
            Assert.Empty(blob.Requests);
            Assert.False(HasRecoveryInstructions(result.Error));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Upload_RejectedRegistrationExplainsNewUploadWithoutRetry(HttpStatusCode status)
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image)
            {
                RegisterStatus = status,
                RegisterResponse = """{"title":"Registration rejected"}""",
            };
            using var blob = new BlobHandler();
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler, uploadHandler: blob);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("Registration rejected", result.Error, StringComparison.Ordinal);
            Assert.Contains("Rerun upload to create a new image asset.", result.Error,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Image requirements:", result.Error, StringComparison.Ordinal);
            Assert.False(HasRecoveryInstructions(result.Error));
            Assert.Equal(2, handler.Requests.Count);
            Assert.Single(blob.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_StorageRejectionDoesNotRegisterOrExposeSas()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var gateway = new ImageHandler(HttpStatusCode.OK, Image);
            using var blob = new BlobHandler { Status = HttpStatusCode.Forbidden };
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], gateway, uploadHandler: blob);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("HTTP 403", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("sig=secret", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("opaque-upload-secret", result.Error, StringComparison.Ordinal);
            Assert.False(HasRecoveryInstructions(result.Error));
            Assert.Single(gateway.Requests);
            Assert.Single(blob.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("""{"uploadUri":"https://example.blob.core.windows.net/a?sig=secret","xfusId":"00000000-0000-0000-0000-000000000000","uploadToken":"secret","expiresAt":"2099-01-01T00:00:00Z"}""")]
    [InlineData("""{"uploadUri":"https://example.blob.core.windows.net/a?sig=secret","xfusId":"11111111-2222-3333-4444-555555555555","uploadToken":"","expiresAt":"2099-01-01T00:00:00Z"}""")]
    [InlineData("""{"uploadUri":"https://example.blob.core.windows.net/a?sig=secret","xfusId":"11111111-2222-3333-4444-555555555555","uploadToken":"secret","expiresAt":"2020-01-01T00:00:00Z"}""")]
    public async Task Upload_InvalidInitializationNeverUploadsOrRegisters(string body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var gateway = new ImageHandler(HttpStatusCode.OK, Image)
            {
                InitializeResponse = body,
            };
            using var blob = new BlobHandler();
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], gateway, uploadHandler: blob);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("initialization response was invalid", result.Error,
                StringComparison.Ordinal);
            Assert.DoesNotContain("sig=secret", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("uploadToken", result.Error, StringComparison.Ordinal);
            Assert.Single(gateway.Requests);
            Assert.Empty(blob.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Upload_RegisterErrorsRedactSessionTokenAndWriteSas(HttpStatusCode status)
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image)
            {
                RegisterStatus = status,
                RegisterResponse = JsonSerializer.Serialize(new
                {
                    title = "Rejected opaque-upload-secret",
                    detail = "Destination https://example.blob.core.windows.net/images/badge.png?sig=secret",
                    errors = new Dictionary<string, string[]>
                    {
                        ["UploadToken"] = ["opaque-upload-secret was rejected."],
                    },
                }),
            };
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.DoesNotContain("opaque-upload-secret", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("sig=secret", result.Error, StringComparison.Ordinal);
            Assert.Contains("[redacted]", result.Error, StringComparison.Ordinal);
            Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), result.Error,
                StringComparison.Ordinal);
            Assert.Equal((int)status >= 500, result.Error.Contains(
                "Upload outcome unknown", StringComparison.Ordinal));
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_PreservesPreviewUrlsAndUnknownFieldsWithDerivedIconImageId()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            string returned = Image[..^1] +
                ""","imagePreviewUrl":"https://preview.example/image","thumbnailPreviewUrl":"https://preview.example/thumb","extra":{"key":true},"iconImageId":"00000000-0000-0000-0000-000000000000"}""";
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path, "--output", "json-compact"],
                new ImageHandler(HttpStatusCode.OK, returned));

            Assert.Equal(0, result.Code);
            using var parsed = JsonDocument.Parse(result.Out);
            JsonElement root = parsed.RootElement;
            Assert.Equal(AssetId, root.GetProperty("iconImageId").GetString());
            Assert.Equal(AssetId, root.GetProperty("assetId").GetString());
            Assert.Equal("https://preview.example/image",
                root.GetProperty("imagePreviewUrl").GetString());
            Assert.Equal("https://preview.example/thumb",
                root.GetProperty("thumbnailPreviewUrl").GetString());
            Assert.True(root.GetProperty("extra").GetProperty("key").GetBoolean());
            Assert.Equal(1, root.EnumerateObject()
                .Count(property => property.NameEquals("iconImageId")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_OmitsSellerIdWhenCallerDoesNotSpecifyOne()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var gateway = new ImageHandler(HttpStatusCode.OK, Image);
            using var client = CreateClient(gateway);
            using var blob = CreateBlobClient();

            await client.UploadAchievementImageAsync(
                null, Guid.Parse(Scid), path, CancellationToken.None, blob);

            Assert.Equal(2, gateway.Requests.Count);
            foreach (var request in gateway.Requests)
            {
                using var body = JsonDocument.Parse(request.Body!);
                Assert.False(body.RootElement.TryGetProperty("sellerId", out _));
                Assert.Equal(Scid, body.RootElement.GetProperty("scid").GetString());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_RejectsWrongExtensionBeforeRequest()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.jpeg");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image);
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains(".png filename", result.Error, StringComparison.Ordinal);
            Assert.False(HasRecoveryInstructions(result.Error));
            Assert.Empty(handler.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_AcceptsCaseInsensitivePngExtension()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.PNG");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image);
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler);

            Assert.Equal(0, result.Code);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_RejectsInvalidPngHeadersAndDimensionsBeforeNetwork()
    {
        var invalidHeaders = new List<byte[]>
        {
            Png[..32],
            CreatePngHeader(0, 1080),
            CreatePngHeader(1920, 0),
            CreatePngHeader(1919, 1080),
            CreatePngHeader(1920, 1081),
        };
        byte[] wrongChunkLength = (byte[])Png.Clone();
        wrongChunkLength[11] = 12;
        invalidHeaders.Add(wrongChunkLength);
        byte[] wrongChunkType = (byte[])Png.Clone();
        wrongChunkType[12] = (byte)'J';
        invalidHeaders.Add(wrongChunkType);

        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image);
            using var client = CreateClient(handler);
            foreach (byte[] header in invalidHeaders)
            {
                await File.WriteAllBytesAsync(path, header);
                var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    client.UploadAchievementImageAsync(
                        12345, Guid.Parse(Scid), path, CancellationToken.None));
                Assert.Contains("1920x1080", error.Message, StringComparison.Ordinal);
            }

            Assert.Empty(handler.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(20 * 1024 * 1024, true)]
    [InlineData(20 * 1024 * 1024 + 1, false)]
    public async Task Upload_ChecksFileSizeAtTwentyMiBBoundary(long size, bool accepted)
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            await file.WriteAsync(Png);
            file.SetLength(size);
        }

        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image);
            using var client = CreateClient(handler);
            if (accepted)
            {
                using var blob = CreateBlobClient();
                var image = await client.UploadAchievementImageAsync(
                    12345, Guid.Parse(Scid), path, CancellationToken.None, blob);
                Assert.Equal(Guid.Parse(AssetId), image.AssetId);
                Assert.Equal(2, handler.Requests.Count);
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    client.UploadAchievementImageAsync(
                        12345, Guid.Parse(Scid), path, CancellationToken.None));
                Assert.Contains("20 MiB", error.Message, StringComparison.Ordinal);
                Assert.Empty(handler.Requests);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Get_UsesJsonLookupAndPrintsOneImage()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, Image);
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "get", "--scid", Scid,
                "--asset-id", AssetId, "--seller-id", "12345", "--output", "json-compact"],
            handler);

        Assert.Equal(0, result.Code);
        Assert.Equal(ImageWithIconImageId + Environment.NewLine, result.Out);
        Assert.Empty(result.Error);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/xboxpartner/v1/xboxlive/achievement-image", handler.Requests[0].Path);
        Assert.Equal("application/json", handler.Requests[0].MediaType);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(12345, body.RootElement.GetProperty("sellerId").GetInt64());
        Assert.Equal(Guid.Parse(Scid), body.RootElement.GetProperty("scid").GetGuid());
        Assert.Equal(AssetId, body.RootElement.GetProperty("assetId").GetString());
    }

    [Fact]
    public async Task Get_OmittedSellerIsDelegatedToGateway()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, Image);
        using var client = CreateClient(handler);
        XboxLiveAchievementImage response = await client.GetAchievementImageAsync(
            null, Guid.Parse(Scid), Guid.Parse(AssetId), CancellationToken.None);

        Assert.Equal(Image, response.RawResponse);
        Assert.Equal(Guid.Parse(AssetId), response.AssetId);
        Assert.Equal(Guid.Parse(Scid), response.Scid);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("sellerId").ValueKind);
    }

    [Fact]
    public async Task Get_TypedImageKeepsNullableFieldsAndUnknownJson()
    {
        const string response = """
            {"assetId":"11111111-2222-3333-4444-555555555555","scid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","cdnUrl":null,"newGatewayField":{"value":42}}
            """;
        var handler = new ImageHandler(HttpStatusCode.OK, response);
        using var client = CreateClient(handler);
        XboxLiveAchievementImage image = await client.GetAchievementImageAsync(
            12345, Guid.Parse(Scid), Guid.Parse(AssetId), CancellationToken.None);

        Assert.Equal(Guid.Parse(AssetId), image.AssetId);
        Assert.Equal(Guid.Parse(Scid), image.Scid);
        Assert.Null(image.CdnUrl);
        Assert.Null(image.WidthInPixels);
        Assert.Null(image.IsPublic);
        Assert.Equal(response, image.RawResponse);
        var output = await InvokeAsync(
            ["xbl", "achievement-image", "get", "--scid", Scid,
                "--asset-id", AssetId, "--seller-id", "12345", "--output", "json-compact"],
            new ImageHandler(HttpStatusCode.OK, response));
        using var projected = JsonDocument.Parse(output.Out);
        Assert.Equal(AssetId, projected.RootElement.GetProperty("iconImageId").GetString());
        Assert.Equal(AssetId, projected.RootElement.GetProperty("assetId").GetString());
        Assert.Equal(42, projected.RootElement.GetProperty("newGatewayField")
            .GetProperty("value").GetInt32());
        Assert.Equal(JsonValueKind.Null, projected.RootElement.GetProperty("cdnUrl").ValueKind);
        Assert.False(projected.RootElement.TryGetProperty("widthInPixels", out _));
    }

    [Fact]
    public async Task List_SendsReadOnlyJsonLookupAndReturnsImagesForGet()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, ImageList);
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "list", "--scid", Scid,
                "--seller-id", "12345", "--output", "json-compact"], handler);

        Assert.Equal(0, result.Code);
        Assert.Equal("[" + ImageWithIconImageId + "]" + Environment.NewLine, result.Out);
        Assert.Empty(result.Error);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/xboxpartner/v1/xboxlive/achievement-images", handler.Requests[0].Path);
        Assert.Equal("application/json", handler.Requests[0].MediaType);
        Assert.Equal("caller-token", handler.Requests[0].Auth?.Parameter);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(12345, body.RootElement.GetProperty("sellerId").GetInt64());
        Assert.Equal(Guid.Parse(Scid), body.RootElement.GetProperty("scid").GetGuid());
        Assert.Equal(XboxPartnerClient.AchievementImagesPageSize,
            body.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, body.RootElement.EnumerateObject().Count());
        using var images = JsonDocument.Parse(result.Out);
        Assert.Equal(AssetId, images.RootElement[0].GetProperty("assetId").GetString());
    }

    [Fact]
    public async Task List_FollowsContinuationTokensWithOneCorrelationId()
    {
        const string otherAssetId = "66666666-7777-8888-9999-000000000000";
        string second = Image[..^1] + ""","futureField":"kept"}""";
        second = second.Replace(AssetId, otherAssetId, StringComparison.Ordinal);
        var handler = new ImageHandler(HttpStatusCode.OK, "[]")
        {
            ListResponses = new([
                "{\"images\":[" + Image + "],\"continuationToken\":\"page-2\"}",
                "{\"images\":[" + second + "],\"continuationToken\":null}",
            ]),
        };
        using var client = CreateClient(handler);
        var pages = new List<int>();
        XboxLiveAchievementImageCollection result = await client.ListAchievementImagesAsync(
            12345, Guid.Parse(Scid), CancellationToken.None, pages.Add);

        Assert.Equal([Guid.Parse(AssetId), Guid.Parse(otherAssetId)],
            result.Images.Select(image => image.AssetId));
        Assert.Equal([1, 1], pages);
        Assert.Equal("[" + Image + "," + second + "]", result.RawResponse);
        Assert.Equal(2, handler.Requests.Count);
        using var firstBody = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(XboxPartnerClient.AchievementImagesPageSize,
            firstBody.RootElement.GetProperty("pageSize").GetInt32());
        Assert.False(firstBody.RootElement.TryGetProperty("continuationToken", out _));
        using var secondBody = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("page-2", secondBody.RootElement.GetProperty("continuationToken").GetString());
        Assert.False(secondBody.RootElement.TryGetProperty("pageSize", out _));
        Assert.Equal(12345, secondBody.RootElement.GetProperty("sellerId").GetInt64());
        Assert.NotNull(handler.CorrelationIds[0]);
        Assert.Equal(handler.CorrelationIds[0], handler.CorrelationIds[1]);
    }

    [Fact]
    public async Task List_SinglePagedObjectReturnsImagesArrayText()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, "[]")
        {
            ListResponses = new(["{\"images\":[" + Image + "]}"]),
        };
        using var client = CreateClient(handler);
        XboxLiveAchievementImageCollection result = await client.ListAchievementImagesAsync(
            12345, Guid.Parse(Scid), CancellationToken.None);

        Assert.Equal("[" + Image + "]", result.RawResponse);
        Assert.Single(result.Images);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task List_EmptyContinuationTokenEndsListing()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, "[]")
        {
            ListResponses = new(["{\"images\":[],\"continuationToken\":\"\"}"]),
        };
        using var client = CreateClient(handler);
        XboxLiveAchievementImageCollection result = await client.ListAchievementImagesAsync(
            12345, Guid.Parse(Scid), CancellationToken.None);

        Assert.Empty(result.Images);
        Assert.Equal("[]", result.RawResponse);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task List_RepeatedContinuationTokenFailsWithoutSuccessOutput()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, "[]")
        {
            ListResponses = new([
                "{\"images\":[" + Image + "],\"continuationToken\":\"loop\"}",
                "{\"images\":[" + Image + "],\"continuationToken\":\"loop\"}",
            ]),
        };
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "list", "--scid", Scid,
                "--seller-id", "12345"], handler);

        Assert.Equal(1, result.Code);
        Assert.Empty(result.Out);
        Assert.Contains("repeated a continuation token", result.Error, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("list", HttpStatusCode.BadGateway, """{"title":"Unavailable"}""", "Unavailable")]
    [InlineData("list", HttpStatusCode.OK, "{}", "must contain the images array")]
    [InlineData("list", HttpStatusCode.OK, """{"images":[{"cdnUrl":"https://cdn.example/badge.png"}]}""", "must contain an assetId")]
    [InlineData("download", HttpStatusCode.BadGateway, """{"title":"Unavailable"}""", "Unavailable")]
    [InlineData("download", HttpStatusCode.OK, "{}", "must contain the images array")]
    public async Task List_FailureOnALaterPageWritesTheCorrelationIdAndNothingToStdout(
        string action,
        HttpStatusCode status,
        string body,
        string expected)
    {
        var handler = new ImageHandler(status, body)
        {
            ListResponses = new(["{\"images\":[" + Image + "],\"continuationToken\":\"page-2\"}"]),
        };
        string[] args = action == "list"
            ? ["xbl", "achievement-image", "list", "--scid", Scid, "--seller-id", "12345"]
            : ["xbl", "achievement-image", "download", "--scid", Scid, "--all",
                "--seller-id", "12345", "--dest", Path.Combine(Path.GetTempPath(), $"unused-{Guid.NewGuid():N}")];

        var result = await InvokeAsync(args, handler);

        Assert.Equal(1, result.Code);
        Assert.Empty(result.Out);
        Assert.Equal(2, handler.Requests.Count);
        string correlationId = Assert.IsType<string>(handler.CorrelationIds[1]);
        Assert.Equal(handler.CorrelationIds[0], correlationId);
        Assert.StartsWith(
            $"x-ms-correlation-id: {correlationId}{Environment.NewLine}",
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_InvalidImageFailsBeforeFetchingTheNextPage()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, "[]")
        {
            ListResponses = new([
                """{"images":[{"cdnUrl":"https://cdn.example/badge.png"}],"continuationToken":"page-2"}""",
                "{\"images\":[" + Image + "]}",
            ]),
        };
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "list", "--scid", Scid,
                "--seller-id", "12345"], handler);

        Assert.Equal(1, result.Code);
        Assert.Empty(result.Out);
        Assert.Single(handler.Requests);
        Assert.StartsWith("x-ms-correlation-id: ", result.Error, StringComparison.Ordinal);
        Assert.Contains("must contain an assetId", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_EmptyInventoryReturnsEmptyArray()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, ImagesPage("[]"));
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "list", "--scid", Scid,
                "--seller-id", "12345"], handler);

        Assert.Equal(0, result.Code);
        Assert.Equal("[]" + Environment.NewLine, result.Out);
        Assert.Empty(result.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task List_TableRendersOneRowPerRegisteredImage()
    {
        const string otherAssetId = "66666666-7777-8888-9999-000000000000";
        var handler = new ImageHandler(HttpStatusCode.OK, ImagesPage(
            "[" + Image + "," + Image.Replace(AssetId, otherAssetId, StringComparison.Ordinal) + "]"));
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "list", "--scid", Scid,
                "--seller-id", "12345", "--output", "table"], handler);

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        AssertImageTable(result.Out, AssetId, otherAssetId);
        Assert.Single(handler.Requests);
        Assert.Equal("/xboxpartner/v1/xboxlive/achievement-images", handler.Requests[0].Path);
    }

    [Fact]
    public async Task List_EmptyTableReportsNoResults()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, ImagesPage("[]"));
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "list", "--scid", Scid,
                "--seller-id", "12345", "--output", "table"], handler);

        Assert.Equal(0, result.Code);
        Assert.Equal("No results." + Environment.NewLine, result.Out);
        Assert.Empty(result.Error);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("https://cdn.example/path/TestAchievement001%2EPNG?sig=secret", "TestAchievement001.PNG")]
    [InlineData("https://cdn.example/path/%2E%2E?sig=secret", "-")]
    public async Task ImageTable_ProjectsSafeDecodedCdnFilenameWithoutChangingJson(
        string url, string expected)
    {
        string response = Image.Replace("https://cdn.example/badge.png", url,
            StringComparison.Ordinal);
        var table = await InvokeAsync(
            ["xbl", "achievement-image", "get", "--scid", Scid,
                "--asset-id", AssetId, "--seller-id", "12345", "--output", "table"],
            new ImageHandler(HttpStatusCode.OK, response));
        var json = await InvokeAsync(
            ["xbl", "achievement-image", "get", "--scid", Scid,
                "--asset-id", AssetId, "--seller-id", "12345", "--output", "json-compact"],
            new ImageHandler(HttpStatusCode.OK, response));

        Assert.Equal(0, table.Code);
        Assert.Contains("CDN FILENAME", table.Out, StringComparison.Ordinal);
        Assert.Contains(expected, table.Out.Split(Environment.NewLine)[2], StringComparison.Ordinal);
        Assert.DoesNotContain("sig=secret", table.Out, StringComparison.Ordinal);
        using var projected = JsonDocument.Parse(json.Out);
        Assert.Equal(url, projected.RootElement.GetProperty("cdnUrl").GetString());
        Assert.Equal(AssetId, projected.RootElement.GetProperty("iconImageId").GetString());
    }

    [Fact]
    public async Task List_OmittedSellerIsDelegatedToGateway()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, ImagesPage("[]"));
        using var client = CreateClient(handler);
        XboxLiveAchievementImageCollection response = await client.ListAchievementImagesAsync(
            null, Guid.Parse(Scid), CancellationToken.None);

        Assert.Equal("[]", response.RawResponse);
        Assert.Empty(response.Images);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("sellerId").ValueKind);
    }

    [Fact]
    public async Task List_TypedImagesPreserveBareArrayAndUnknownFields()
    {
        string response = "[" + Image[..^1] + ""","futureField":"kept"}]""";
        var handler = new ImageHandler(HttpStatusCode.OK, ImagesPage(response));
        using var client = CreateClient(handler);
        XboxLiveAchievementImageCollection result = await client.ListAchievementImagesAsync(
            12345, Guid.Parse(Scid), CancellationToken.None);

        Assert.Equal(response, result.RawResponse);
        var image = Assert.Single(result.Images);
        Assert.Equal(Guid.Parse(AssetId), image.AssetId);
        Assert.Equal(128, image.WidthInPixels);
        Assert.True(image.IsPublic);
        var output = await InvokeAsync(
            ["xbl", "achievement-image", "list", "--scid", Scid,
                "--seller-id", "12345", "--output", "json-compact"],
            new ImageHandler(HttpStatusCode.OK, ImagesPage(response)));
        using var projected = JsonDocument.Parse(output.Out);
        var item = Assert.Single(projected.RootElement.EnumerateArray());
        Assert.Equal(AssetId, item.GetProperty("iconImageId").GetString());
        Assert.Equal(AssetId, item.GetProperty("assetId").GetString());
        Assert.Equal("kept", item.GetProperty("futureField").GetString());
    }

    [Theory]
    [InlineData("get")]
    [InlineData("list")]
    public async Task ImageJson_ReplacesGatewayIconImageIdWithValidatedAssetId(string action)
    {
        const string response = """
            {"assetId":"11111111-2222-3333-4444-555555555555","scid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","iconImageId":"wrong","futureField":3,"iconImageId":"still-wrong"}
            """;
        string[] args = action == "get"
            ? ["xbl", "achievement-image", "get", "--scid", Scid,
                "--asset-id", AssetId, "--seller-id", "12345", "--output", "json-compact"]
            : ["xbl", "achievement-image", "list", "--scid", Scid,
                "--seller-id", "12345", "--output", "json-compact"];
        var result = await InvokeAsync(
            args, new ImageHandler(HttpStatusCode.OK,
                action == "get" ? response : ImagesPage("[" + response + "]")));

        Assert.Equal(0, result.Code);
        using var projected = JsonDocument.Parse(result.Out);
        JsonElement image = action == "get"
            ? projected.RootElement
            : projected.RootElement[0];
        Assert.Equal(AssetId, image.GetProperty("iconImageId").GetString());
        Assert.Equal(AssetId, image.GetProperty("assetId").GetString());
        Assert.Equal(3, image.GetProperty("futureField").GetInt32());
        Assert.Single(image.EnumerateObject(), property => property.Name == "iconImageId");
    }

    [Fact]
    public async Task Get_DefaultJsonIncludesIconImageIdAndGatewayFields()
    {
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "get", "--scid", Scid,
                "--asset-id", AssetId, "--seller-id", "12345"],
            new ImageHandler(HttpStatusCode.OK, Image));

        Assert.Equal(0, result.Code);
        Assert.Contains('\n', result.Out.TrimEnd());
        using var projected = JsonDocument.Parse(result.Out);
        Assert.Equal(AssetId, projected.RootElement.GetProperty("iconImageId").GetString());
        Assert.Equal(AssetId, projected.RootElement.GetProperty("assetId").GetString());
        Assert.Equal("https://cdn.example/badge.png",
            projected.RootElement.GetProperty("cdnUrl").GetString());
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task List_RejectsInvalidScidBeforeNetwork(string scid)
    {
        var handler = new ImageHandler(HttpStatusCode.OK, "[]");
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "list", "--scid", scid,
                "--seller-id", "12345"], handler);

        Assert.Equal(1, result.Code);
        Assert.Contains("--scid", result.Error, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task List_RejectsInvalidSellerBeforeNetwork()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, "[]");
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "list", "--scid", Scid,
                "--seller-id", "0"], handler);

        Assert.Equal(1, result.Code);
        Assert.Contains("--seller-id", result.Error, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("""{"assetId":"wrong-shape"}""", "must contain the images array")]
    [InlineData("""{"images":{}}""", "must contain the images array")]
    [InlineData("[]", "must contain the images array")]
    [InlineData("""{"images":[],"continuationToken":7}""", "continuationToken that was not a string")]
    [InlineData("""{"images":[{"cdnUrl":"https://cdn.example/badge.png"}]}""", "must contain an assetId")]
    [InlineData("""{"images":[{"assetId":"11111111-2222-3333-4444-555555555555"}]}""", "must contain a scid")]
    [InlineData("""{"images":[null]}""", "must contain an assetId")]
    [InlineData("<html>gateway failed</html>", "not valid JSON")]
    public async Task List_RejectsMalformedResponseWithoutSuccessOutput(
        string response,
        string error)
    {
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "list", "--scid", Scid,
                "--seller-id", "12345"],
            new ImageHandler(HttpStatusCode.OK, response));

        Assert.Equal(1, result.Code);
        Assert.Empty(result.Out);
        Assert.Contains(error, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("table")]
    public async Task List_GatewayFailureWritesOnlyToStderr(string format)
    {
        var handler = new ImageHandler(HttpStatusCode.BadGateway, """{"title":"Unavailable"}""");
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "list", "--scid", Scid,
                "--seller-id", "12345", "--output", format], handler);

        Assert.Equal(1, result.Code);
        Assert.Empty(result.Out);
        Assert.Contains("Unavailable", result.Error, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task List_TableRejectsMalformedImagesWithoutSuccessOutput()
    {
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "list", "--scid", Scid,
                "--seller-id", "12345", "--output", "table"],
            new ImageHandler(HttpStatusCode.OK, """{"images":[{"cdnUrl":"https://cdn.example/badge.png"}]}"""));

        Assert.Equal(1, result.Code);
        Assert.Empty(result.Out);
        Assert.Contains("must contain an assetId", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_TableHelpShowsReadOnlySafety()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, "[]");
        var help = await InvokeAsync(
            ["xbl", "achievement-image", "list", "--help"], handler);

        Assert.Equal(0, help.Code);
        Assert.Contains("xbox xbl achievement-image list", help.Out, StringComparison.Ordinal);
        Assert.Contains("Read-only", help.Out, StringComparison.Ordinal);
        Assert.Contains("table", help.Out, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Get_RejectsInvalidScidBeforeNetwork(string scid)
    {
        var handler = new ImageHandler(HttpStatusCode.OK, Image);
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "get", "--scid", scid,
                "--asset-id", AssetId, "--seller-id", "12345"], handler);

        Assert.Equal(1, result.Code);
        Assert.Contains("--scid", result.Error, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Get_RejectsInvalidAssetIdBeforeNetwork(string assetId)
    {
        var handler = new ImageHandler(HttpStatusCode.OK, Image);
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "get", "--scid", Scid,
                "--asset-id", assetId, "--seller-id", "12345"], handler);

        Assert.Equal(1, result.Code);
        Assert.Contains("DESCRIPTION", result.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("\"assetId\"", result.Out, StringComparison.Ordinal);
        Assert.Contains("--asset-id", result.Error, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Upload_RejectsNonPngBeforeNetwork()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(path, "not an image");
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image);
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("PNG image", result.Error, StringComparison.Ordinal);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("<html>bad gateway</html>", "not valid JSON")]
    [InlineData("[]", "must contain an assetId")]
    [InlineData("""{"assetId":null}""", "not valid JSON")]
    public async Task Get_RejectsMalformedSuccessWithoutSuccessOutput(
        string response,
        string error)
    {
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "get", "--scid", Scid,
                "--asset-id", AssetId, "--seller-id", "12345"],
            new ImageHandler(HttpStatusCode.OK, response));
        Assert.Equal(1, result.Code);
        Assert.Empty(result.Out);
        Assert.Contains(error, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("table")]
    public async Task Upload_ServiceErrorIsNotRetriedAndProducesNoSuccessOutput(string format)
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image)
            {
                RegisterStatus = HttpStatusCode.BadGateway,
                RegisterResponse = """{"title":"Unavailable"}""",
            };
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path, "--output", format], handler);
            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("Unavailable", result.Error, StringComparison.Ordinal);
            Assert.Contains("Upload outcome unknown. The service did not confirm image registration.",
                result.Error, StringComparison.Ordinal);
            Assert.Contains("Inspect registered image metadata and CDN image contents by running:",
                result.Error, StringComparison.Ordinal);
            Assert.Contains(
                $"{Environment.NewLine}{Environment.NewLine}xbox xbl achievement-image list --scid {Scid} --seller-id 12345{Environment.NewLine}{Environment.NewLine}",
                result.Error, StringComparison.Ordinal);
            Assert.EndsWith(
                $"Use the existing asset ID if you find your image, or retry the upload.{Environment.NewLine}" +
                $"Each upload creates a separate image asset.{Environment.NewLine}",
                result.Error, StringComparison.Ordinal);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_BadRequestKeepsOriginalErrorAndLabelsRequirements()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.BadRequest,
                """{"title":"Invalid seller","detail":"Seller is not authorized"}""");
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("Seller is not authorized", result.Error, StringComparison.Ordinal);
            Assert.Contains("Image requirements:", result.Error, StringComparison.Ordinal);
            Assert.Contains("1920x1080", result.Error, StringComparison.Ordinal);
            Assert.Contains("20,971,520", result.Error, StringComparison.Ordinal);
            Assert.False(HasRecoveryInstructions(result.Error));
            Assert.Single(handler.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    [InlineData("table")]
    public async Task Upload_BadRequestReportsSyntheticFieldErrorsWithoutGenericRequirements(
        string format)
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            // Synthetic ASP.NET validation body; the live HTTP 400 body was not captured.
            var handler = new ImageHandler(HttpStatusCode.BadRequest,
                """{"title":"One or more validation errors occurred.","errors":{"File":["PNG data was rejected."],"Scid":["The value is invalid."]},"traceId":"private"}""");
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path, "--output", format], handler);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            using var error = JsonDocument.Parse(result.Error);
            Assert.Equal(400, error.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("One or more validation errors occurred.",
                error.RootElement.GetProperty("title").GetString());
            Assert.Contains("HTTP 400", error.RootElement.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
            Assert.Equal("PNG data was rejected.",
                error.RootElement.GetProperty("errors").GetProperty("File")[0].GetString());
            Assert.Equal("The value is invalid.",
                error.RootElement.GetProperty("errors").GetProperty("Scid")[0].GetString());
            Assert.DoesNotContain("Image requirements:", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("private", result.Error, StringComparison.Ordinal);
            Assert.False(HasRecoveryInstructions(result.Error));
            Assert.Single(handler.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("json-compact", false)]
    [InlineData("table", false)]
    [InlineData("json", true)]
    [InlineData("json-compact", true)]
    [InlineData("table", true)]
    public async Task Upload_BadRequestReportsSyntheticRequestWideErrors(
        string format, bool withNamedField)
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            // Synthetic response; the live HTTP 400 body was not captured.
            string body = withNamedField
                ? """{"title":"One or more validation errors occurred.","errors":{"":["The request body could not be read."],"File":["Upload a PNG."]},"traceId":"private"}"""
                : """{"title":"One or more validation errors occurred.","errors":{"":["The request body could not be read."]},"traceId":"private"}""";
            var handler = new ImageHandler(HttpStatusCode.BadRequest, body);
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path, "--output", format], handler);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            using var error = JsonDocument.Parse(result.Error);
            Assert.Equal(400, error.RootElement.GetProperty("status").GetInt32());
            JsonElement errors = error.RootElement.GetProperty("errors");
            Assert.Equal("The request body could not be read.", errors.GetProperty("")[0].GetString());
            Assert.Equal(withNamedField, errors.TryGetProperty("File", out JsonElement named));
            if (withNamedField)
            {
                Assert.Equal("Upload a PNG.", named[0].GetString());
            }

            Assert.DoesNotContain("Image requirements:", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("private", result.Error, StringComparison.Ordinal);
            Assert.False(HasRecoveryInstructions(result.Error));
            Assert.Single(handler.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("""{"title":"Invalid image","detail":"Unable to process image.","errors":{"File":["Image could not be decoded."]}}""", true)]
    [InlineData("""{"title":"Invalid image","errors":{"File":[]}}""", false)]
    [InlineData("""{"title":"Invalid image","errors":{"File":["potentially private"]},"stackTrace":"private trace"}""", false)]
    public async Task Upload_BadRequestKeepsSafeDetailAndFiltersMalformedOrDiagnosticErrors(
        string body, bool hasFieldErrors)
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path],
                new ImageHandler(HttpStatusCode.BadRequest, body));

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            using var error = JsonDocument.Parse(result.Error);
            Assert.Equal(400, error.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(hasFieldErrors, error.RootElement.TryGetProperty("errors", out _));
            Assert.Equal(!hasFieldErrors,
                error.RootElement.GetProperty("detail").GetString()!
                    .Contains("Image requirements:", StringComparison.Ordinal));
            if (hasFieldErrors)
            {
                Assert.StartsWith("Unable to process image.",
                    error.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
            }
            Assert.DoesNotContain("private", result.Error, StringComparison.Ordinal);
            Assert.False(HasRecoveryInstructions(result.Error));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Get_BadRequestDoesNotSuggestUploadImageRequirements()
    {
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "get", "--scid", Scid,
                "--asset-id", AssetId, "--seller-id", "12345"],
            new ImageHandler(HttpStatusCode.BadRequest, """{"detail":"Invalid SCID"}"""));

        Assert.Equal(1, result.Code);
        Assert.Empty(result.Out);
        Assert.Contains("Invalid SCID", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Image requirements:", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_BadRequestIncludesSafeFieldErrorsFromSharedFormatter()
    {
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "get", "--scid", Scid,
                "--asset-id", AssetId, "--seller-id", "12345", "--output", "json-compact"],
            new ImageHandler(HttpStatusCode.BadRequest,
                """{"detail":"Invalid lookup.","errors":{"AssetId":["Image not found."]},"exception":"unrelated private extension"}"""));

        Assert.Equal(1, result.Code);
        Assert.Empty(result.Out);
        using var error = JsonDocument.Parse(result.Error);
        Assert.Equal("Invalid lookup.", error.RootElement.GetProperty("detail").GetString());
        Assert.Equal("Image not found.",
            error.RootElement.GetProperty("errors").GetProperty("AssetId")[0].GetString());
        Assert.DoesNotContain("unrelated private extension", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Image requirements:", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_InvalidSuccessSuggestsRecoveryWithQuotedConfigurationOverrides()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK,
                """{"scid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}""");
            const string scopes = "scope's $env:HOME `x";
            var result = await InvokeAsync(
                ["--scopes", scopes, "--client-id", "00000000-0000-0000-0000-000000000000",
                    "--profile", "test",
                    "xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("must contain an assetId", result.Error, StringComparison.Ordinal);
            Assert.Contains("Upload outcome unknown.", result.Error, StringComparison.Ordinal);
            string quotedScopes = OperatingSystem.IsWindows()
                ? "'scope''s $env:HOME `x'"
                : "'scope'\"'\"'s $env:HOME `x'";
            Assert.EndsWith(
                $"{Environment.NewLine}{Environment.NewLine}Upload outcome unknown. The service did not confirm image registration." +
                $"{Environment.NewLine}Inspect registered image metadata and CDN image contents by running:" +
                $"{Environment.NewLine}{Environment.NewLine}" +
                $"xbox --client-id 00000000-0000-0000-0000-000000000000 --scopes {quotedScopes} --profile test xbl achievement-image list --scid {Scid} --seller-id 12345" +
                $"{Environment.NewLine}{Environment.NewLine}" +
                $"Use the existing asset ID if you find your image, or retry the upload.{Environment.NewLine}" +
                $"Each upload creates a separate image asset.{Environment.NewLine}",
                result.Error, StringComparison.Ordinal);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_RecoveryKeepsExplicitDefaultProfileOverEnvironment()
    {
        string? original = Environment.GetEnvironmentVariable(ContextProfiles.EnvironmentVariable);
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            Environment.SetEnvironmentVariable(ContextProfiles.EnvironmentVariable, "prod");
            var result = await InvokeAsync(
                ["--profile", "default", "xbl", "achievement-image", "upload",
                    "--scid", Scid, "--seller-id", "12345", "--file", path],
                new ImageHandler(HttpStatusCode.OK, Image)
                {
                    RegisterStatus = HttpStatusCode.BadGateway,
                    RegisterResponse = """{"title":"Unavailable"}""",
                });

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("Unavailable", result.Error, StringComparison.Ordinal);
            Assert.Contains("Upload outcome unknown.", result.Error, StringComparison.Ordinal);
            Assert.Contains(
                $"{Environment.NewLine}{Environment.NewLine}xbox --profile default xbl achievement-image list --scid {Scid} --seller-id 12345{Environment.NewLine}{Environment.NewLine}",
                result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextProfiles.EnvironmentVariable, original);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_InvalidSuccessWithAssetIdSuggestsReadOnlyGet()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, $$"""{"assetId":"{{AssetId}}"}""");
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("must contain a scid", result.Error, StringComparison.Ordinal);
            Assert.Contains("Upload outcome unknown.", result.Error, StringComparison.Ordinal);
            Assert.Contains(
                $"{Environment.NewLine}{Environment.NewLine}xbox xbl achievement-image get --scid {Scid} --seller-id 12345 --asset-id {AssetId}{Environment.NewLine}{Environment.NewLine}",
                result.Error, StringComparison.Ordinal);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_ConnectionDropOrCancellationIsUncertain(bool cancelled)
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            string error = cancelled ? "Timed out" : "Connection dropped";
            var handler = new ImageHandler(HttpStatusCode.OK, Image)
            {
                RegisterFailure = cancelled
                    ? new TaskCanceledException(error)
                    : new HttpRequestException(error),
            };
            var dropped = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler);
            Assert.Equal(1, dropped.Code);
            Assert.Empty(dropped.Out);
            Assert.Contains(error, dropped.Error, StringComparison.Ordinal);
            Assert.Contains("Upload outcome unknown.", dropped.Error, StringComparison.Ordinal);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_AuthenticationFailureBeforeDispatchIsOrdinaryError()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image);
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path],
                handler, () => Task.FromResult<string?>(null));
            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("No cached authentication token", result.Error, StringComparison.Ordinal);
            Assert.False(HasRecoveryInstructions(result.Error));
            Assert.Empty(handler.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_RequestTimeoutStatusIsUncertain()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, Image)
            {
                RegisterStatus = HttpStatusCode.RequestTimeout,
                RegisterResponse = """{"detail":"Request timed out"}""",
            };
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("Request timed out", result.Error, StringComparison.Ordinal);
            Assert.Contains("Upload outcome unknown.", result.Error, StringComparison.Ordinal);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    public async Task Upload_RejectionWithUnreadableBodyKeepsKnownStatus(
        HttpStatusCode status, bool uncertain)
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(uncertain ? HttpStatusCode.OK : status, string.Empty)
            {
                RegisterStatus = uncertain ? status : null,
                ResponseContent = new UnreadableContent(new IOException("Disconnected while reading.")),
            };
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains($"HTTP {(int)status}", result.Error, StringComparison.Ordinal);
            Assert.Contains("The response body could not be read", result.Error, StringComparison.Ordinal);
            Assert.Equal(uncertain,
                HasRecoveryInstructions(result.Error));
            Assert.Equal(status == HttpStatusCode.BadRequest,
                result.Error.Contains("Image requirements:", StringComparison.Ordinal));
            Assert.Equal(uncertain ? 2 : 1,
                handler.Requests.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upload_CancelledRejectionBodyIsDefiniteButCancelledSuccessBodyIsUncertain()
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            foreach (HttpStatusCode status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.OK })
            {
                var handler = new ImageHandler(status, string.Empty)
                {
                    ResponseContent = new UnreadableContent(
                        new OperationCanceledException("Body read cancelled.")),
                };
                var result = await InvokeAsync(
                    ["xbl", "achievement-image", "upload", "--scid", Scid,
                        "--seller-id", "12345", "--file", path], handler);
                Assert.Equal(1, result.Code);
                Assert.Empty(result.Out);
                Assert.Equal(status == HttpStatusCode.OK,
                    HasRecoveryInstructions(result.Error));
                Assert.Equal(status == HttpStatusCode.OK ? 2 : 1, handler.Requests.Count);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task Upload_OversizedBodyDistinguishesRejectionFromUnverifiableSuccess(
        HttpStatusCode status, bool uncertain)
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(status, string.Empty)
            {
                ResponseContent = new ByteArrayContent(
                    new byte[XboxPartnerClient.DefaultMaxResponseBytes + 1]),
            };
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "upload", "--scid", Scid,
                    "--seller-id", "12345", "--file", path], handler);
            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("exceeded the configured maximum", result.Error, StringComparison.Ordinal);
            Assert.Equal(uncertain,
                HasRecoveryInstructions(result.Error));
            if (!uncertain)
            {
                Assert.Contains("HTTP 400", result.Error, StringComparison.Ordinal);
            }
            Assert.Equal(uncertain ? 2 : 1, handler.Requests.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("get")]
    [InlineData("list")]
    public async Task ImageCommands_VerboseDiagnosticsStayOnStderr(string action)
    {
        string path = Path.Combine(Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, Png);
        try
        {
            var handler = new ImageHandler(HttpStatusCode.OK, action == "list" ? ImageList : Image);
            var args = new List<string>
            {
                "--verbose", "xbl", "achievement-image", action,
                "--scid", Scid, "--seller-id", "12345", "--output", "json-compact",
            };
            if (action == "upload")
            {
                args.AddRange(["--file", path]);
            }
            else if (action == "get")
            {
                args.AddRange(["--asset-id", AssetId]);
            }

            var result = await InvokeAsync([.. args], handler);
            Assert.Equal(0, result.Code);
            Assert.Equal(
                (action == "list" ? "[" + ImageWithIconImageId + "]" : ImageWithIconImageId) +
                Environment.NewLine, result.Out);
            Assert.Contains(Scid, result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("caller-token", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("opaque-upload-secret", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("sig=secret", result.Error, StringComparison.Ordinal);
            Assert.Equal(action == "upload" ? 2 : 1, handler.Requests.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Get_TableRendersOneImageAndHelpRemainsAvailable()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, Image);
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "get", "--scid", Scid,
                "--asset-id", AssetId, "--seller-id", "12345", "--output", "table"],
            handler);
        var help = await InvokeAsync(
            ["xbl", "achievement-image", "get", "--output", "table", "--help"],
            handler);

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        AssertImageTable(result.Out, AssetId);
        Assert.Equal(0, help.Code);
        Assert.Contains("achievement-image get", help.Out, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.Equal("/xboxpartner/v1/xboxlive/achievement-image", handler.Requests[0].Path);
    }

    [Fact]
    public async Task Upload_HelpUsesCanonicalPathAndExplainsRecovery()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, Image);
        var help = await InvokeAsync(
            ["xbl", "achievement-image", "upload", "--help"], handler);
        var pathHelp = await InvokeAsync(
            ["help", "xbl", "achievement-image", "upload"], handler);

        Assert.Equal(0, help.Code);
        Assert.Equal(help.Out, pathHelp.Out);
        var text = string.Join(' ', help.Out.Split(
            [' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("xbox xbl achievement-image upload", text, StringComparison.Ordinal);
        Assert.Contains("restarts upload", text, StringComparison.Ordinal);
        Assert.Contains("never retries", text, StringComparison.Ordinal);
        Assert.Contains("registration after a timeout or temporary failure.", text,
            StringComparison.Ordinal);
        Assert.Contains("If the outcome is unknown,", text, StringComparison.Ordinal);
        Assert.Contains("inspect registered metadata and CDN images by running the printed read-only",
            text, StringComparison.Ordinal);
        Assert.Contains("1920x1080", help.Out, StringComparison.Ordinal);
        Assert.Contains("20 MiB", help.Out, StringComparison.Ordinal);
        Assert.Contains(ProjectLinks.Documentation, help.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("/xboxpartner/v1/", help.Out, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("list")]
    public async Task ReadHelpLinksImageReferenceAndExplainsScid(string action)
    {
        var handler = new ImageHandler(HttpStatusCode.OK, Image);
        var help = await InvokeAsync(
            ["xbl", "achievement-image", action, "--help"], handler);

        Assert.Equal(0, help.Code);
        Assert.Contains("title", help.Out, StringComparison.Ordinal);
        Assert.Contains("--scid", help.Out, StringComparison.Ordinal);
        Assert.Contains(ProjectLinks.Documentation, help.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("/xboxpartner/v1/", help.Out, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void ImageCommandsDeclareRecordAndCollectionTableViews()
    {
        var commands = Program.BuildRootCommand().Subcommands
            .Single(command => command.Name == "xbl").Subcommands
            .Single(command => command.Name == "achievement-image").Subcommands;

        foreach (var command in commands.Where(command => command.Name != "download"))
        {
            var view = Assert.IsType<TableView>(CommandMetadataRegistry.GetMetadata(command).Table);
            Assert.Equal(command.Name == "list" ? TableShape.Collection : TableShape.Record,
                view.Shape);
            Assert.Equal(["assetId", "cdnUrl", "widthInPixels", "heightInPixels", "imageType", "isPublic"],
                view.Columns.Select(column => column.Property));
            Assert.NotNull(view.Columns[1].Transform);
        }
        var download = Assert.Single(commands, command => command.Name == "download");
        var downloadView = Assert.IsType<TableView>(CommandMetadataRegistry.GetMetadata(download).Table);
        Assert.Equal(["assetId", "destination"], downloadView.Columns.Select(column => column.Property));
    }

    [Fact]
    public void Download_RequiresOneSelectorAndDestination()
    {
        foreach (string[] tail in new[]
        {
            Array.Empty<string>(),
            new[] { "--all", "--asset-id", AssetId, "--dest", "images" },
            new[] { "--asset-id", AssetId },
            new[] { "--asset-id", "bad", "--dest", "images" },
            new[] { "--asset-id", Guid.Empty.ToString(), "--dest", "images" },
        })
        {
            var parsed = Program.BuildRootCommand().Parse(
                ["xbl", "achievement-image", "download", "--scid", Scid, .. tail],
                Program.NoResponseFileConfig);
            Assert.NotEmpty(parsed.Errors);
        }

        var valid = Program.BuildRootCommand().Parse(
            ["xbl", "achievement-image", "download", "--scid", Scid,
                "--all", "--dest", "images"], Program.NoResponseFileConfig);
        Assert.Empty(valid.Errors);
    }

    [Fact]
    public async Task Download_InvokedGetWritesFullImageAndThumbnailAndReturnsManifest()
    {
        string destination = CreateDownloadDestination();
        const string fullUrl = "https://cdn.example/TestAchievement001%2EPNG?sig=secret";
        const string thumbnailUrl = "https://cdn.example/thumbnail.jpg?sig=secret";
        var gateway = new ImageHandler(HttpStatusCode.OK, ImageWithUrls(fullUrl, thumbnailUrl));
        var download = ForImages(new Dictionary<string, byte[]>
        {
            [new Uri(fullUrl).AbsoluteUri] = Png,
            [new Uri(thumbnailUrl).AbsoluteUri] = CreateJpegHeader(320, 180),
        });
        try
        {
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "download", "--scid", Scid,
                    "--asset-id", AssetId, "--seller-id", "12345", "--dest", destination,
                    "--output", "json-compact"],
                gateway, downloadHandler: download);

            Assert.True(result.Code == 0, result.Error);
            Assert.Empty(result.Error);
            Assert.Single(gateway.Requests);
            Assert.Equal("/xboxpartner/v1/xboxlive/achievement-image", gateway.Requests[0].Path);
            Assert.Equal(new[] { new Uri(fullUrl).AbsoluteUri, new Uri(thumbnailUrl).AbsoluteUri },
                download.Requests.Select(request => request.Url.AbsoluteUri));
            Assert.All(download.Requests, request => Assert.Null(request.Authorization));
            string folder = Path.Combine(destination, AssetId);
            Assert.Equal(Png, await File.ReadAllBytesAsync(Path.Combine(folder, "TestAchievement001.PNG")));
            Assert.Equal(CreateJpegHeader(320, 180),
                await File.ReadAllBytesAsync(Path.Combine(folder, "thumbnail.jpg")));
            using var manifest = JsonDocument.Parse(result.Out);
            Assert.Equal(AssetId, manifest.RootElement.GetProperty("assetId").GetString());
            Assert.Equal(folder, manifest.RootElement.GetProperty("destination").GetString());
            Assert.Equal(Png.Length, manifest.RootElement.GetProperty("imageBytes").GetInt64());
            Assert.Equal(CreateJpegHeader(320, 180).Length,
                manifest.RootElement.GetProperty("thumbnailBytes").GetInt64());
            Assert.Equal(Path.Combine(folder, "TestAchievement001.PNG"),
                manifest.RootElement.GetProperty("imagePath").GetString());
        }
        finally
        {
            DeleteDownloadDestination(destination);
        }
    }

    [Fact]
    public async Task Download_AllUsesTypedListAndRendersManifestTable()
    {
        string destination = CreateDownloadDestination();
        const string otherId = "66666666-7777-8888-9999-000000000000";
        var images = new Dictionary<string, byte[]>
        {
            ["https://cdn.example/one/full.png"] = Png,
            ["https://cdn.example/one/thumb.png"] = CreatePngHeader(320, 180),
            ["https://cdn.example/two/full.png"] = Png,
            ["https://cdn.example/two/thumb.png"] = CreatePngHeader(320, 180),
        };
        var gateway = new ImageHandler(HttpStatusCode.OK, ImagesPage(
            "[" + ImageWithUrls("https://cdn.example/one/full.png",
                "https://cdn.example/one/thumb.png") + "," +
            ImageWithUrls("https://cdn.example/two/full.png",
                "https://cdn.example/two/thumb.png", otherId) + "]"));
        var download = ForImages(images);
        try
        {
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "download", "--scid", Scid,
                    "--all", "--seller-id", "12345", "--dest", destination,
                    "--output", "table"],
                gateway, downloadHandler: download);

            Assert.Equal(0, result.Code);
            Assert.Empty(result.Error);
            Assert.Equal("/xboxpartner/v1/xboxlive/achievement-images", Assert.Single(gateway.Requests).Path);
            Assert.Equal(4, download.Requests.Count);
            Assert.Contains("ASSET ID", result.Out, StringComparison.Ordinal);
            Assert.Contains("DESTINATION", result.Out, StringComparison.Ordinal);
            Assert.Contains(AssetId, result.Out, StringComparison.Ordinal);
            Assert.Contains(otherId, result.Out, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(destination, AssetId, "full.png")));
            Assert.True(File.Exists(Path.Combine(destination, otherId, "thumb.png")));
        }
        finally
        {
            DeleteDownloadDestination(destination);
        }
    }

    [Fact]
    public async Task Download_AllJsonReturnsArrayOfCompletedImagePairs()
    {
        string destination = CreateDownloadDestination();
        const string fullUrl = "https://cdn.example/full.png";
        const string thumbnailUrl = "https://cdn.example/thumb.png";
        var download = ForImages(new Dictionary<string, byte[]>
        {
            [fullUrl] = Png,
            [thumbnailUrl] = CreatePngHeader(320, 180),
        });
        try
        {
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "download", "--scid", Scid,
                    "--all", "--seller-id", "12345", "--dest", destination,
                    "--output", "json-compact"],
                new ImageHandler(HttpStatusCode.OK,
                    ImagesPage("[" + ImageWithUrls(fullUrl, thumbnailUrl) + "]")),
                downloadHandler: download);

            Assert.Equal(0, result.Code);
            using var manifest = JsonDocument.Parse(result.Out);
            var item = Assert.Single(manifest.RootElement.EnumerateArray());
            Assert.Equal(AssetId, item.GetProperty("assetId").GetString());
            Assert.EndsWith("full.png", item.GetProperty("imagePath").GetString(),
                StringComparison.Ordinal);
            Assert.EndsWith("thumb.png", item.GetProperty("thumbnailPath").GetString(),
                StringComparison.Ordinal);
            Assert.False(item.TryGetProperty("iconImageId", out _));
        }
        finally
        {
            DeleteDownloadDestination(destination);
        }
    }

    [Theory]
    [InlineData("json-compact", "[]")]
    [InlineData("table", "No results.")]
    public async Task Download_EmptyListProducesRequestedOutput(
        string format, string expected)
    {
        string destination = CreateDownloadDestination();
        try
        {
            var download = ForImages(new Dictionary<string, byte[]>());
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "download", "--scid", Scid,
                    "--all", "--seller-id", "12345", "--dest", destination,
                    "--output", format],
                new ImageHandler(HttpStatusCode.OK, ImagesPage("[]")), downloadHandler: download);
            Assert.Equal(0, result.Code);
            Assert.Equal(expected + Environment.NewLine, result.Out);
            Assert.Empty(download.Requests);
        }
        finally
        {
            DeleteDownloadDestination(destination);
        }
    }

    [Theory]
    [InlineData("http://cdn.example/image.png", "https://cdn.example/thumb.png")]
    [InlineData("https://cdn.example/full.png", "https://cdn.example/full.png")]
    [InlineData("https://cdn.example/%2Funsafe.png", "https://cdn.example/thumb.png")]
    [InlineData("https://cdn.example/CON.png", "https://cdn.example/thumb.png")]
    public async Task Download_RejectsUnsafeCdnMetadataBeforeWriting(
        string imageUrl, string thumbnailUrl)
    {
        string destination = CreateDownloadDestination();
        var gateway = new ImageHandler(HttpStatusCode.OK,
            ImageWithUrls(imageUrl, thumbnailUrl));
        var download = ForImages(new Dictionary<string, byte[]>());
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "download", "--scid", Scid,
                "--asset-id", AssetId, "--seller-id", "12345", "--dest", destination],
            gateway, downloadHandler: download);

        Assert.Equal(1, result.Code);
        Assert.Empty(result.Out);
        Assert.StartsWith("Request failed:", result.Error, StringComparison.Ordinal);
        Assert.Empty(download.Requests);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("66666666-7777-8888-9999-000000000000", Scid)]
    [InlineData(AssetId, "bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee")]
    public async Task Download_RejectsMismatchedAssetOrScidBeforeCdn(
        string returnedAssetId, string returnedScid)
    {
        string destination = CreateDownloadDestination();
        var download = ForImages(new Dictionary<string, byte[]>());
        var result = await InvokeAsync(
            ["xbl", "achievement-image", "download", "--scid", Scid,
                "--asset-id", AssetId, "--seller-id", "12345", "--dest", destination],
            new ImageHandler(HttpStatusCode.OK,
                ImageWithUrls("https://cdn.example/full.png",
                    "https://cdn.example/thumb.png", returnedAssetId, returnedScid)),
            downloadHandler: download);

        Assert.Equal(1, result.Code);
        Assert.Empty(result.Out);
        Assert.Empty(download.Requests);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Download_FailedThumbnailPreservesExistingImagePair()
    {
        string destination = CreateDownloadDestination();
        string folder = Path.Combine(destination, AssetId);
        Directory.CreateDirectory(folder);
        string image = Path.Combine(folder, "full.png");
        string thumbnail = Path.Combine(folder, "thumb.png");
        await File.WriteAllTextAsync(image, "previous image");
        await File.WriteAllTextAsync(thumbnail, "previous thumbnail");
        var handler = ForImages(new Dictionary<string, byte[]>
        {
            ["https://cdn.example/full.png"] = Png,
        });
        try
        {
            var result = await InvokeAsync(
                ["xbl", "achievement-image", "download", "--scid", Scid,
                    "--asset-id", AssetId, "--seller-id", "12345", "--dest", destination],
                new ImageHandler(HttpStatusCode.OK,
                    ImageWithUrls("https://cdn.example/full.png", "https://cdn.example/thumb.png")),
                downloadHandler: handler);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("thumbnail download", result.Error, StringComparison.Ordinal);
            Assert.Equal("previous image", await File.ReadAllTextAsync(image));
            Assert.Equal("previous thumbnail", await File.ReadAllTextAsync(thumbnail));
            Assert.Empty(Directory.EnumerateDirectories(destination, ".xboxdevcli-*"));
        }
        finally
        {
            DeleteDownloadDestination(destination);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Download_BoundsDeclaredAndStreamingContent(bool declaredLength)
    {
        string destination = CreateDownloadDestination();
        var handler = new DownloadHandler((_, _, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = declaredLength
                    ? new ByteArrayContent(Png)
                    : new UnmeasuredContent(Png),
            }));
        try
        {
            var result = await RunDownloadAsync(
                ImageWithUrls("https://cdn.example/full.png", "https://cdn.example/thumb.png"),
                handler, destination, AchievementImageDownloadPolicy.Default with { MaxBytes = 32 });

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("exceeded 32 bytes", result.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(destination, AssetId, "full.png")));
        }
        finally
        {
            DeleteDownloadDestination(destination);
        }
    }

    [Fact]
    public async Task Download_AcceptsTrustedRedirectWithoutGatewayCredentials()
    {
        string destination = CreateDownloadDestination();
        const string fullUrl = "https://cdn.example/full.png";
        const string thumbnailUrl = "https://cdn.example/thumb.png";
        var handler = new DownloadHandler((request, _, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == fullUrl
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Png),
                    RequestMessage = new HttpRequestMessage(
                        HttpMethod.Get, "https://cdn.example/redirected.png"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(
                        request.RequestUri.AbsoluteUri == thumbnailUrl
                            ? CreatePngHeader(320, 180)
                            : Png),
                }));
        try
        {
            var result = await RunDownloadAsync(
                ImageWithUrls(fullUrl, thumbnailUrl), handler, destination);

            Assert.Equal(0, result.Code);
            Assert.Equal(2, handler.Requests.Count);
            Assert.All(handler.Requests, request => Assert.Null(request.Authorization));
            Assert.True(File.Exists(Path.Combine(destination, AssetId, "full.png")));
        }
        finally
        {
            DeleteDownloadDestination(destination);
        }
    }

    [Fact]
    public async Task Download_RejectsRedirectToUntrustedHttpHost()
    {
        string destination = CreateDownloadDestination();
        var handler = new DownloadHandler((_, _, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Png),
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get, "http://untrusted.example/redirected.png"),
            }));
        try
        {
            var result = await RunDownloadAsync(
                ImageWithUrls("https://cdn.example/full.png",
                    "https://cdn.example/thumb.png"), handler, destination);

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("must be absolute HTTPS", result.Error, StringComparison.Ordinal);
            Assert.Single(handler.Requests);
        }
        finally
        {
            DeleteDownloadDestination(destination);
        }
    }

    [Fact]
    public async Task Download_ReportsPerFileTimeout()
    {
        string destination = CreateDownloadDestination();
        var handler = new DownloadHandler(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        try
        {
            var result = await RunDownloadAsync(
                ImageWithUrls("https://cdn.example/full.png",
                    "https://cdn.example/thumb.png"), handler, destination,
                AchievementImageDownloadPolicy.Default with { Timeout = TimeSpan.FromMilliseconds(20) });

            Assert.Equal(1, result.Code);
            Assert.Empty(result.Out);
            Assert.Contains("timed out", result.Error, StringComparison.Ordinal);
            Assert.Single(handler.Requests);
        }
        finally
        {
            DeleteDownloadDestination(destination);
        }
    }

    [Fact]
    public async Task Download_HelpShowsReadOnlyAndNoShortAliases()
    {
        var handler = new ImageHandler(HttpStatusCode.OK, Image);
        var help = await InvokeAsync(
            ["xbl", "achievement-image", "download", "--help"], handler);
        var pathHelp = await InvokeAsync(
            ["help", "xbl", "achievement-image", "download"], handler);

        Assert.Equal(0, help.Code);
        Assert.Equal(help.Out, pathHelp.Out);
        Assert.Contains("Read-only. Does not modify remote state.", help.Out, StringComparison.Ordinal);
        Assert.Contains("--asset-id", help.Out, StringComparison.Ordinal);
        Assert.Contains("--all", help.Out, StringComparison.Ordinal);
        Assert.Contains("--dest", help.Out, StringComparison.Ordinal);
        Assert.Contains(ProjectLinks.Documentation, help.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("-a, --asset-id", help.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("-s, --scid", help.Out, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    private static void AssertImageTable(string output, params string[] assetIds)
    {
        var lines = output.TrimEnd().Split(Environment.NewLine);
        Assert.Equal(assetIds.Length + 2, lines.Length);
        Assert.Contains("ASSET ID", lines[0], StringComparison.Ordinal);
        Assert.Contains("CDN FILENAME", lines[0], StringComparison.Ordinal);
        Assert.Contains("WIDTH", lines[0], StringComparison.Ordinal);
        Assert.Contains("HEIGHT", lines[0], StringComparison.Ordinal);
        Assert.Contains("IMAGE TYPE", lines[0], StringComparison.Ordinal);
        Assert.Contains("PUBLIC", lines[0], StringComparison.Ordinal);
        Assert.All(lines[1], character => Assert.True(character is '-' or ' '));
        for (int index = 0; index < assetIds.Length; index++)
        {
            Assert.Contains(assetIds[index], lines[index + 2], StringComparison.Ordinal);
            Assert.Contains("badge.png", lines[index + 2], StringComparison.Ordinal);
            Assert.Contains("128", lines[index + 2], StringComparison.Ordinal);
            Assert.Contains("96", lines[index + 2], StringComparison.Ordinal);
            Assert.Contains("png", lines[index + 2], StringComparison.Ordinal);
            Assert.Contains("true", lines[index + 2], StringComparison.Ordinal);
        }
        Assert.DoesNotContain("cdnUrl", output, StringComparison.Ordinal);
    }

    private static bool HasRecoveryInstructions(string error) =>
        error.Contains("Upload outcome unknown.", StringComparison.Ordinal);

    private static byte[] CreatePngHeader(int width, int height)
    {
        byte[] header = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(header.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20, 4), height);
        header[24] = 8;
        header[25] = 6;
        return header;
    }

    private static byte[] CreateJpegHeader(int width, int height)
    {
        byte[] header =
        [
            255, 216, 255, 192, 0, 17, 8, 0, 0, 0, 0, 3,
            1, 17, 0, 2, 17, 1, 3, 17, 1, 255, 217,
        ];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(7, 2), checked((ushort)height));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(9, 2), checked((ushort)width));
        return header;
    }

    private static XboxPartnerClient CreateClient(HttpMessageHandler handler) =>
        new(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["XboxPartner:BaseUrl"] = "http://localhost:8080",
            }).Build(),
            new HttpClient(handler),
            () => Task.FromResult<string?>("caller-token"));

    private static HttpClient CreateBlobClient(HttpMessageHandler? handler = null) =>
        new(handler ?? new BlobHandler(), disposeHandler: handler is null);

    private static async Task<(int Code, string Out, string Error)> InvokeAsync(
        string[] args,
        ImageHandler handler,
        Func<Task<string?>>? tokenFactory = null,
        HttpMessageHandler? downloadHandler = null,
        HttpMessageHandler? uploadHandler = null)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Program.XboxPartnerClientFactory = configuration =>
            new XboxPartnerClient(
                configuration, new HttpClient(handler),
                tokenFactory ?? (() => Task.FromResult<string?>("caller-token")));
        Program.AchievementImageDownloadClientFactory = downloadHandler is null
            ? null
            : () => new HttpClient(downloadHandler, disposeHandler: false);
        Program.AchievementImageUploadClientFactory = () =>
            CreateBlobClient(uploadHandler);
        try
        {
            int code = await Program.Main(args);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            Program.AchievementImageDownloadClientFactory = null;
            Program.AchievementImageUploadClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static string ImagesPage(string images) => "{\"images\":" + images + "}";

    private static string ImageWithUrls(
        string imageUrl, string thumbnailUrl, string assetId = AssetId, string scid = Scid) =>
        JsonSerializer.Serialize(new
        {
            assetId,
            scid,
            cdnUrl = imageUrl,
            thumbnailCdnUrl = thumbnailUrl,
            widthInPixels = 1920,
            heightInPixels = 1080,
            imageType = "png",
            isPublic = true,
        });

    private static string CreateDownloadDestination() =>
        Path.Combine(Path.GetTempPath(), $"XboxDevCLI-download-{Guid.NewGuid():N}");

    private static async Task<(int Code, string Out, string Error)> RunDownloadAsync(
        string response,
        HttpMessageHandler downloadHandler,
        string destination,
        AchievementImageDownloadPolicy? policy = null)
    {
        using var gatewayClient = CreateClient(new ImageHandler(HttpStatusCode.OK, response));
        using var downloadClient = new HttpClient(downloadHandler, disposeHandler: false);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int code = await DownloadAchievementImagesCommandFactory.RunAsync(
            gatewayClient, downloadClient, 12345, Guid.Parse(Scid), Guid.Parse(AssetId),
            all: false, destination, OutputOptions.JsonCompact, stdout, stderr,
            CancellationToken.None, policy);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static void DeleteDownloadDestination(string destination)
    {
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    private sealed class DownloadHandler(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<(Uri Url, AuthenticationHeaderValue? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization));
            return respond(request, Requests.Count, cancellationToken);
        }

    }

    private sealed class BlobHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, byte[] Bytes, string? BlobType,
            AuthenticationHeaderValue? Auth)> Requests { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.Created;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!,
                await request.Content!.ReadAsByteArrayAsync(cancellationToken),
                request.Headers.TryGetValues("x-ms-blob-type", out var values)
                    ? values.Single() : null,
                request.Headers.Authorization));
            return new HttpResponseMessage(Status);
        }
    }

    private static DownloadHandler ForImages(IReadOnlyDictionary<string, byte[]> images) =>
        new((request, _, _) => Task.FromResult(
            images.TryGetValue(request.RequestUri!.AbsoluteUri, out byte[]? bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound)));

    private sealed class UnmeasuredContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(bytes));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ImageHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public Exception? Failure { get; init; }
        public HttpContent? ResponseContent { get; init; }
        public HttpStatusCode? InitializeStatus { get; init; }
        public HttpStatusCode? RegisterStatus { get; init; }
        public string? InitializeResponse { get; init; }
        public string? RegisterResponse { get; init; }
        public Exception? RegisterFailure { get; init; }
        public Queue<string>? InitializeResponses { get; init; }
        public Queue<HttpStatusCode>? RegisterStatuses { get; init; }
        public Queue<string>? RegisterResponses { get; init; }
        public Queue<string>? ListResponses { get; init; }
        public List<(HttpMethod Method, string Path, string? Body, string? MediaType,
            AuthenticationHeaderValue? Auth)> Requests { get; } = [];
        public List<string?> CorrelationIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((
                request.Method, request.RequestUri!.AbsolutePath, body,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.Authorization));
            CorrelationIds.Add(request.Headers.TryGetValues("x-ms-correlation-id", out var ids)
                ? ids.Single() : null);
            if (ListResponses?.Count > 0 && request.RequestUri.AbsolutePath.EndsWith(
                "/xboxlive/achievement-images", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ListResponses.Dequeue()),
                };
            }
            bool initialize = request.RequestUri.AbsolutePath.EndsWith(
                "/upload/initialize", StringComparison.Ordinal);
            bool register = request.RequestUri.AbsolutePath.EndsWith(
                "/upload/register", StringComparison.Ordinal);
            if (RegisterFailure is not null && register)
            {
                throw RegisterFailure;
            }
            if (Failure is not null && (!initialize || status != HttpStatusCode.OK))
            {
                throw Failure;
            }
            HttpStatusCode effectiveStatus = initialize
                ? InitializeStatus ?? (status == HttpStatusCode.OK ? HttpStatusCode.OK : status)
                : register && RegisterStatuses?.Count > 0 ? RegisterStatuses.Dequeue()
                : register ? RegisterStatus ?? status : status;
            string? initializeResponse = initialize && InitializeResponses?.Count > 0
                ? InitializeResponses.Dequeue()
                : InitializeResponse;
            string? registerResponse = register && RegisterResponses?.Count > 0
                ? RegisterResponses.Dequeue()
                : RegisterResponse;
            return new HttpResponseMessage(effectiveStatus)
            {
                Content = initialize && status == HttpStatusCode.OK
                    ? new StringContent(initializeResponse ?? InitializeImage)
                    : ResponseContent ?? new StringContent(initialize
                    ? initializeResponse ?? (status == HttpStatusCode.OK ? InitializeImage : response)
                    : register && effectiveStatus != HttpStatusCode.OK
                        ? registerResponse ?? response : response),
            };
        }
    }

    private sealed class UnreadableContent(Exception exception) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context) =>
            Task.FromException(exception);

        protected override Task<Stream> CreateContentReadStreamAsync(
            CancellationToken cancellationToken) =>
            Task.FromException<Stream>(exception);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
