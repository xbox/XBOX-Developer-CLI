// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.Services;
using XboxDevCLI.Telemetry;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// <c>playfab mps certificate upload</c> and <c>playfab mps secret upload</c> carry a PFX password
/// or a secret value in their request body, so they take it only from <c>--body-file</c>.
/// </summary>
[Collection("ConsoleRedirect")]
public class MpsSecretBodyTests
{
    private const string Marker = "nod-private-secret-marker";

    private static readonly Dictionary<string, (string ApiPath, string Body)> Commands = new()
    {
        ["certificate"] = (
            "/MultiplayerServer/UploadCertificate",
            $$$"""{"GameCertificate":{"Name":"nod-arena-tls","Base64EncodedValue":"TU9DSw==","Password":"{{{Marker}}}"}}"""),
        ["secret"] = (
            "/MultiplayerServer/UploadSecret",
            $$$"""{"GameSecret":{"Name":"nod-arena-api-key","Value":"{{{Marker}}}"}}"""),
    };

    public static TheoryData<string, string> InlineBodyForms()
    {
        var data = new TheoryData<string, string>();
        foreach (var noun in Commands.Keys)
        {
            data.Add(noun, "separate");
            data.Add(noun, "equals");
            data.Add(noun, "bare");
            data.Add(noun, "with-body-file");
            data.Add(noun, "split");
            data.Add(noun, "repeated");
        }

        return data;
    }

