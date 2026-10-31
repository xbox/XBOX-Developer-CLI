// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class RequestBodySizeLimitTests
{
    [Theory]
    [InlineData("inline")]
    [InlineData("file")]
    [InlineData("stdin")]
    public async Task RawApi_Verbose_OmitsRequestBodyButPreservesDiagnosticsAndOutput(string source)
    {
        const string body = """{"Keys":["nod-private-request-marker"]}""";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var tempFile = source == "file" ? Path.GetTempFileName() : null;

        try
        {
            if (tempFile is not null)
            {
                await File.WriteAllTextAsync(tempFile, body);
            }

            string[] bodyArguments = source == "inline"
                ? ["--body", body]
                : ["--body-file", tempFile ?? "-"];
            var result = await InvokeAsync(
                ["playfab", "api", "/Admin/GetTitleData", "--verbose", .. bodyArguments],
                source == "stdin" ? input : null);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(body, result.Handler.LastRequestBody);
            Assert.DoesNotContain("nod-private-request-marker", result.Stderr);
            Assert.Contains("[verbose] POST /Admin/GetTitleData", result.Stderr);
            Assert.Contains("[verbose] Request succeeded.", result.Stderr);
            Assert.Equal("{\"Data\":{\"MyKey\":\"my-value\"}}" + Environment.NewLine, result.Stdout);
        }
        finally
        {
            if (tempFile is not null)
            {
                File.Delete(tempFile);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("{\"Keys\":[\"caf\u00e9 \U0001f3ae\"]}\r\n")]
    [InlineData("{\"first\":1}\n{\"second\":2}\n")]
    public async Task RawApi_Stdin_SendsTheWholeBodyUnchanged(string body)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var result = await InvokeAsync(
            ["playfab", "api", "/Admin/GetTitleData", "--body-file", "-"],
            input);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.Equal(body, result.Handler.LastRequestBody);
        Assert.Equal("{\"Data\":{\"MyKey\":\"my-value\"}}" + Environment.NewLine, result.Stdout);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task RawApi_WithoutStdinOption_DoesNotReadStdin(string? body)
    {
        string[] bodyArguments = body is null ? [] : ["--body", body];

        var result = await InvokeAsync(
            ["playfab", "api", "/Admin/GetTitleData", .. bodyArguments]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.Equal(body ?? "{}", result.Handler.LastRequestBody);
    }

    [Fact]
    public async Task RawApi_ConflictingBodySources_FailsBeforeReadingStdin()
    {
        var result = await InvokeAsync(
            ["playfab", "api", "/Admin/GetTitleData", "--body", "{}", "--body-file", "-"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--body and --body-file cannot both be specified", result.Stderr);
        Assert.Empty(result.Stdout);
        Assert.Null(result.Handler.LastRequest);
    }

    [Fact]
    public async Task RawApi_StdinOverLimit_FailsBeforeSendingARequest()
    {
        using var input = new MemoryStream(new byte[1025]);

        var result = await InvokeAsync(
            ["playfab", "api", "/Admin/GetTitleData", "--body-file", "-"],
            input);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--body-file - exceeds the configured maximum size of 1024 bytes", result.Stderr);
        Assert.Empty(result.Stdout);
        Assert.Null(result.Handler.LastRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawApi_DestructiveStdin_RequiresTheConfirmOption(bool confirmed)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        string[] confirmation = confirmed ? ["--confirm"] : [];

        var result = await InvokeAsync(
            ["playfab", "api", "/Admin/DeleteTitle", "--body-file", "-", .. confirmation],
            confirmed ? input : null);

        Assert.Equal(confirmed ? 0 : 1, result.ExitCode);
        if (confirmed)
        {
            Assert.Empty(result.Stderr);
            Assert.Equal("{}", result.Handler.LastRequestBody);
        }
        else
        {
            Assert.Contains("destructive and requires --confirm when --body-file - reads stdin", result.Stderr);
            Assert.Empty(result.Stdout);
            Assert.Null(result.Handler.LastRequest);
        }
    }

    [Fact]
    public async Task Mps_Stdin_UsesTheSharedBodyReader()
    {
        const string body = "{\"BuildId\":\"ABC123\"}";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var result = await InvokeAsync(["playfab", "mps", "build", "get", "--body-file", "-"], input);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.Equal(body, result.Handler.LastRequestBody);
        Assert.Equal("/MultiplayerServer/GetBuild", result.Handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ReadConsoleInputWithLimitAsync_SelectsUtf8BeforeOpeningAndRestoresInputEncoding()
    {
        var originalInputEncoding = Console.InputEncoding;
        var originalOutputEncoding = Console.OutputEncoding;
        const string body = "caf\u00e9 \U0001f3ae\r\n";
        var bytes = Encoding.UTF8.GetBytes(body);
        using var input = new MemoryStream(bytes);
        try
        {
            Console.InputEncoding = Encoding.ASCII;

            var result = await Program.ReadConsoleInputWithLimitAsync(
                () =>
                {
                    Assert.Equal(Encoding.UTF8.CodePage, Console.InputEncoding.CodePage);
                    return input;
                },
                bytes.Length,
                CancellationToken.None);

            Assert.Equal(body, result);
            Assert.Equal(Encoding.ASCII.CodePage, Console.InputEncoding.CodePage);
            Assert.Equal(originalOutputEncoding.CodePage, Console.OutputEncoding.CodePage);
            Assert.False(input.CanRead);
        }
        finally
        {
            Console.InputEncoding = originalInputEncoding;
        }
    }

    [Theory]
    [InlineData("open", typeof(IOException))]
    [InlineData("size", typeof(InvalidOperationException))]
    [InlineData("decode", typeof(DecoderFallbackException))]
    public async Task ReadConsoleInputWithLimitAsync_RestoresInputEncodingOnFailure(string failure, Type errorType)
    {
        var originalInputEncoding = Console.InputEncoding;
        var originalOutputEncoding = Console.OutputEncoding;
        using var input = new MemoryStream(failure == "size" ? new byte[9] : [0xff]);
        try
        {
            Console.InputEncoding = Encoding.ASCII;

            var error = await Record.ExceptionAsync(() => Program.ReadConsoleInputWithLimitAsync(
                () =>
                {
                    Assert.Equal(Encoding.UTF8.CodePage, Console.InputEncoding.CodePage);
                    return failure == "open" ? throw new IOException("Cannot open console input.") : input;
                },
                8,
                CancellationToken.None));

            Assert.IsType(errorType, error);
            Assert.Equal(Encoding.ASCII.CodePage, Console.InputEncoding.CodePage);
            Assert.Equal(originalOutputEncoding.CodePage, Console.OutputEncoding.CodePage);
        }
        finally
        {
            Console.InputEncoding = originalInputEncoding;
        }
    }

    [Fact]
    public async Task ReadConsoleInputWithLimitAsync_RestoresInputEncodingWhenPendingReadIsCanceled()
    {
        var originalInputEncoding = Console.InputEncoding;
        var originalOutputEncoding = Console.OutputEncoding;
        using var input = new WaitingInputStream();
        using var cancellation = new CancellationTokenSource();
        try
        {
            Console.InputEncoding = Encoding.ASCII;
            var read = Program.ReadConsoleInputWithLimitAsync(() => input, 8, cancellation.Token);
            await input.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Encoding.UTF8.CodePage, Console.InputEncoding.CodePage);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => read.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(Encoding.ASCII.CodePage, Console.InputEncoding.CodePage);
            Assert.Equal(originalOutputEncoding.CodePage, Console.OutputEncoding.CodePage);
            Assert.False(input.CanRead);
        }
        finally
        {
            Console.InputEncoding = originalInputEncoding;
        }
    }

    [Fact]
    public async Task ReadStandardInputWithLimitAsync_DecodesSplitUtf8AtTheExactByteLimit()
    {
        const string body = "{\"Key\":\"caf\u00e9 \U0001f3ae\"}\r\n";
        var bytes = Encoding.UTF8.GetBytes(body);
        using var input = new SingleByteInputStream(bytes);

        var result = await Program.ReadStandardInputWithLimitAsync(input, bytes.Length, CancellationToken.None);

        Assert.Equal(body, result);
    }

    [Fact]
    public async Task ReadStandardInputWithLimitAsync_DropsALeadingByteOrderMarkOnly()
    {
        const string body = "{\"Key\":\"\ufeff\"}\r\n";
        using var input = new MemoryStream([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(body)]);

        var result = await Program.ReadStandardInputWithLimitAsync(input, 100, CancellationToken.None);

        Assert.Equal(body, result);
    }

    [Fact]
    public async Task ReadStandardInputWithLimitAsync_StopsAfterTheFirstExcessByte()
    {
        using var input = new MemoryStream(new byte[20000]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Program.ReadStandardInputWithLimitAsync(input, 8, CancellationToken.None));

        Assert.Contains("--body-file - exceeds the configured maximum size", error.Message);
        Assert.Equal(9, input.Position);
    }

    [Fact]
    public async Task ReadStandardInputWithLimitAsync_RejectsInvalidUtf8RatherThanReplacingIt()
    {
        using var input = new MemoryStream([0xff]);

        await Assert.ThrowsAsync<DecoderFallbackException>(
            () => Program.ReadStandardInputWithLimitAsync(input, 8, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadStandardInputWithLimitAsync_CancelsEvenIfTheStreamIgnoresCancellation(bool alreadyCanceled)
    {
        using var input = new WaitingInputStream();
        using var cancellation = new CancellationTokenSource();
        if (alreadyCanceled)
        {
            cancellation.Cancel();
        }

        var read = Program.ReadStandardInputWithLimitAsync(input, 8, cancellation.Token);
        if (!alreadyCanceled)
        {
            await input.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(!alreadyCanceled, input.ReadStarted.Task.IsCompleted);
    }

    [Fact]
    public void EnsureRequestBodyWithinLimit_WhenBodyWithinLimit_DoesNotThrow()
    {
        Program.EnsureRequestBodyWithinLimit("--body", bodySizeBytes: 16, maxRequestBodyBytes: 16);
    }

    [Fact]
    public void EnsureRequestBodyWithinLimit_WhenBodyExceedsLimit_ThrowsHelpfulError()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Program.EnsureRequestBodyWithinLimit("--body", bodySizeBytes: 17, maxRequestBodyBytes: 16));

        Assert.Contains("--body exceeds the configured maximum size", ex.Message);
        Assert.Contains("PlayFab:MaxRequestBodyBytes", ex.Message);
    }

    [Fact]
    public async Task ReadBodyFileWithLimitAsync_WhenFileWithinLimit_ReturnsContent()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            const string content = "{\"k\":\"v\"}";
            await File.WriteAllTextAsync(tempFile, content);

            var result = await Program.ReadBodyFileWithLimitAsync(
                tempFile,
                maxRequestBodyBytes: 1024,
                CancellationToken.None);

            Assert.Equal(content, result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ReadBodyFileWithLimitAsync_WhenFileExceedsLimit_ThrowsHelpfulError()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, new string('a', 32));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Program.ReadBodyFileWithLimitAsync(
                    tempFile,
                    maxRequestBodyBytes: 8,
                    CancellationToken.None));

            Assert.Contains("--body-file exceeds the configured maximum size", ex.Message);
            Assert.Contains("PlayFab:MaxRequestBodyBytes", ex.Message);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetMaxRequestBodyBytes_InvalidConfig_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayFab:MaxRequestBodyBytes"] = "0",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => Program.GetMaxRequestBodyBytes(configuration));

        Assert.Contains("PlayFab:MaxRequestBodyBytes", ex.Message);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr, MockHttpMessageHandler Handler)> InvokeAsync(
        string[] arguments,
        Stream? input = null)
    {
        const string limitVariable = "XBOX_PLAYFAB__MAXREQUESTBODYBYTES";
        var originalLimit = Environment.GetEnvironmentVariable(limitVariable);
        var originalInputFactory = Program.StandardInputFactory;
        var originalClientFactory = Program.ApiClientFactory;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":{\"Data\":{\"MyKey\":\"my-value\"}}}"),
        });
        using var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PlayFab:TitleId"] = "ABC123",
            ["Telemetry:Enabled"] = "false",
            ["AuditLog:Enabled"] = "false",
        }).Build();

        try
        {
            Environment.SetEnvironmentVariable(limitVariable, "1024");
            Program.StandardInputFactory = () => input
                ?? throw new InvalidOperationException("Stdin must not be read for this invocation.");
            Program.ApiClientFactory = _ => new PlayFabApiClient(
                configuration,
                new FakeAuthenticationService(FakeAuthenticationService.CreateToken()),
                httpClient);
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var result = Program.BuildRootCommand().Parse(
                ["--playfab-title-id", "ABC123", "--disable-telemetry", .. arguments,
                    "--output", "json-compact", "--data-only"],
                Program.NoResponseFileConfig);
            var exitCode = await result.InvokeAsync();
            return (exitCode, stdout.ToString(), stderr.ToString(), handler);
        }
        finally
        {
            Environment.SetEnvironmentVariable(limitVariable, originalLimit);
            Program.StandardInputFactory = originalInputFactory;
            Program.ApiClientFactory = originalClientFactory;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class SingleByteInputStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class WaitingInputStream : MemoryStream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            return new ValueTask<int>(_read.Task);
        }

        protected override void Dispose(bool disposing)
        {
            _read.TrySetResult(0);
            base.Dispose(disposing);
        }
    }
}
