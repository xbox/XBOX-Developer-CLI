// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Identity.Client;
using XboxDevCLI.Safety;
using XboxDevCLI.Services;
using XboxDevCLI.Telemetry;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class TelemetryInstrumentationTests
{
    private static readonly CommandInvocationEvent Success = new(
        "playfab-mps-build-list",
        "user",
        ExitCode: 0,
        ErrorType: null,
        HttpStatusCode: 200,
        TimeSpan.FromMilliseconds(1234.9),
        ["verbose", "output"],
        ["seller-id", "playfab-title-id"],
        Ci: true);

    [Fact]
    public void ToAttributes_SendsTheSchema3CustomEvent()
    {
        var attributes = Attributes(Success);

        Assert.Equal(TelemetryInstrumentation.CustomEventNameAttribute, attributes.Keys.First());
        Assert.Equal("xbox.cli.command_invocation", attributes["microsoft.custom_event.name"]);
        Assert.Equal("3", attributes["xbox.cli.schema_version"]);
        Assert.Equal("playfab-mps-build-list", attributes["xbox.cli.command"]);
        Assert.Equal("user", attributes["xbox.cli.auth_mode"]);
        Assert.Equal("success", attributes["xbox.cli.outcome"]);
        Assert.Equal("0", attributes["process.exit.code"]);
        Assert.Equal("200", attributes["http.response.status_code"]);
        Assert.Equal("1234", attributes["xbox.cli.duration_ms"]);
        Assert.Equal("output,verbose", attributes["xbox.cli.options"]);
        Assert.Equal("playfab-title-id,seller-id", attributes["xbox.cli.context_keys"]);
        Assert.Equal("true", attributes["xbox.cli.ci"]);
        Assert.Equal(".NET", attributes["process.runtime.name"]);
        Assert.DoesNotContain("error.type", attributes.Keys);
        Assert.All(attributes.Values, value => Assert.IsType<string>(value));
    }

    [Fact]
    public void ToAttributes_SendsOnlyTheFieldsInThePlan()
    {
        Assert.Equal(
            [
                "error.type", "host.arch", "http.response.status_code", "microsoft.custom_event.name",
                "os.type", "os.version", "process.exit.code", "process.runtime.name", "process.runtime.version",
                "service.version", "xbox.cli.auth_mode", "xbox.cli.ci", "xbox.cli.command",
                "xbox.cli.context_keys", "xbox.cli.duration_ms", "xbox.cli.options", "xbox.cli.outcome",
                "xbox.cli.schema_version",
            ],
            Attributes(Success with { ExitCode = 1, ErrorType = "auth" }).Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(0, "auth", "success", null)]
    [InlineData(1, null, "failure", "_OTHER")]
    [InlineData(1, "service_rejected", "failure", "service_rejected")]
    [InlineData(1, "C:\\Users\\dev\\secret.json", "failure", "_OTHER")]
    [InlineData(130, "cancelled", "cancelled", "cancelled")]
    [InlineData(0, "cancelled", "cancelled", "cancelled")]
    public void ToAttributes_DerivesTheOutcomeAndBoundsTheErrorType(
        int exitCode,
        string? errorType,
        string outcome,
        string? sentErrorType)
    {
        var attributes = Attributes(Success with { ExitCode = exitCode, ErrorType = errorType });

        Assert.Equal(outcome, attributes["xbox.cli.outcome"]);
        Assert.Equal(sentErrorType, attributes.GetValueOrDefault("error.type"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(99)]
    [InlineData(600)]
    public void ToAttributes_DropsAMissingOrInvalidStatus(int? status)
    {
        Assert.DoesNotContain("http.response.status_code", Attributes(Success with { HttpStatusCode = status }).Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("game list")]
    [InlineData("dev@nodpublishers.com")]
    [InlineData("C:\\temp\\file")]
    public void ToAttributes_SendsUnknownForAnUnexpectedCommand(string command)
    {
        Assert.Equal("unknown", Attributes(Success with { Command = command })["xbox.cli.command"]);
    }

    [Fact]
    public void ToAttributes_DropsUnexpectedOptionAndContextNames()
    {
        var attributes = Attributes(Success with
        {
            Options = ["body-file", "dev@nodpublishers.com", "Verbose", "verbose"],
            ContextKeys = ["playfab-title-id", "ABC123", "title-id", "scid"],
        });

        Assert.Equal("body-file,verbose", attributes["xbox.cli.options"]);
        Assert.Equal("playfab-title-id,title-id", attributes["xbox.cli.context_keys"]);
    }

    [Fact]
    public void ToAttributes_SendsNoNegativeDuration()
    {
        Assert.Equal("0", Attributes(Success with { Duration = TimeSpan.FromMilliseconds(-5) })["xbox.cli.duration_ms"]);
    }

    public static TheoryData<Exception, string> Exceptions() => new()
    {
        { new OperationCanceledException(), "cancelled" },
        { new TaskCanceledException("timed out", new TimeoutException()), "transport" },
        { new DestructiveOperationNotConfirmedException("/Admin/DeleteTitle"), "not_confirmed" },
        { new PlayFabApiException("/Admin/SetTitleData", 400, "BadRequest", "InvalidParams", "bad", null), "service_rejected" },
        { new PlayFabApiException("/Admin/SetTitleData", 500, "InternalServerError", "", "lost", new IOException()), "service_error" },
        { PlayFabApiException.NoResponse("/Admin/SetTitleData", "the request timed out", new TaskCanceledException("timed out", new TimeoutException())), "transport" },
        { PlayFabApiException.NoResponse("/Admin/SetTitleData", "cancelled", new TaskCanceledException()), "cancelled" },
        { PlayFabApiException.NotSent("/Admin/SetTitleData", new HttpRequestException()), "transport" },
        { new MsalServiceException("invalid_client", "bad client"), "auth" },
        { new HttpRequestException(), "transport" },
        { new IOException(), "io" },
        { new UnauthorizedAccessException(), "io" },
        { new InvalidOperationException(), "_OTHER" },
    };

    [Theory]
    [MemberData(nameof(Exceptions))]
    public void ClassifyException_MapsToTheFixedReasons(Exception exception, string expected)
    {
        Assert.Equal(expected, TelemetryInstrumentation.ClassifyException(exception));
    }

    [Fact]
    public void ClassifyException_MapsServiceStatusAndResponseFailures()
    {
        var uri = new Uri("https://example.invalid/resource");

        Assert.Equal(
            "service_rejected",
            TelemetryInstrumentation.ClassifyException(new XboxPartnerApiException(HttpStatusCode.NotFound, "Not Found", "", uri)));
        Assert.Equal(
            "service_error",
            TelemetryInstrumentation.ClassifyException(new XboxPartnerApiException(HttpStatusCode.BadGateway, "Bad Gateway", "", uri)));
        Assert.Equal(
            "service_rejected",
            TelemetryInstrumentation.ClassifyException(new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden)));
        Assert.Equal(
            "service_error",
            TelemetryInstrumentation.ClassifyException(new XboxPartnerResponseException("{", null, new System.Text.Json.JsonException())));
        Assert.Equal(
            "transport",
            TelemetryInstrumentation.ClassifyException(new XboxPartnerResponseException("", null, new OperationCanceledException())));
        Assert.Equal(
            "transport",
            TelemetryInstrumentation.ClassifyException(new PlayFabApiException(
                "/Admin/GetTitleData", 200, "OK", "", "The response body was cut off.", new IOException("connection reset"))));
        Assert.Equal(
            "service_rejected",
            TelemetryInstrumentation.ClassifyException(new PlayFabApiException(
                "/Admin/GetTitleData", 404, "NotFound", "", "The response body was cut off.", new IOException("connection reset"))));
    }

    [Theory]
    [InlineData(Architecture.X64, "amd64")]
    [InlineData(Architecture.Arm64, "arm64")]
    [InlineData(Architecture.X86, "unknown")]
    public void GetHostArch_UsesTheOpenTelemetryNames(Architecture architecture, string expected)
    {
        Assert.Equal(expected, TelemetryInstrumentation.GetHostArch(architecture));
    }

    [Fact]
    public void GetSuppliedOptions_ReturnsOnlyTheNamesOfSuppliedOptions()
    {
        var root = Program.BuildRootCommand();
        var parseResult = root.Parse(
            ["--playfab-title-id", "ABC123", "playfab", "mps", "build", "list", "-o", "table", "--verbose"],
            Program.NoResponseFileConfig);

        Assert.Equal(["output", "playfab-title-id", "verbose"], TelemetryInstrumentation.GetSuppliedOptions(parseResult));
    }

    [Fact]
    public void Recorder_KeepsTheMostSpecificReason()
    {
        var recorded = TelemetryRecorder.Start();
        try
        {
            TelemetryRecorder.RecordError(new PlayFabApiException("/Admin/GetTitleData", 404, "NotFound", "", "missing", null));
            TelemetryRecorder.RecordError(new InvalidOperationException());
            TelemetryRecorder.RecordErrorIfUnset(TelemetryErrorTypes.LocalTool);
            Assert.Equal("service_rejected", recorded.ErrorType);

            TelemetryRecorder.SetError(TelemetryErrorTypes.Usage);
            TelemetryRecorder.RecordError(TelemetryErrorTypes.Io);
            Assert.Equal("usage", recorded.ErrorType);

            TelemetryRecorder.RecordHttpStatus(404);
            TelemetryRecorder.RecordContextKey("seller-id");
            TelemetryRecorder.RecordContextKey("playfab-title-id");
            Assert.Equal(404, recorded.HttpStatusCode);
            Assert.Equal(["playfab-title-id", "seller-id"], recorded.ContextKeys);
        }
        finally
        {
            TelemetryRecorder.Stop();
        }

        Assert.Null(TelemetryRecorder.Current);
        TelemetryRecorder.RecordHttpStatus(500);
    }

    private static Dictionary<string, object?> Attributes(CommandInvocationEvent invocation) =>
        TelemetryInstrumentation.ToAttributes(invocation).ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
}