    public static TheoryData<string, string, HttpStatusCode> BodyFileSources()
    {
        var data = new TheoryData<string, string, HttpStatusCode>();
        foreach (var noun in Commands.Keys)
        {
            foreach (var source in new[] { "file", "stdin" })
            {
                data.Add(noun, source, HttpStatusCode.OK);
                data.Add(noun, source, HttpStatusCode.BadRequest);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(InlineBodyForms))]
    public async Task InlineBody_IsRefusedWithoutRepeatingIt(string noun, string form)
    {
        var body = Commands[noun].Body;
        string[] arguments = form switch
        {
            "separate" => ["--body", body],
            "equals" => [$"--body={body}"],
            "bare" => ["--body"],
            "with-body-file" => ["--body", body, "--body-file", "-"],
            // As the shell passes JSON it split on spaces, or expanded braces in.
            "split" => ["--body", "{\"Value\":", Marker, "}", "--verbose"],
            _ => ["--body", "{}", "--body", body],
        };

        var result = await InvokeAsync(noun, arguments, stdin: null, HttpStatusCode.OK);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(MpsCommandFactory.InlineSecretBodyRefusal, result.Stderr, StringComparison.Ordinal);
        Assert.Contains("--body-file <path>", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("--body-file -", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.Null(result.Handler.LastRequest);
    }

    [Theory]
    [InlineData("certificate")]
    [InlineData("secret")]
    public async Task MissingBodyFile_IsAParseErrorAndSendsNothing(string noun)
    {
        var result = await InvokeAsync(noun, [], stdin: null, HttpStatusCode.OK);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--body-file", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("required", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Handler.LastRequest);
    }

    [Theory]
    [MemberData(nameof(BodyFileSources))]
    public async Task BodyFile_SendsTheBodyWithoutWritingTheSecretAnywhere(
        string noun,
        string source,
        HttpStatusCode status)
    {
        var (apiPath, body) = Commands[noun];
        var tempFile = source == "file" ? Path.GetTempFileName() : null;
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var originalOptOut = Environment.GetEnvironmentVariable(TelemetryOptions.OptOutEnvironmentVariable);
        var telemetry = new List<string>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == TelemetryInstrumentation.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (telemetry)
                {
                    telemetry.Add(activity.DisplayName);
                    telemetry.AddRange(activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"));
                }
            },
        };
        using var metrics = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TelemetryInstrumentation.SourceName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        void Measured<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            var captured = tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}");
            lock (telemetry)
            {
                telemetry.AddRange(captured);
            }
        }

        metrics.SetMeasurementEventCallback<long>(Measured);
        metrics.SetMeasurementEventCallback<double>(Measured);

        try
        {
            if (tempFile is not null)
            {
                await File.WriteAllTextAsync(tempFile, body);
            }

            // The client turns its own telemetry off under the opt-out, which would leave nothing
            // to inspect.
            Environment.SetEnvironmentVariable(TelemetryOptions.OptOutEnvironmentVariable, null);
            ActivitySource.AddActivityListener(activities);
            metrics.Start();

            var result = await InvokeAsync(
                noun,
                ["--verbose", "--body-file", tempFile ?? "-"],
                source == "stdin" ? stdin : null,
                status,
                telemetryEnabled: true);

            // Program.Main records the command by its path in the tree, never its arguments.
            TelemetryInstrumentation.RecordCommand(
                CommandPathResolver.ResolveTelemetryIdentity(result.Root, result.Command),
                result.ExitCode,
                TimeSpan.Zero,
                authMode: "user");

            Assert.Equal(status == HttpStatusCode.OK ? 0 : 1, result.ExitCode);
            Assert.Equal(body, result.Handler.LastRequestBody);
            Assert.Contains($"[verbose] POST {apiPath}", result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(Marker, result.Stdout + result.Stderr, StringComparison.Ordinal);

            string[] captured;
            lock (telemetry)
            {
                captured = [.. telemetry];
            }

            Assert.Contains($"rpc.method={apiPath.Split('/')[^1]}", captured);
            Assert.Contains($"cli.command=playfab-mps-{noun}-upload", captured);
            Assert.DoesNotContain(captured, value => value.Contains(Marker, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TelemetryOptions.OptOutEnvironmentVariable, originalOptOut);
            if (tempFile is not null)
            {
                File.Delete(tempFile);
            }
        }
    }

    [Theory]
    [InlineData("certificate", "GameCertificate (Name, Base64EncodedValue, Password)")]
    [InlineData("secret", "GameSecret (Name, Value, ExpirationDate)")]
    public async Task Help_NamesTheFieldsAndShowsOnlyBodyFile(string noun, string fields)
    {
        var originalOut = Console.Out;
        using var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var exit = await Program.BuildRootCommand()
                .Parse(["playfab", "mps", noun, "upload", "--help"], Program.NoResponseFileConfig)
                .InvokeAsync();

            Assert.Equal(0, exit);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // The Help writer wraps option descriptions, so compare on collapsed whitespace.
        var help = string.Join(' ', stdout.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("--body-file <path>", help, StringComparison.Ordinal);
        Assert.Contains(fields, help, StringComparison.Ordinal);
        Assert.Contains($"xbox playfab mps {noun} upload --body-file ./{noun}.json", help, StringComparison.Ordinal);
        Assert.DoesNotContain("--body <", help, StringComparison.Ordinal);
    }

    private static async Task<InvocationResult> InvokeAsync(
        string noun,
        string[] arguments,
        Stream? stdin,
        HttpStatusCode status,
        bool telemetryEnabled = false)
    {
        var originalInputFactory = Program.StandardInputFactory;
        var originalClientFactory = Program.ApiClientFactory;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(status)
        {
            Content = new StringContent(status == HttpStatusCode.OK
                ? """{"code":200,"status":"OK","data":{}}"""
                : """{"code":400,"status":"BadRequest","error":"InvalidRequest","errorMessage":"Invalid input parameters"}"""),
        });
        using var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PlayFab:TitleId"] = "ABC123",
            ["Telemetry:Enabled"] = telemetryEnabled.ToString(),
            ["AuditLog:Enabled"] = "false",
        }).Build();

        try
        {
            Program.StandardInputFactory = () => stdin
                ?? throw new InvalidOperationException("Stdin must not be read for this invocation.");
            Program.ApiClientFactory = _ => new PlayFabApiClient(
                configuration,
                new FakeAuthenticationService(FakeAuthenticationService.CreateToken()),
                httpClient);
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var root = Program.BuildRootCommand();
            var parseResult = root.Parse(
                ["--playfab-title-id", "ABC123", "playfab", "mps", noun, "upload", .. arguments],
                Program.NoResponseFileConfig);
            var exitCode = await parseResult.InvokeAsync();
            return new InvocationResult(
                exitCode,
                stdout.ToString(),
                stderr.ToString(),
                handler,
                root,
                parseResult.CommandResult.Command);
        }
        finally
        {
            Program.StandardInputFactory = originalInputFactory;
            Program.ApiClientFactory = originalClientFactory;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record InvocationResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        MockHttpMessageHandler Handler,
        RootCommand Root,
        Command Command);
}
