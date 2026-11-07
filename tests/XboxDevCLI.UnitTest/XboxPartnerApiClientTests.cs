// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Output;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class XboxPartnerApiClientTests
{
    [Fact]
    public async Task GetXboxLiveConfigurationsAsync_SendsScidAndSeller()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK, """{"configurations":[]}""");
        using var client = CreateClient(handler);
        var scid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        string result = await client.GetXboxLiveConfigurationsAsync(
            12345,
            scid,
            CancellationToken.None);

        Assert.Equal("[]", result);
        Assert.Equal(
            "http://localhost:8080/xboxpartner/v1/xboxlive/configurations",
            handler.RequestUri?.AbsoluteUri);
        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(12345, body.RootElement.GetProperty("sellerId").GetInt64());
        Assert.Equal(scid, body.RootElement.GetProperty("scid").GetGuid());
        Assert.Equal(100, body.RootElement.GetProperty("pageSize").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("continuationToken", out _));
        Assert.False(body.RootElement.TryGetProperty("userId", out _));
        Assert.False(body.RootElement.TryGetProperty("userEmail", out _));
    }

    [Fact]
    public async Task GetXboxLiveConfigurationsAsync_FollowsEveryPageWithTheSameCorrelationId()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Page("""{"configurations":[{"sandbox":"XDKS.1"}],"continuationToken":"t1"}"""),
            Page("""{"configurations":[{"sandbox":"ABCDEF.1"},{"sandbox":"TESTSB.42"}]}"""));
        using var client = CreateClient(handler);
        var pages = new List<int>();

        string result = await client.GetXboxLiveConfigurationsAsync(
            null,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            CancellationToken.None,
            pages.Add);

        Assert.Equal("""[{"sandbox":"XDKS.1"},{"sandbox":"ABCDEF.1"},{"sandbox":"TESTSB.42"}]""", result);
        Assert.Equal([1, 2], pages);
        Assert.Equal(
            new[]
            {
                """{"sellerId":null,"scid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","pageSize":100}""",
                """{"sellerId":null,"scid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","continuationToken":"t1"}""",
            },
            handler.Requests.Select(request => request.Body));
        string correlationId = Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]);
        Assert.All(handler.Requests, request =>
            Assert.Equal(correlationId, Assert.Single(request.Headers["x-ms-correlation-id"])));
    }

    [Fact]
    public async Task GetXboxLiveConfigurationsAsync_RejectsARepeatedContinuationToken()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Page("""{"configurations":[],"continuationToken":"t1"}"""),
            Page("""{"configurations":[],"continuationToken":"t1"}"""));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<XboxPartnerResponseException>(
            () => client.GetXboxLiveConfigurationsAsync(
                12345,
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                CancellationToken.None));

        Assert.Equal(
            "The configuration list response repeated a continuation token.",
            exception.Message);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]),
            exception.CorrelationId);
    }

    [Theory]
    [InlineData("[]", "must contain the configurations array.")]
    [InlineData("""{"configurations":[],"continuationToken":5}""", "contained a continuationToken that was not a string.")]
    public async Task GetXboxLiveConfigurationsAsync_RejectsAMalformedPage(string body, string expected)
    {
        var handler = new XboxPartnerHttpMessageHandler(Page(body));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<XboxPartnerResponseException>(
            () => client.GetXboxLiveConfigurationsAsync(
                12345,
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                CancellationToken.None));

        Assert.Equal($"The configuration list response {expected}", exception.Message);
        Assert.Equal(body, exception.RawResponse);
        Assert.Equal(
            Assert.Single(Assert.Single(handler.Requests).Headers["x-ms-correlation-id"]),
            exception.CorrelationId);
    }

    [Fact]
    public async Task GetXboxLiveConfigurationAsync_SendsScidSandboxAndSeller()
    {
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """{"sandbox":"STAGE.0"}""");
        using var client = CreateClient(handler);
        var scid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        string result = await client.GetXboxLiveConfigurationAsync(
            12345,
            scid,
            "STAGE.0",
            CancellationToken.None);

        Assert.Contains("STAGE.0", result, StringComparison.Ordinal);
        Assert.Equal(
            "http://localhost:8080/xboxpartner/v1/xboxlive/configurations/get",
            handler.RequestUri?.AbsoluteUri);
        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(scid, body.RootElement.GetProperty("scid").GetGuid());
        Assert.Equal("STAGE.0", body.RootElement.GetProperty("sandbox").GetString());
    }

    [Fact]
    public async Task GetSchemaTypesAsync_SendsAuthenticatedRequest()
    {
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """["achievements","userstats"]""");
        using var client = CreateClient(handler);

        string result = await client.GetSchemaTypesAsync(CancellationToken.None);

        Assert.Contains("achievements", result, StringComparison.Ordinal);
        Assert.Equal(
            "http://localhost:8080/xboxpartner/v1/xboxlive/schemas",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal("caller-token", handler.AuthorizationParameter);
    }

    [Fact]
    public async Task GetSchemaTypesAsync_TimesOutWhileReadingSuccessfulResponseBody()
    {
        using var client = CreateClient(
            new StalledResponseHttpMessageHandler(HttpStatusCode.OK),
            timeoutSeconds: "0.05");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSchemaTypesAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task GetSchemaTypesAsync_TimesOutWhileReadingErrorResponseBody()
    {
        using var client = CreateClient(
            new StalledResponseHttpMessageHandler(
                HttpStatusCode.BadGateway, "request-correlation", TimeSpan.FromSeconds(12)),
            timeoutSeconds: "0.05");

        var error = await Assert.ThrowsAsync<XboxPartnerApiException>(
            () => client.GetSchemaTypesAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Empty(error.ResponseBody);
        Assert.Equal("request-correlation", error.CorrelationId);
        Assert.Equal(TimeSpan.FromSeconds(12), error.RetryAfter);
    }

    [Fact]
    public async Task GetSchemaAsync_ReturnsRawBytes()
    {
        byte[] expected = System.Text.Encoding.UTF8.GetBytes("<schema />");
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK, expected);
        using var client = CreateClient(handler);

        byte[] result = await client.GetSchemaAsync(
            "userstats",
            2,
            CancellationToken.None);

        Assert.Equal(expected, result);
        Assert.Equal(
            "http://localhost:8080/xboxpartner/v1/xboxlive/schemas/userstats/2",
            handler.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task GetSchemaAsync_TimesOutWhileReadingSuccessfulResponseBody()
    {
        using var client = CreateClient(
            new StalledResponseHttpMessageHandler(HttpStatusCode.OK),
            timeoutSeconds: "0.05");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSchemaAsync("userstats", 2, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task GetDocumentsAsync_TimesOutWhileReadingSuccessfulResponseBody()
    {
        using var client = CreateClient(
            new StalledResponseHttpMessageHandler(HttpStatusCode.OK),
            timeoutSeconds: "0.05");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetXboxLiveDocumentsAsync(
                    12345,
                    new XboxLiveGetDocumentsRequest(
                        "sandbox",
                        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                        "XDKS.1",
                        "working",
                        null,
                        []),
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task GetSchemaVersionsAsync_RejectsDotSegments(string schemaType)
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK, "[]");
        using var client = CreateClient(handler);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetSchemaVersionsAsync(schemaType, CancellationToken.None));

        Assert.Contains("path segment", exception.Message, StringComparison.Ordinal);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task GetSchemasCommand_RunAsync_WritesSchemaTypesAsJson()
    {
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """["achievements","userstats"]""");
        using var client = CreateClient(handler);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        await using var rawStdout = new MemoryStream();

        int exitCode = await GetSchemasCommandFactory.RunAsync(
            client,
            schemaType: null,
            version: null,
            destination: null,
            OutputOptions.JsonCompact,
            stdout,
            stderr,
            rawStdout,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("""["achievements","userstats"]""" + Environment.NewLine, stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task GetSchemasCommand_RunAsync_WritesSchemaTypesAsTable()
    {
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """["achievements","userstats"]""");
        using var client = CreateClient(handler);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        await using var rawStdout = new MemoryStream();

        int exitCode = await GetSchemasCommandFactory.RunAsync(
            client,
            schemaType: null,
            version: null,
            destination: null,
            OutputOptions.Table(XboxPartnerCommandSupport.SchemaTableView),
            stdout,
            stderr,
            rawStdout,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("TYPE", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("achievements", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("userstats", stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(rawStdout.ToArray());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task GetSchemasCommand_RunAsync_WritesRawSchema()
    {
        byte[] expected = System.Text.Encoding.UTF8.GetBytes("<schema />");
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK, expected);
        using var client = CreateClient(handler);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        await using var rawStdout = new MemoryStream();

        int exitCode = await GetSchemasCommandFactory.RunAsync(
            client,
            "userstats",
            2,
            destination: null,
            OutputOptions.Json,
            stdout,
            stderr,
            rawStdout,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, rawStdout.ToArray());
        Assert.Empty(stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task GetSchemasCommand_RunAsync_SummarizesRawSchemaAsTable()
    {
        byte[] content = System.Text.Encoding.UTF8.GetBytes("<schema />");
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK, content);
        using var client = CreateClient(handler);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        await using var rawStdout = new MemoryStream();

        int exitCode = await GetSchemasCommandFactory.RunAsync(
            client,
            "userstats",
            2,
            destination: null,
            OutputOptions.Table(XboxPartnerCommandSupport.SchemaTableView),
            stdout,
            stderr,
            rawStdout,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("TYPE", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("VERSION", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("BYTES", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("userstats", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains(content.Length.ToString(), stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(rawStdout.ToArray());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task GetSchemasCommand_RunAsync_WritesSchemaVersionsAsJson()
    {
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """[{"version":1,"namespace":"urn:one"},{"version":2,"namespace":"urn:two"}]""");
        using var client = CreateClient(handler);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        await using var rawStdout = new MemoryStream();

        int exitCode = await GetSchemasCommandFactory.RunAsync(
            client,
            "userstats",
            version: null,
            destination: null,
            OutputOptions.JsonCompact,
            stdout,
            stderr,
            rawStdout,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            """[{"version":1,"namespace":"urn:one"},{"version":2,"namespace":"urn:two"}]""" +
                Environment.NewLine,
            stdout.ToString());
        Assert.Equal(
            "http://localhost:8080/xboxpartner/v1/xboxlive/schemas/userstats",
            handler.RequestUri?.AbsoluteUri);
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task GetSchemasCommand_RunAsync_SavesSelectedSchemaAndWritesJson()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        try
        {
            byte[] expected = System.Text.Encoding.UTF8.GetBytes("<schema />");
            var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK, expected);
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            await using var rawStdout = new MemoryStream();

            int exitCode = await GetSchemasCommandFactory.RunAsync(
                client,
                "UserStats",
                2,
                directory,
                OutputOptions.JsonCompact,
                stdout,
                stderr,
                rawStdout,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            string path = Path.Combine(Path.GetFullPath(directory), "userstats_2.xsd");
            Assert.Equal(expected, await File.ReadAllBytesAsync(path));
            using JsonDocument output = JsonDocument.Parse(stdout.ToString());
            Assert.Equal("UserStats", output.RootElement.GetProperty("type").GetString());
            Assert.Equal(2, output.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(path, output.RootElement.GetProperty("path").GetString());
            Assert.Empty(rawStdout.ToArray());
            Assert.Empty(stderr.ToString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetSchemasCommand_RunAsync_DownloadsEverySchemaVersion()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        try
        {
            var handler = new RoutingHttpMessageHandler(request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                string response = path switch
                {
                    "/xboxpartner/v1/xboxlive/schemas" =>
                        """["userstats","achievements"]""",
                    "/xboxpartner/v1/xboxlive/schemas/userstats" =>
                        """[{"version":2,"namespace":"urn:userstats:2"}]""",
                    "/xboxpartner/v1/xboxlive/schemas/achievements" =>
                        """[{"version":3,"namespace":"urn:achievements:3"}]""",
                    "/xboxpartner/v1/xboxlive/schemas/userstats/2" => "<userstats />",
                    "/xboxpartner/v1/xboxlive/schemas/achievements/3" => "<achievements />",
                    _ => throw new InvalidOperationException($"Unexpected request path '{path}'."),
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response),
                };
            });
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            await using var rawStdout = new MemoryStream();

            int exitCode = await GetSchemasCommandFactory.RunAsync(
                client,
                schemaType: null,
                version: null,
                directory,
                OutputOptions.JsonCompact,
                stdout,
                stderr,
                rawStdout,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            string[] paths = JsonSerializer.Deserialize<string[]>(stdout.ToString())!;
            Assert.Equal(
                [
                    Path.Combine(Path.GetFullPath(directory), "userstats_2.xsd"),
                    Path.Combine(Path.GetFullPath(directory), "achievements_3.xsd"),
                ],
                paths);
            Assert.Equal("<userstats />", await File.ReadAllTextAsync(paths[0]));
            Assert.Equal("<achievements />", await File.ReadAllTextAsync(paths[1]));
            Assert.Equal(5, handler.RequestUris.Count);
            Assert.Empty(stderr.ToString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetSchemasCommand_RunAsync_SavesEveryVersionOfSelectedType()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        try
        {
            var handler = new RoutingHttpMessageHandler(request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                string response = path switch
                {
                    "/xboxpartner/v1/xboxlive/schemas/presence" =>
                        """[{"version":1,"namespace":"urn:presence:1"},{"version":2,"namespace":"urn:presence:2"}]""",
                    "/xboxpartner/v1/xboxlive/schemas/presence/1" => "<presence1 />",
                    "/xboxpartner/v1/xboxlive/schemas/presence/2" => "<presence2 />",
                    _ => throw new InvalidOperationException($"Unexpected request path '{path}'."),
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response),
                };
            });
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            await using var rawStdout = new MemoryStream();

            int exitCode = await GetSchemasCommandFactory.RunAsync(
                client,
                " presence ",
                version: null,
                directory,
                OutputOptions.JsonCompact,
                stdout,
                stderr,
                rawStdout,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            string[] paths = JsonSerializer.Deserialize<string[]>(stdout.ToString())!;
            Assert.Equal(
                [
                    Path.Combine(Path.GetFullPath(directory), "presence_1.xsd"),
                    Path.Combine(Path.GetFullPath(directory), "presence_2.xsd"),
                ],
                paths);
            Assert.Equal("<presence1 />", await File.ReadAllTextAsync(paths[0]));
            Assert.Equal("<presence2 />", await File.ReadAllTextAsync(paths[1]));
            Assert.Equal(3, handler.RequestUris.Count);
            Assert.Empty(rawStdout.ToArray());
            Assert.Empty(stderr.ToString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetSchemasCommand_RunAsync_UnknownTypeLeavesNoDestination()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        try
        {
            var handler = new CapturingHttpMessageHandler(
                HttpStatusCode.NotFound,
                """{"error":"not found"}""");
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            await using var rawStdout = new MemoryStream();

            int exitCode = await GetSchemasCommandFactory.RunAsync(
                client,
                "bogus",
                version: null,
                directory,
                OutputOptions.JsonCompact,
                stdout,
                stderr,
                rawStdout,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.False(Directory.Exists(directory));
            Assert.Empty(stdout.ToString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetSchemasCommand_RunAsync_RejectsSelectedSchemaReparsePoint()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        string targetPath = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}.xsd");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(targetPath, "existing");
            string schemaPath = Path.Combine(directory, "userstats_2.xsd");
            if (!TryCreateSymbolicLink(schemaPath, targetPath))
            {
                return;
            }

            var handler = new CapturingHttpMessageHandler(
                HttpStatusCode.OK,
                System.Text.Encoding.UTF8.GetBytes("<replacement />"));
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            await using var rawStdout = new MemoryStream();

            int exitCode = await GetSchemasCommandFactory.RunAsync(
                client,
                "userstats",
                2,
                directory,
                OutputOptions.JsonCompact,
                stdout,
                stderr,
                rawStdout,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal("existing", await File.ReadAllTextAsync(targetPath));
            Assert.Contains("reparse point", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            File.Delete(targetPath);
        }
    }

    [Fact]
    public async Task GetSchemasCommand_RunAsync_RejectsBulkSchemaReparsePoint()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        string targetPath = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}.xsd");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(targetPath, "existing");
            string schemaPath = Path.Combine(directory, "userstats_2.xsd");
            if (!TryCreateSymbolicLink(schemaPath, targetPath))
            {
                return;
            }

            var handler = new RoutingHttpMessageHandler(request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                string response = path switch
                {
                    "/xboxpartner/v1/xboxlive/schemas" => """["userstats"]""",
                    "/xboxpartner/v1/xboxlive/schemas/userstats" =>
                        """[{"version":2,"namespace":"urn:userstats:2"}]""",
                    "/xboxpartner/v1/xboxlive/schemas/userstats/2" => "<replacement />",
                    _ => throw new InvalidOperationException($"Unexpected request path '{path}'."),
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response),
                };
            });
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            await using var rawStdout = new MemoryStream();

            int exitCode = await GetSchemasCommandFactory.RunAsync(
                client,
                schemaType: null,
                version: null,
                directory,
                OutputOptions.JsonCompact,
                stdout,
                stderr,
                rawStdout,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal("existing", await File.ReadAllTextAsync(targetPath));
            Assert.Contains("reparse point", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            File.Delete(targetPath);
        }
    }

    [Fact]
    public async Task GetSchemasCommand_RunAsync_RejectsDestinationDirectoryReparsePoint()
    {
        string targetDirectory = Path.Combine(
            Path.GetTempPath(),
            $"XboxDevCLI-{Guid.NewGuid():N}");
        string linkedDirectory = Path.Combine(
            Path.GetTempPath(),
            $"XboxDevCLI-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(targetDirectory);
            if (!TryCreateDirectorySymbolicLink(linkedDirectory, targetDirectory))
            {
                return;
            }

            string destination = Path.Combine(linkedDirectory, "output");
            var handler = new CapturingHttpMessageHandler(
                HttpStatusCode.OK,
                System.Text.Encoding.UTF8.GetBytes("<schema />"));
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            await using var rawStdout = new MemoryStream();

            int exitCode = await GetSchemasCommandFactory.RunAsync(
                client,
                "userstats",
                2,
                destination,
                OutputOptions.JsonCompact,
                stdout,
                stderr,
                rawStdout,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains("reparse point", stderr.ToString(), StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(targetDirectory));
        }
        finally
        {
            if (Directory.Exists(linkedDirectory))
            {
                Directory.Delete(linkedDirectory);
            }

            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }
        }
    }

    private static bool TryCreateSymbolicLink(string path, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(path, targetPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string path, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(path, targetPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public void XboxPartnerCommandErrorFormatter_ReturnsMessageForUnpairedSurrogate()
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadGateway,
            "Bad Gateway",
            "\ud800",
            new Uri("https://gateway.example/xboxpartner/v1/xboxlive/sandboxes"));

        Assert.Equal(exception.Message, XboxPartnerCommandErrorFormatter.Format(exception, compact: true));
    }

    [Theory]
    [InlineData(29.2, 30L)]
    [InlineData(120.0, 120L)]
    [InlineData(0.0, 1L)]
    [InlineData(-1.0, 1L)]
    [InlineData(3000000000.0, 3000000000L)]
    public void XboxPartnerCommandErrorFormatter_ReportsRoundedRetrySeconds(double seconds, long expected)
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.TooManyRequests,
            "Too Many Requests",
            """{"title":"Throttled","detail":"Slow down.","code":"another_code"}""",
            new Uri("https://nodpublishers.com/request"),
            retryAfter: TimeSpan.FromSeconds(seconds));

        using var document = JsonDocument.Parse(XboxPartnerCommandErrorFormatter.Format(exception, compact: true));
        JsonElement error = document.RootElement;
        Assert.Equal(expected, error.GetProperty("retryAfterSeconds").GetInt64());
        Assert.Equal("another_code", error.GetProperty("code").GetString());
        Assert.Equal(
            $"Slow down. Try again in {expected} {(expected == 1 ? "second" : "seconds")}.",
            error.GetProperty("detail").GetString());
    }

    private const string ThrottledPlain =
        "The request failed with HTTP 429 (Too Many Requests). The response body could not be read.";
    private const string ThrottledStructured = "The service returned HTTP 429 (Too Many Requests).";

    [Theory]
    [InlineData("", ThrottledPlain)]
    [InlineData("not JSON", ThrottledPlain)]
    [InlineData("[]", ThrottledPlain)]
    [InlineData("null", ThrottledPlain)]
    [InlineData("\ud800", ThrottledPlain)]
    [InlineData("{}", ThrottledStructured)]
    [InlineData("""{"stackTrace":"private","detail":"private","code":"private"}""", ThrottledStructured)]
    public void XboxPartnerCommandErrorFormatter_ThrottledFallbackKeepsStructuredWait(string body, string statusSentence)
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.TooManyRequests,
            "Too Many Requests",
            body,
            new Uri("https://nodpublishers.com/request"),
            retryAfter: TimeSpan.FromSeconds(30),
            innerException: new IOException("private"));

        foreach (bool compact in new[] { true, false })
        {
            string formatted = XboxPartnerCommandErrorFormatter.Format(
                exception, compact, additionalDetail: "CREDENTIAL123",
                sensitiveValues: ["CREDENTIAL123"],
                fallbackTitle: "Operation failed");
            using var document = JsonDocument.Parse(formatted);
            JsonElement error = document.RootElement;
            Assert.Equal(429, error.GetProperty("status").GetInt32());
            Assert.Equal("Operation failed", error.GetProperty("title").GetString());
            Assert.Equal(30, error.GetProperty("retryAfterSeconds").GetInt64());
            Assert.Equal(
                $"{statusSentence} [redacted] Try again in 30 seconds.",
                error.GetProperty("detail").GetString());
            Assert.DoesNotContain("private", formatted, StringComparison.Ordinal);
            Assert.DoesNotContain("CREDENTIAL123", formatted, StringComparison.Ordinal);
            Assert.False(error.TryGetProperty("code", out _));
            Assert.Equal(!compact, formatted.Contains('\n'));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, null)]
    [InlineData(HttpStatusCode.BadGateway, 30.0)]
    public void XboxPartnerCommandErrorFormatter_OnlyAddsRetrySecondsForThrottlingWithAHeader(
        HttpStatusCode status, double? seconds)
    {
        var exception = new XboxPartnerApiException(
            status, null, """{"detail":"Request rejected."}""",
            new Uri("https://nodpublishers.com/request"),
            retryAfter: seconds is { } value ? TimeSpan.FromSeconds(value) : null);

        using var document = JsonDocument.Parse(XboxPartnerCommandErrorFormatter.Format(exception, compact: true));

        Assert.False(document.RootElement.TryGetProperty("retryAfterSeconds", out _));
        Assert.Equal(
            status == HttpStatusCode.TooManyRequests
                ? "Request rejected. Wait before retrying."
                : "Request rejected.",
            document.RootElement.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"stackTrace":"private"}""")]
    public void XboxPartnerCommandErrorFormatter_FallbackPreservesHttpDetails(string body)
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.ServiceUnavailable,
            "Maintenance until 12:00",
            body,
            new Uri("https://nodpublishers.com/request"),
            "CORRELATION123");

        string formatted = XboxPartnerCommandErrorFormatter.Format(exception, compact: true);

        Assert.Contains("HTTP 503 (Maintenance until 12:00).", formatted, StringComparison.Ordinal);
        Assert.Equal("Maintenance until 12:00", exception.ReasonPhrase);
        Assert.Equal("CORRELATION123", exception.CorrelationId);
        Assert.Equal(body, exception.ResponseBody);
        Assert.DoesNotContain("private", formatted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void XboxPartnerCommandErrorFormatter_EmptyReasonKeepsNumericStatus(string? reason)
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.ServiceUnavailable,
            reason,
            string.Empty,
            new Uri("https://nodpublishers.com/request"));

        Assert.Contains(
            "HTTP 503.",
            XboxPartnerCommandErrorFormatter.Format(exception, compact: true),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"headers":{"Authorization":"private"}}""")]
    public void XboxPartnerCommandErrorFormatter_PreservesExplicitFallbackPresentation(string body)
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.ServiceUnavailable,
            "Maintenance until 12:00",
            body,
            new Uri("https://nodpublishers.com/request"));

        string formatted = XboxPartnerCommandErrorFormatter.Format(
            exception,
            compact: true,
            sensitiveValues: ["CREDENTIAL123"],
            fallbackTitle: "Operation failed",
            fallbackDetail: "The operation failed with HTTP 503. CREDENTIAL123");

        Assert.Contains("The operation failed with HTTP 503. [redacted]", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("CREDENTIAL123", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("private", formatted, StringComparison.Ordinal);
        if (body.Length > 0)
        {
            using var document = JsonDocument.Parse(formatted);
            Assert.Equal("Operation failed", document.RootElement.GetProperty("title").GetString());
            Assert.Equal(503, document.RootElement.GetProperty("status").GetInt32());
        }
    }

    [Fact]
    public void XboxPartnerCommandErrorFormatter_PreservesServerProblemDetails()
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadRequest,
            "Bad Request",
            """{"title":"Invalid document","detail":"The document name was rejected.","code":"invalid_document","errors":{"Document":["The document name is not allowed."]}}""",
            new Uri("https://nodpublishers.com/request"));

        using var document = JsonDocument.Parse(
            XboxPartnerCommandErrorFormatter.Format(
                exception, compact: true,
                fallbackTitle: "Operation failed", fallbackDetail: "The operation failed."));
        JsonElement error = document.RootElement;

        Assert.Equal(400, error.GetProperty("status").GetInt32());
        Assert.Equal("Invalid document", error.GetProperty("title").GetString());
        Assert.Equal("The document name was rejected.", error.GetProperty("detail").GetString());
        Assert.Equal("invalid_document", error.GetProperty("code").GetString());
        Assert.Equal("The document name is not allowed.", error.GetProperty("errors").GetProperty("Document")[0].GetString());
    }

    [Fact]
    public void XboxPartnerCommandErrorFormatter_PreservesSafeProblemDetails()
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadRequest,
            "Bad Request",
            """{"title":"Invalid seller","detail":"The seller is not authorized."}""",
            new Uri("https://gateway.example/xboxpartner/v1/xboxlive/sandboxes"));

        string formatted = XboxPartnerCommandErrorFormatter.Format(exception, compact: true);
        using var document = JsonDocument.Parse(formatted);

        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Invalid seller", document.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "The seller is not authorized.",
            document.RootElement.GetProperty("detail").GetString());
        Assert.False(document.RootElement.TryGetProperty("code", out _));
    }

    [Fact]
    public void XboxPartnerCommandErrorFormatter_PreservesValidationMessages()
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.Conflict,
            "Conflict",
            """
            {
              "title": "XBOX Live validation failed.",
              "detail": "The configuration failed XBOX Live validation. See validation for each message.",
              "code": "validation_failed",
              "validation": [
                { "severity": "Error", "documentName": "achievements2017.xml", "error": "ChangedDocumentNotSupportedForXblLimited", "message": "XblLimited products do not support achievements. Seller 1234567." },
                { "severity": "Warning", "documentName": null, "message": "Check the title." },
                { "severity": "Error", "message": "" },
                "not an object"
              ]
            }
            """,
            new Uri("https://gateway.example/xboxpartner/v1/xboxlive/publish"));

        using var document = JsonDocument.Parse(XboxPartnerCommandErrorFormatter.Format(
            exception,
            compact: true,
            sensitiveValues: ["1234567"]));

        JsonElement[] validation = [.. document.RootElement.GetProperty("validation").EnumerateArray()];
        Assert.Equal(2, validation.Length);
        Assert.Equal("Error", validation[0].GetProperty("severity").GetString());
        Assert.Equal("achievements2017.xml", validation[0].GetProperty("documentName").GetString());
        Assert.Equal("ChangedDocumentNotSupportedForXblLimited", validation[0].GetProperty("error").GetString());
        Assert.Equal(
            "XblLimited products do not support achievements. Seller [redacted].",
            validation[0].GetProperty("message").GetString());
        Assert.Equal("Warning", validation[1].GetProperty("severity").GetString());
        Assert.False(validation[1].TryGetProperty("documentName", out _));
        Assert.False(validation[1].TryGetProperty("error", out _));
        Assert.Equal("validation_failed", document.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("""{"title":"Conflict","validation":[]}""")]
    [InlineData("""{"title":"Conflict","validation":{"message":"not an array"}}""")]
    [InlineData("""{"title":"Conflict","stackTrace":"at X","validation":[{"message":"Hidden."}]}""")]
    public void XboxPartnerCommandErrorFormatter_OmitsValidation_WhenThereAreNoUsableMessages(string response)
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.Conflict,
            "Conflict",
            response,
            new Uri("https://gateway.example/xboxpartner/v1/xboxlive/publish"));

        using var document = JsonDocument.Parse(XboxPartnerCommandErrorFormatter.Format(exception, compact: true));

        Assert.False(document.RootElement.TryGetProperty("validation", out _));
    }

    [Fact]
    public void XboxPartnerCommandErrorFormatter_PreservesTheProblemCode()
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadRequest,
            "Bad Request",
            """{"title":"Rejected","detail":"Name rejected.","code":"test_account_name_invalid"}""",
            new Uri("https://gateway.example/xboxpartner/v1/testaccounts/batches"));

        using var document = JsonDocument.Parse(XboxPartnerCommandErrorFormatter.Format(exception, compact: true));

        Assert.Equal(
            "test_account_name_invalid",
            document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void XboxPartnerCommandErrorFormatter_OnlyOptsInToPartialValidationErrors()
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadRequest,
            "Bad Request",
            """{"title":"One or more validation errors occurred.","status":400,"errors":{"subscriptions":["The JSON property 'subscriptions' could not be mapped."],"count":["The field Count must be between 1 and 2147483647.",7],"bad":"not an array"}}""",
            new Uri("https://gateway.example/xboxpartner/v1/testaccounts/batches"));

        using var withErrors = JsonDocument.Parse(
            XboxPartnerCommandErrorFormatter.Format(exception, compact: true, includeValidationErrors: true));
        using var withoutErrors = JsonDocument.Parse(XboxPartnerCommandErrorFormatter.Format(exception, compact: true));

        JsonElement errors = withErrors.RootElement.GetProperty("errors");
        Assert.Equal(
            "The JSON property 'subscriptions' could not be mapped.",
            Assert.Single(errors.GetProperty("subscriptions").EnumerateArray()).GetString());
        Assert.Equal(
            "The field Count must be between 1 and 2147483647.",
            Assert.Single(errors.GetProperty("count").EnumerateArray()).GetString());
        Assert.False(errors.TryGetProperty("bad", out _));
        Assert.False(withErrors.RootElement.TryGetProperty("code", out _));
        Assert.False(withoutErrors.RootElement.TryGetProperty("errors", out _));
    }

    [Fact]
    public void XboxPartnerCommandErrorFormatter_DropsValidationErrorsOnOtherStatuses()
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.Conflict,
            "Conflict",
            """{"title":"Conflict","errors":{"field":["message"]}}""",
            new Uri("https://gateway.example/xboxpartner/v1/testaccounts/batches"));

        using var document = JsonDocument.Parse(
            XboxPartnerCommandErrorFormatter.Format(exception, compact: true, includeValidationErrors: true));

        Assert.False(document.RootElement.TryGetProperty("errors", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void XboxPartnerCommandErrorFormatter_PreservesFieldValidationErrors(bool compact)
    {
        // Synthetic ASP.NET ValidationProblemDetails; no live response body was captured.
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadRequest,
            "Bad Request",
            """{"title":"One or more validation errors occurred.","errors":{"File":["PNG could not be read."],"Scid":["A SCID is required.","Use a valid GUID."]},"traceId":"private-trace"}""",
            new Uri("https://gateway.example/xboxpartner/v1/xboxlive/achievement-images/upload"));

        string formatted = XboxPartnerCommandErrorFormatter.Format(
            exception, compact, "Image requirements.", omitAdditionalDetailWithFieldErrors: true);
        using var document = JsonDocument.Parse(formatted);
        JsonElement root = document.RootElement;

        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.Equal("One or more validation errors occurred.", root.GetProperty("title").GetString());
        Assert.Equal("The service returned HTTP 400 (Bad Request).", root.GetProperty("detail").GetString());
        Assert.Equal(["PNG could not be read."],
            root.GetProperty("errors").GetProperty("File").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["A SCID is required.", "Use a valid GUID."],
            root.GetProperty("errors").GetProperty("Scid").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(4, root.EnumerateObject().Count());
        Assert.DoesNotContain("private-trace", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("Image requirements.", formatted, StringComparison.Ordinal);
        Assert.Equal(compact ? false : true, formatted.Contains(Environment.NewLine, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void XboxPartnerCommandErrorFormatter_PreservesRequestWideValidationErrors(
        bool compact, bool withNamedField)
    {
        // Synthetic ASP.NET validation body; the live HTTP 400 body was not captured.
        string body = withNamedField
            ? """{"title":"Validation failed","errors":{"":["The request body could not be read."],"File":["Upload a PNG."]},"traceId":"private"}"""
            : """{"title":"Validation failed","errors":{"":["The request body could not be read."]},"traceId":"private"}""";
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadRequest,
            "Bad Request",
            body,
            new Uri("https://gateway.example/xboxpartner/v1/xboxlive/achievement-images/upload"));

        string formatted = XboxPartnerCommandErrorFormatter.Format(
            exception, compact, "Image requirements.", omitAdditionalDetailWithFieldErrors: true);
        using var document = JsonDocument.Parse(formatted);
        JsonElement root = document.RootElement;
        JsonElement errors = root.GetProperty("errors");

        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.Equal("The request body could not be read.", errors.GetProperty("")[0].GetString());
        Assert.Equal(withNamedField, errors.TryGetProperty("File", out JsonElement named));
        if (withNamedField)
        {
            Assert.Equal("Upload a PNG.", named[0].GetString());
        }

        Assert.Equal(withNamedField ? 2 : 1, errors.EnumerateObject().Count());
        Assert.DoesNotContain("Image requirements.", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("private", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void XboxPartnerCommandErrorFormatter_PreservesSafeDetailAlongsideFieldErrors()
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadRequest,
            "Bad Request",
            """{"title":"Validation failed","detail":"The request is invalid.","errors":{"File":["Upload a PNG."]},"ignored":"private"}""",
            new Uri("https://gateway.example/xboxpartner/v1/xboxlive/achievement-images/upload"));

        using var document = JsonDocument.Parse(
            XboxPartnerCommandErrorFormatter.Format(exception, compact: true));
        Assert.Equal("The request is invalid.", document.RootElement.GetProperty("detail").GetString());
        Assert.Equal("Upload a PNG.",
            document.RootElement.GetProperty("errors").GetProperty("File")[0].GetString());
        Assert.False(document.RootElement.TryGetProperty("ignored", out _));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"errors":{}}""")]
    [InlineData("""{"errors":{"File":[]}}""")]
    [InlineData("""{"errors":{"File":[""]}}""")]
    [InlineData("""{"errors":{"File":["valid", 3]}}""")]
    [InlineData("""{"errors":{"File":{"message":"private"}}}""")]
    [InlineData("""{"errors":{"":[" "]}}""")]
    [InlineData("""{"errors":{"":["valid"],"File":[true]}}""")]
    [InlineData("""{"errors":["private"]}""")]
    public void XboxPartnerCommandErrorFormatter_RejectsMalformedOrEmptyFieldErrors(string response)
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadRequest,
            "Bad Request",
            response,
            new Uri("https://gateway.example/xboxpartner/v1/xboxlive/achievement-images/upload"));

        using var document = JsonDocument.Parse(XboxPartnerCommandErrorFormatter.Format(
            exception, compact: true, "Image requirements.",
            omitAdditionalDetailWithFieldErrors: true));
        Assert.False(document.RootElement.TryGetProperty("errors", out _));
        Assert.Contains("Image requirements.",
            document.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void XboxPartnerCommandErrorFormatter_DropsErrorsFromDiagnosticBearingPayload()
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadRequest,
            "Bad Request",
            """{"title":"Internal failure","detail":"secret","headers":{"Authorization":"Bearer private"},"errors":{"File":["secret validation message"]}}""",
            new Uri("https://gateway.example/xboxpartner/v1/xboxlive/achievement-images/upload"));

        using var document = JsonDocument.Parse(XboxPartnerCommandErrorFormatter.Format(
            exception, compact: true, "Image requirements.",
            omitAdditionalDetailWithFieldErrors: true));
        Assert.False(document.RootElement.TryGetProperty("errors", out _));
        Assert.Equal("Request failed",
            document.RootElement.GetProperty("title").GetString());
        Assert.Contains("Image requirements.",
            document.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", document.RootElement.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void XboxPartnerCommandErrorFormatter_UsesExistingFallbackForInvalidJson()
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadRequest,
            "Bad Request",
            """{"errors":{"File":["private"]}""",
            new Uri("https://gateway.example/xboxpartner/v1/xboxlive/achievement-images/upload"));

        Assert.Equal(
            $"{exception.Message} Image requirements.",
            XboxPartnerCommandErrorFormatter.Format(
                exception, compact: true, "Image requirements.",
                omitAdditionalDetailWithFieldErrors: true));
    }

    [Fact]
    public void XboxPartnerCommandErrorFormatter_RemovesDiagnosticDetails()
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadGateway,
            "Bad Gateway",
            """{"title":"Internal failure","detail":"secret","stackTrace":"sensitive","code":"leaky"}""",
            new Uri("https://gateway.example/xboxpartner/v1/xboxlive/sandboxes"));

        string formatted = XboxPartnerCommandErrorFormatter.Format(exception, compact: true);
        using var document = JsonDocument.Parse(formatted);

        Assert.Equal(
            "Request failed",
            document.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "The service returned HTTP 502 (Bad Gateway).",
            document.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("secret", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("leaky", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void XboxPartnerCommandErrorFormatter_StatesStatusWhenObjectHasNoDetail()
    {
        var exception = new XboxPartnerApiException(
            (HttpStatusCode)503,
            reasonPhrase: null,
            """{"code":"Unavailable"}""",
            new Uri("https://gateway.example/xboxpartner/v1/xboxlive/sandboxes"));

        using var document = JsonDocument.Parse(
            XboxPartnerCommandErrorFormatter.Format(exception, compact: true, additionalDetail: "Retry later."));

        Assert.Equal(503, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Request failed", document.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "The service returned HTTP 503. Retry later.",
            document.RootElement.GetProperty("detail").GetString());
        Assert.Equal("Unavailable", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void XboxPartnerCommandErrorFormatter_ReturnsMessageForNonObjectJson()
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadGateway,
            "Bad Gateway",
            """["unexpected"]""",
            new Uri("https://gateway.example/xboxpartner/v1/xboxlive/sandboxes"));

        Assert.Equal(
            exception.Message,
            XboxPartnerCommandErrorFormatter.Format(exception, compact: false));
    }

    [Fact]
    public void XboxPartnerCommandErrorFormatter_AppendsHintForNonObjectJson()
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadGateway,
            "Bad Gateway",
            """["unexpected"]""",
            new Uri("https://gateway.example/xboxpartner/xboxlive/sandboxes"));

        Assert.Equal(
            $"{exception.Message} Retry first.",
            XboxPartnerCommandErrorFormatter.Format(exception, compact: false, additionalDetail: "Retry first."));
    }

    [Fact]
    public async Task CommitXboxLiveDocumentsAsync_SendsCommitParameters()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK, """{"version":42}""");
        using var client = CreateClient(handler);
        var scid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        await client.CommitXboxLiveDocumentsAsync(
            12345,
            new XboxLiveCommitDocumentsRequest(
                "sandbox",
                scid,
                "XDKS.1",
                true,
                false,
                "\"etag\"",
                "Validate changes",
                [new XboxDocumentUpload("achievements.xml", [1, 2, 3])]),
            CancellationToken.None);

        Assert.Equal(
            "http://localhost:8080/xboxpartner/v1/xboxlive/documents/commit",
            handler.RequestUri?.AbsoluteUri);
        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(scid, body.RootElement.GetProperty("scid").GetGuid());
        Assert.True(body.RootElement.GetProperty("validateOnly").GetBoolean());
        Assert.Equal("achievements.xml", body.RootElement.GetProperty("documents")[0]
            .GetProperty("name").GetString());
        Assert.False(body.RootElement.TryGetProperty("userId", out _));
        Assert.False(body.RootElement.TryGetProperty("userEmail", out _));
    }

    [Fact]
    public async Task PublishXboxLiveConfigurationAsync_SendsPublishParameters()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK, """{"status":"Pending"}""");
        using var client = CreateClient(handler);
        var scid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        await client.PublishXboxLiveConfigurationAsync(
            12345,
            new XboxLivePublishRequest(
                scid,
                "RETAIL",
                "XDKS.1",
                true,
                42),
            CancellationToken.None);

        Assert.Equal(
            "http://localhost:8080/xboxpartner/v1/xboxlive/publish",
            handler.RequestUri?.AbsoluteUri);
        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("RETAIL", body.RootElement.GetProperty("sandbox").GetString());
        Assert.Equal("XDKS.1", body.RootElement.GetProperty("sourceSandbox").GetString());
        Assert.False(body.RootElement.TryGetProperty("sourceView", out _));
        Assert.False(body.RootElement.TryGetProperty("destinationSandbox", out _));
        Assert.True(body.RootElement.GetProperty("validateOnly").GetBoolean());
        Assert.Equal(42UL, body.RootElement.GetProperty("configSetVersion").GetUInt64());
        Assert.False(body.RootElement.GetProperty("republish").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("userId", out _));
        Assert.False(body.RootElement.TryGetProperty("userEmail", out _));
    }

    [Fact]
    public async Task PublishXboxLiveConfigurationAsync_SendsRepublish()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK, """{"status":"Running"}""");
        using var client = CreateClient(handler);

        await client.PublishXboxLiveConfigurationAsync(
            12345,
            new XboxLivePublishRequest(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "RETAIL",
                "RETAIL",
                false,
                null,
                Republish: true),
            CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(body.RootElement.GetProperty("republish").GetBoolean());
    }

    [Fact]
    public async Task GetXboxLivePublishStatusAsync_SendsStatusParameters()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK, """{"status":"Completed"}""");
        using var client = CreateClient(handler);
        var scid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        await client.GetXboxLivePublishStatusAsync(
            12345,
            scid,
            "RETAIL",
            CancellationToken.None);

        Assert.Equal(
            "http://localhost:8080/xboxpartner/v1/xboxlive/publish/status",
            handler.RequestUri?.AbsoluteUri);
        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(scid, body.RootElement.GetProperty("scid").GetGuid());
        Assert.Equal("RETAIL", body.RootElement.GetProperty("sandbox").GetString());
        Assert.False(body.RootElement.TryGetProperty("userId", out _));
        Assert.False(body.RootElement.TryGetProperty("userEmail", out _));
    }

    [Fact]
    public void Step3Commands_AcceptOmittedIdentityOptions()
    {
        var publish = Program.BuildRootCommand().Parse(
            "xbl publish start --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee " +
            "--sandbox-id RETAIL --source-sandbox-id XDKS.1");
        var status = Program.BuildRootCommand().Parse(
            "xbl publish status --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id RETAIL");
        var commit = Program.BuildRootCommand().Parse(
            "xbl document commit --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee " +
            "--sandbox-id XDKS.1 --file achievements.xml --validate-only");

        Assert.Empty(publish.Errors);
        Assert.Empty(status.Errors);
        Assert.Empty(commit.Errors);

        Assert.NotEmpty(Program.BuildRootCommand().Parse(
            "xbl publish start --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee " +
            "--sandbox-id RETAIL --source-sandbox-id XDKS.1 --user-id 1").Errors);
        Assert.NotEmpty(Program.BuildRootCommand().Parse(
            "xbl publish start --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee " +
            "--from XDKS.1 --to RETAIL").Errors);
        Assert.NotEmpty(Program.BuildRootCommand().Parse(
            "xbl publish start --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee " +
            "--sandbox-id RETAIL --source-sandbox-id XDKS.1 --source-view working").Errors);
        Assert.NotEmpty(Program.BuildRootCommand().Parse(
            "xbl publish status --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee " +
            "--sandbox-id RETAIL --user-email developer@example.com").Errors);
    }

    [Fact]
    public async Task PublishCommands_RunAsync_WriteJsonResponses()
    {
        var publishHandler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """{"status":"Pending"}""");
        using var publishClient = CreateClient(publishHandler);
        using var publishOutput = new StringWriter();
        using var publishErrors = new StringWriter();
        const long sellerId = 12345;
        var scid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        int publishExitCode = await PublishCommandFactory.RunAsync(
            publishClient,
            sellerId,
            new XboxLivePublishRequest(
                scid,
                "RETAIL",
                "XDKS.1",
                false,
                42),
            OutputOptions.JsonCompact,
            publishOutput,
            publishErrors,
            CancellationToken.None);

        var statusHandler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """{"status":"Completed"}""");
        using var statusClient = CreateClient(statusHandler);
        using var statusOutput = new StringWriter();
        using var statusErrors = new StringWriter();
        int statusExitCode = await GetPublishStatusCommandFactory.RunAsync(
            statusClient,
            sellerId,
            scid,
            "RETAIL",
            OutputOptions.JsonCompact,
            statusOutput,
            statusErrors,
            CancellationToken.None);

        Assert.Equal(0, publishExitCode);
        Assert.Equal("""{"status":"Pending"}""" + Environment.NewLine, publishOutput.ToString());
        Assert.Equal(
            ExpectedGlobalStorageWarning("XDKS.1", "RETAIL") + Environment.NewLine,
            publishErrors.ToString());
        Assert.Equal(0, statusExitCode);
        Assert.Equal("""{"status":"Completed"}""" + Environment.NewLine, statusOutput.ToString());
        Assert.Empty(statusErrors.ToString());
    }

    [Fact]
    public async Task PublishCommands_RunAsync_WriteTableResponses()
    {
        var publishHandler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """{"status":"Pending"}""");
        using var publishClient = CreateClient(publishHandler);
        using var publishOutput = new StringWriter();
        using var publishErrors = new StringWriter();
        var scid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        int publishExitCode = await PublishCommandFactory.RunAsync(
            publishClient,
            12345,
            new XboxLivePublishRequest(
                scid,
                "RETAIL",
                "XDKS.1",
                false,
                null),
            OutputOptions.Table(XboxPartnerCommandSupport.PublishTableView),
            publishOutput,
            publishErrors,
            CancellationToken.None);

        var statusHandler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """{"status":"Completed"}""");
        using var statusClient = CreateClient(statusHandler);
        using var statusOutput = new StringWriter();
        using var statusErrors = new StringWriter();
        int statusExitCode = await GetPublishStatusCommandFactory.RunAsync(
            statusClient,
            12345,
            scid,
            "RETAIL",
            OutputOptions.Table(XboxPartnerCommandSupport.PublishStatusTableView),
            statusOutput,
            statusErrors,
            CancellationToken.None);

        Assert.Equal(0, publishExitCode);
        Assert.Contains("STATUS", publishOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("Pending", publishOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("SOURCE VERSION", publishOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SOURCE VIEW", publishOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("latest", publishOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            ExpectedGlobalStorageWarning("XDKS.1", "RETAIL") + Environment.NewLine,
            publishErrors.ToString());
        Assert.Equal(0, statusExitCode);
        Assert.Contains("STATUS", statusOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("Completed", statusOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("RETAIL", statusOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(statusErrors.ToString());
    }

    [Theory]
    [InlineData("not-json", "not valid JSON")]
    [InlineData("{}", "did not contain a status")]
    [InlineData("""{"status":"   "}""", "did not contain a status")]
    public async Task PublishCommands_RunAsync_RejectMalformedSuccessResponses(
        string responseBody,
        string expectedError)
    {
        var scid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        using var publishClient = CreateClient(
            new CapturingHttpMessageHandler(HttpStatusCode.OK, responseBody));
        using var publishOutput = new StringWriter();
        using var publishErrors = new StringWriter();
        int publishExitCode = await PublishCommandFactory.RunAsync(
            publishClient,
            12345,
            new XboxLivePublishRequest(
                scid,
                "RETAIL",
                "XDKS.1",
                false,
                null),
            OutputOptions.Json,
            publishOutput,
            publishErrors,
            CancellationToken.None);

        using var statusClient = CreateClient(
            new CapturingHttpMessageHandler(HttpStatusCode.OK, responseBody));
        using var statusOutput = new StringWriter();
        using var statusErrors = new StringWriter();
        int statusExitCode = await GetPublishStatusCommandFactory.RunAsync(
            statusClient,
            12345,
            scid,
            "RETAIL",
            OutputOptions.Json,
            statusOutput,
            statusErrors,
            CancellationToken.None);

        Assert.Equal(1, publishExitCode);
        Assert.Empty(publishOutput.ToString());
        Assert.Contains(expectedError, publishErrors.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, statusExitCode);
        Assert.Empty(statusOutput.ToString());
        Assert.Contains(expectedError, statusErrors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishStart_RunAsync_WritesXorcValidationMessagesToStderr()
    {
        using var client = CreateClient(new CapturingHttpMessageHandler(
            HttpStatusCode.Conflict,
            """{"status":409,"title":"XBOX Live validation failed.","detail":"The configuration failed XBOX Live validation. See validation for each message.","code":"validation_failed","validation":[{"severity":"Error","documentName":"achievements2017.xml","error":null,"message":"XblLimited products do not support achievements or stats2013/stats2015 configuration."}]}"""));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await PublishCommandFactory.RunAsync(
            client,
            12345,
            new XboxLivePublishRequest(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "XDKS.1",
                "XDKS.1",
                true,
                null),
            OutputOptions.Json,
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout.ToString());
        using var document = JsonDocument.Parse(stderr.ToString());
        JsonElement message = Assert.Single(document.RootElement.GetProperty("validation").EnumerateArray());
        Assert.Equal("achievements2017.xml", message.GetProperty("documentName").GetString());
        Assert.False(message.TryGetProperty("error", out _));
        Assert.StartsWith("XblLimited products", message.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishStart_UsesRegisteredClientFactory()
    {
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """{"Status":"Running","sourceHasUnpublishedChanges":true,"sourceLiveVersion":52,"SourceVersion":"53"}""");

        CapturedInvocation result = await InvokeProgramAsync(
            [
                "xbl", "publish", "start",
                "--seller-id", "12345",
                "--scid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "--source-sandbox-id", "XDKS.1",
                "--sandbox-id", "RETAIL",
                "--output", "table",
                "--confirm",
            ],
            handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Running", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("53", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            ExpectedGlobalStorageWarning("XDKS.1", "RETAIL"),
            result.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain("global title storage", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            "Warning: XDKS.1 has unpublished changes. This publish uses its latest working " +
            "version, 53; what's live in XDKS.1 is working version 52. To publish that instead, " +
            "add --configset 52.",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Equal(
            "/xboxpartner/v1/xboxlive/publish",
            handler.RequestUri?.AbsolutePath);
        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(body.RootElement.TryGetProperty("sourceView", out _));
        Assert.Equal("XDKS.1", body.RootElement.GetProperty("sourceSandbox").GetString());
        Assert.Equal("RETAIL", body.RootElement.GetProperty("sandbox").GetString());
    }

    [Fact]
    public async Task PublishStart_DefaultsSourceToDestinationAndLatestWorkingVersion()
    {
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """{"status":"Pending"}""");

        CapturedInvocation result = await InvokeProgramAsync(
            [
                "xbl", "publish", "start",
                "--seller-id", "12345",
                "--scid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "--sandbox-id", "XDKS.1",
            ],
            handler);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("global title storage", result.Stderr, StringComparison.Ordinal);
        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("XDKS.1", body.RootElement.GetProperty("sourceSandbox").GetString());
        Assert.False(body.RootElement.TryGetProperty("sourceView", out _));
        Assert.Equal(
            JsonValueKind.Null,
            body.RootElement.GetProperty("configSetVersion").ValueKind);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public void PublishStart_SanitizesTheGatewaysSourceVersionInTheWarning()
    {
        string? warning = PublishCommandFactory.FormatSourceWarning(
            new XboxLivePublishRequest(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "TESTSB.42",
                "XDKS.1",
                false,
                null),
            XboxPartnerCommandSupport.DeserializeResponse<XboxLivePublishResponse>(
                """{"Status":"Running","sourceHasUnpublishedChanges":true,"SourceVersion":"53\nWarning: forged\u001b[2J"}""",
                "publish"));

        Assert.NotNull(warning);
        Assert.DoesNotContain('\n', warning);
        Assert.DoesNotContain('\u001b', warning);
        Assert.Contains(
            XboxPartnerCommandSupport.ForTerminal("53\nWarning: forged\u001b[2J"),
            warning,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """{"Status":"Running","sourceHasUnpublishedChanges":false,"sourceLiveVersion":53,"SourceVersion":"53"}""",
        false,
        null)]
    [InlineData("""{"Status":"Running"}""", false, null)]
    [InlineData(
        """{"Status":"Running","sourceHasUnpublishedChanges":true,"sourceLiveVersion":52,"SourceVersion":"53"}""",
        false,
        "Warning: XDKS.1 has unpublished changes. This publish uses its latest working version, 53; " +
        "what's live in XDKS.1 is working version 52. To publish that instead, add --configset 52.")]
    [InlineData(
        """{"Status":"Success","sourceHasUnpublishedChanges":true,"sourceLiveVersion":52,"SourceVersion":53}""",
        true,
        "Warning: XDKS.1 has unpublished changes. This validation uses its latest working version, 53; " +
        "what's live in XDKS.1 is working version 52. To validate that instead, add --configset 52.")]
    [InlineData(
        """{"Status":"Running","sourceHasUnpublishedChanges":true,"SourceVersion":"53"}""",
        false,
        "Warning: XDKS.1 has unpublished changes. This publish uses its latest working version, 53, " +
        "which isn't what's live in XDKS.1.")]
    [InlineData(
        """{"Status":"Running","sourceHasUnpublishedChanges":true,"SourceVersion":null}""",
        false,
        "Warning: XDKS.1 has unpublished changes. This publish uses its latest working version, " +
        "which isn't what's live in XDKS.1.")]
    public async Task PublishStart_WarnsWhenTheSourcesLatestWorkingVersionIsNotLive(
        string responseBody,
        bool validateOnly,
        string? expectedWarning)
    {
        using var client = CreateClient(
            new CapturingHttpMessageHandler(HttpStatusCode.OK, responseBody));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await PublishCommandFactory.RunAsync(
            client,
            12345,
            new XboxLivePublishRequest(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "TESTSB.42",
                "XDKS.1",
                validateOnly,
                null),
            OutputOptions.JsonCompact,
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(responseBody + Environment.NewLine, stdout.ToString());
        Assert.Equal(
            ExpectedGlobalStorageWarning("XDKS.1", "TESTSB.42") + Environment.NewLine +
                (expectedWarning is null ? string.Empty : expectedWarning + Environment.NewLine),
            stderr.ToString());
    }

    [Fact]
    public async Task PublishStart_TreatsCaseOnlySandboxDifferenceAsSameSandbox()
    {
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """{"status":"Pending"}""");

        CapturedInvocation result = await InvokeProgramAsync(
            [
                "xbl", "publish", "start",
                "--seller-id", "12345",
                "--scid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "--source-sandbox-id", "xdks.1",
                "--sandbox-id", "XDKS.1",
            ],
            handler);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("global title storage", result.Stderr, StringComparison.Ordinal);
        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(body.RootElement.TryGetProperty("sourceView", out _));
    }

    [Theory]
    [InlineData("XDKS.1", "RETAIL", true, true)]
    [InlineData("XDKS.1", "RETAIL", false, true)]
    [InlineData("xdks.1", "XDKS.1", false, false)]
    [InlineData("XDKS.1", "XDKS.1", false, false)]
    [InlineData("XDKS.1", "XDKS.1", true, false)]
    public async Task PublishStart_RunAsync_WarnsAboutGlobalStorageOnlyAcrossSandboxes(
        string sourceSandbox,
        string sandbox,
        bool validateOnly,
        bool expectWarning)
    {
        using var client = CreateClient(
            new CapturingHttpMessageHandler(HttpStatusCode.OK, """{"status":"Pending"}"""));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await PublishCommandFactory.RunAsync(
            client,
            12345,
            new XboxLivePublishRequest(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                sandbox,
                sourceSandbox,
                validateOnly,
                null),
            OutputOptions.JsonCompact,
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("""{"status":"Pending"}""" + Environment.NewLine, stdout.ToString());
        Assert.Equal(
            expectWarning
                ? ExpectedGlobalStorageWarning(sourceSandbox, sandbox) + Environment.NewLine
                : string.Empty,
            stderr.ToString());
    }

    [Fact]
    public async Task PublishStart_RunAsync_WarnsAboutGlobalStorageBeforeAFailedRequest()
    {
        using var client = CreateClient(
            new CapturingHttpMessageHandler(HttpStatusCode.OK, "not-json"));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await PublishCommandFactory.RunAsync(
            client,
            12345,
            new XboxLivePublishRequest(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "TESTSB.42",
                "XDKS.1",
                false,
                null),
            OutputOptions.Json,
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout.ToString());
        string errors = stderr.ToString();
        Assert.StartsWith(
            ExpectedGlobalStorageWarning("XDKS.1", "TESTSB.42") + Environment.NewLine,
            errors,
            StringComparison.Ordinal);
        Assert.Contains("not valid JSON", errors, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishStart_HelpDescribesGlobalStorageReplacement()
    {
        var metadata = CommandMetadataRegistry.GetMetadata(
            Program.BuildRootCommand()
                .Parse("xbl publish start --help")
                .CommandResult.Command);

        Assert.Contains(
            "global title storage files",
            metadata.SafetyDetails,
            StringComparison.Ordinal);
    }

    private static string ExpectedGlobalStorageWarning(string sourceSandbox, string sandbox) =>
        $"Warning: publishing from {sourceSandbox} to {sandbox} replaces {sandbox}'s global " +
        $"title storage files with {sourceSandbox}'s. Files with the same path are " +
        $"overwritten, files only {sourceSandbox} holds are added, and files only {sandbox} " +
        "holds are deleted. Deleted files can't be recovered.";

    [Fact]
    public async Task ConfigurationsList_WritesJsonAndTableOutput()
    {
        const string response =
            """
            {"configurations":[{
              "sandbox": "DEV.0",
              "hasWorkingConfiguration": true,
              "workingVersion": 7,
              "workingCommitTime": "2026-09-25T18:00:00Z",
              "hasPublishedConfiguration": true,
              "publishedVersion": 5,
              "publishedCommitTime": "2026-09-24T17:00:00Z",
              "hasUnpublishedChanges": true
            }]}
            """;
        var scid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        using var jsonClient = CreateClient(
            new CapturingHttpMessageHandler(HttpStatusCode.OK, response));
        using var jsonOutput = new StringWriter();
        using var jsonErrors = new StringWriter();
        int jsonExitCode = await ListConfigurationsCommandFactory.RunAsync(
            jsonClient,
            12345,
            scid,
            OutputOptions.Json,
            jsonOutput,
            jsonErrors,
            CancellationToken.None);

        using var tableClient = CreateClient(
            new CapturingHttpMessageHandler(HttpStatusCode.OK, response));
        using var tableOutput = new StringWriter();
        using var tableErrors = new StringWriter();
        int tableExitCode = await ListConfigurationsCommandFactory.RunAsync(
            tableClient,
            12345,
            scid,
            OutputOptions.Table(XboxPartnerCommandSupport.ConfigurationRecordTableView),
            tableOutput,
            tableErrors,
            CancellationToken.None);

        Assert.Equal(0, jsonExitCode);
        using JsonDocument json = JsonDocument.Parse(jsonOutput.ToString());
        Assert.Equal("DEV.0", json.RootElement[0].GetProperty("sandbox").GetString());
        Assert.Empty(jsonErrors.ToString());
        Assert.Equal(0, tableExitCode);
        Assert.Contains("SANDBOX", tableOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("DEV.0", tableOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("7", tableOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("5", tableOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("Yes", tableOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("2026-09-25 18:00:00 Z", tableOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(tableErrors.ToString());
    }

    [Fact]
    public async Task ConfigurationsList_UsesRegisteredClientFactory()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK, """{"configurations":[]}""");

        CapturedInvocation result = await InvokeProgramAsync(
            [
                "xbl", "configuration", "list",
                "--seller-id", "12345",
                "--scid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "--output", "table",
            ],
            handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "/xboxpartner/v1/xboxlive/configurations",
            handler.RequestUri?.AbsolutePath);
    }

    [Theory]
    [InlineData(HttpStatusCode.GatewayTimeout, """{"title":"Gateway Timeout","code":"downstream_timeout"}""", "\"downstream_timeout\"")]
    [InlineData(HttpStatusCode.OK, "[]", "Request failed: The configuration list response must contain the configurations array.")]
    public async Task ConfigurationsList_FailureOnALaterPageWritesTheCorrelationIdAndNothingToStdout(
        HttpStatusCode statusCode,
        string body,
        string expected)
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Page("""{"configurations":[{"sandbox":"XDKS.1"}],"continuationToken":"t1"}"""),
            new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
        using var client = CreateClient(handler);
        using var output = new StringWriter();
        using var errors = new StringWriter();

        int exitCode = await ListConfigurationsCommandFactory.RunAsync(
            client,
            12345,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            OutputOptions.Json,
            output,
            errors,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Empty(output.ToString());
        string correlationId = Assert.Single(handler.Requests[1].Headers["x-ms-correlation-id"]);
        Assert.Equal(correlationId, Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]));
        Assert.StartsWith(
            $"x-ms-correlation-id: {correlationId}{Environment.NewLine}",
            errors.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(expected, errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigurationGet_WritesTableOutput()
    {
        const string response =
            """
            {
              "sandbox": "STAGE.0",
              "hasWorkingConfiguration": true,
              "workingVersion": 9,
              "workingCommitTime": "2026-09-25T18:00:00Z",
              "hasPublishedConfiguration": true,
              "publishedVersion": 9,
              "publishedCommitTime": "2026-09-25T17:00:00Z",
              "hasUnpublishedChanges": false
            }
            """;
        using var client = CreateClient(
            new CapturingHttpMessageHandler(HttpStatusCode.OK, response));
        using var output = new StringWriter();
        using var errors = new StringWriter();

        int exitCode = await GetConfigurationCommandFactory.RunAsync(
            client,
            12345,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            "STAGE.0",
            OutputOptions.Table(XboxPartnerCommandSupport.ConfigurationTableView),
            output,
            errors,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("STAGE.0", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("2026-09-25 18:00:00 Z", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("No", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(errors.ToString());
    }

    [Fact]
    public void BuildRootCommand_RegistersXboxLiveResources()
    {
        var root = Program.BuildRootCommand();
        var sandbox = Assert.Single(root.Subcommands, command => command.Name == "sandbox");
        Assert.Contains(sandbox.Subcommands, command => command.Name == "list");

        var xboxLive = Assert.Single(root.Subcommands, command => command.Name == "xbl");
        Assert.DoesNotContain(xboxLive.Subcommands, command => command.Name == "sandboxes");
        var configuration = Assert.Single(
            xboxLive.Subcommands,
            command => command.Name == "configuration");
        Assert.Contains(configuration.Subcommands, command => command.Name == "list");
        Assert.Contains(configuration.Subcommands, command => command.Name == "get");
        Assert.True(Program.SupportsTableOutput(
            configuration.Subcommands.Single(command => command.Name == "list")));
        Assert.True(Program.SupportsTableOutput(
            configuration.Subcommands.Single(command => command.Name == "get")));
        Assert.Contains(
            xboxLive.Subcommands.Single(command => command.Name == "schema").Subcommands,
            command => command.Name == "get");
        var documentCommands = xboxLive.Subcommands
            .Single(command => command.Name == "document")
            .Subcommands;
        Assert.Contains(documentCommands, command => command.Name == "download");
        Assert.Contains(documentCommands, command => command.Name == "commit");
        var publishCommands = xboxLive.Subcommands
            .Single(command => command.Name == "publish")
            .Subcommands;
        Assert.Contains(publishCommands, command => command.Name == "start");
        Assert.Contains(publishCommands, command => command.Name == "status");
        Assert.DoesNotContain(publishCommands, command => command.Name is "list" or "get");
        var achievementImages = xboxLive.Subcommands
            .Single(command => command.Name == "achievement-image")
            .Subcommands;
        Assert.Contains(achievementImages, command => command.Name == "upload");
        Assert.Contains(achievementImages, command => command.Name == "get");
        Assert.Contains(achievementImages, command => command.Name == "list");
        Assert.All(achievementImages, command => Assert.True(Program.SupportsTableOutput(command)));
    }

    [Fact]
    public void SandboxList_ExposesOnlyTheAuthorizedSellerOverride()
    {
        var root = Program.BuildRootCommand();
        var sandbox = Assert.Single(root.Subcommands, command => command.Name == "sandbox");
        var list = Assert.Single(sandbox.Subcommands, command => command.Name == "list");

        Assert.Contains(list.Options, option => option.Name == "--seller-id");
        Assert.DoesNotContain(list.Options, option => option.Name == "--user-id");
        Assert.DoesNotContain(list.Options, option => option.Name == "--user-email");
        Assert.True(Program.SupportsTableOutput(list));
    }

    [Fact]
    public async Task SchemaGet_ValidatesAndWarnsAboutGatewayBeforeRequest()
    {
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """["userstats"]""");
        const string variableName = "XBOX_XBOXPARTNER__BASEURL";
        string? originalValue = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, "http://localhost:8080");

            CapturedInvocation result = await InvokeProgramAsync(
                ["xbl", "schema", "get", "--output", "json-compact"],
                handler);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("""["userstats"]""" + Environment.NewLine, result.Stdout);
            Assert.Contains(
                "service endpoint is overridden",
                result.Stderr,
                StringComparison.Ordinal);
            Assert.NotNull(handler.RequestUri);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalValue);
        }
    }

    private static async Task<CapturedInvocation> InvokeProgramAsync(
        string[] args,
        CapturingHttpMessageHandler? handler = null)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Program.XboxPartnerClientFactory = handler is null
            ? null
            : configuration => new XboxPartnerClient(
                configuration,
                new HttpClient(handler),
                () => Task.FromResult<string?>("caller-token"));

        try
        {
            int exitCode = await Program.Main(args);
            return new CapturedInvocation(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static XboxPartnerClient CreateClient(
        HttpMessageHandler handler,
        string timeoutSeconds = "10") =>
        new(
            CreateConfiguration(timeoutSeconds),
            new HttpClient(handler),
            () => Task.FromResult<string?>("caller-token"));

    private static HttpResponseMessage Page(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static IConfiguration CreateConfiguration(string timeoutSeconds = "10") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["XboxPartner:BaseUrl"] = "http://localhost:8080",
                ["XboxPartner:TimeoutSeconds"] = timeoutSeconds,
            })
            .Build();

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly byte[] _responseBody;

        public CapturingHttpMessageHandler(HttpStatusCode statusCode, string responseBody)
            : this(statusCode, System.Text.Encoding.UTF8.GetBytes(responseBody))
        {
        }

        public CapturingHttpMessageHandler(HttpStatusCode statusCode, byte[] responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string? UserAgent { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            UserAgent = request.Headers.UserAgent.ToString();
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode)
            {
                Content = new ByteArrayContent(_responseBody),
            };
        }
    }

    private sealed class RoutingHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> createResponse) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(createResponse(request));
        }
    }

    private sealed class StalledResponseHttpMessageHandler(
        HttpStatusCode statusCode, string? correlationId = null, TimeSpan? retryAfter = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StreamContent(new StalledReadStream()),
            };
            if (correlationId is not null)
            {
                response.Headers.TryAddWithoutValidation(XboxPartnerClient.CorrelationIdHeader, correlationId);
            }

            if (retryAfter is { } delay)
            {
                response.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class StalledReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed record CapturedInvocation(int ExitCode, string Stdout, string Stderr);
}
