// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using XboxDevCLI.Services;
using XboxDevCLI.Telemetry;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class TelemetryInstrumentationTests
{
    [Fact]
    public void RecordCommand_RejectsUnboundedCommandValues()
    {
        KeyValuePair<string, object?>[]? capturedTags = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TelemetryInstrumentation.SourceName &&
                    instrument.Name == "xboxdevcli.command.invocations")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>(
            (_, _, tags, _) =>
            {
                if (tags.ToArray().Any(tag =>
                    tag.Key == "cli.command" &&
                    Equals(tag.Value, "unknown")))
                {
                    capturedTags = tags.ToArray();
                }
            });
        listener.Start();

        TelemetryInstrumentation.RecordCommand(
            "playfab api /Admin/GetTitleData --body {\"Secret\":\"value\"}",
            1,
            TimeSpan.FromMilliseconds(5),
            authMode: "user");

        Assert.NotNull(capturedTags);
        Assert.Contains(capturedTags!, tag =>
            tag.Key == "cli.command" && Equals(tag.Value, "unknown"));
        Assert.DoesNotContain(capturedTags!, tag =>
            tag.Value?.ToString()?.Contains("Secret", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void RecordCommand_AcceptsBoundedCanonicalCommandPath()
    {
        KeyValuePair<string, object?>[]? capturedTags = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TelemetryInstrumentation.SourceName &&
                    instrument.Name == "xboxdevcli.command.invocations")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>(
            (_, _, tags, _) => capturedTags = tags.ToArray());
        listener.Start();

        TelemetryInstrumentation.RecordCommand(
            "playfab-economy-inventory-transaction-list",
            0,
            TimeSpan.FromMilliseconds(5),
            authMode: "githuboidc");

        Assert.NotNull(capturedTags);
        Assert.Contains(capturedTags!, tag =>
            tag.Key == "cli.auth_mode" &&
            Equals(tag.Value, "githuboidc"));
        Assert.Contains(capturedTags!, tag =>
            tag.Key == "cli.command" &&
            Equals(tag.Value, "playfab-economy-inventory-transaction-list"));
        Assert.Contains(capturedTags!, tag =>
            tag.Key == "telemetry.schema_version" &&
            Equals(tag.Value, "2"));
    }

    [Fact]
    public void PlayFabDependency_KnownEndpointUsesGeneratedMetadata()
    {
        Activity? stoppedActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetryInstrumentation.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivity = activity,
        };
        ActivitySource.AddActivityListener(listener);

        using (var operation = TelemetryInstrumentation.StartPlayFabDependency(
            "/Admin/GetTitleData",
            telemetryEnabled: true))
        {
            operation.SetSuccess(200);
        }

        Assert.NotNull(stoppedActivity);
        Assert.Equal("Admin", stoppedActivity!.GetTagItem("rpc.service"));
        Assert.Equal("GetTitleData", stoppedActivity.GetTagItem("rpc.method"));
        Assert.Equal("2xx", stoppedActivity.GetTagItem("http.response.status_code_class"));
    }

    [Fact]
    public void PlayFabDependency_UnknownEndpointDoesNotRecordRawPath()
    {
        Activity? stoppedActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetryInstrumentation.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivity = activity,
        };
        ActivitySource.AddActivityListener(listener);

        using (var operation = TelemetryInstrumentation.StartPlayFabDependency(
            "/Custom/SecretCustomerPath",
            telemetryEnabled: true))
        {
            operation.SetFailure(new HttpRequestException());
        }

        Assert.NotNull(stoppedActivity);
        Assert.Equal("unknown", stoppedActivity!.GetTagItem("rpc.service"));
        Assert.Equal("unknown", stoppedActivity.GetTagItem("rpc.method"));
        Assert.DoesNotContain(stoppedActivity.TagObjects, tag =>
            tag.Value?.ToString()?.Contains("SecretCustomerPath", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void PlayFabDependency_TelemetryDisabled_RecordsNoMetricsOrActivity()
    {
        var playFabMeasurementRecorded = false;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TelemetryInstrumentation.SourceName &&
                    instrument.Name.StartsWith("xboxdevcli.playfab", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>(
            (_, _, _, _) => playFabMeasurementRecorded = true);
        meterListener.SetMeasurementEventCallback<double>(
            (_, _, _, _) => playFabMeasurementRecorded = true);
        meterListener.Start();

        Activity? startedActivity = null;
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetryInstrumentation.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => startedActivity = activity,
        };
        ActivitySource.AddActivityListener(activityListener);

        using (var operation = TelemetryInstrumentation.StartPlayFabDependency(
            "/Admin/GetTitleData",
            telemetryEnabled: false))
        {
            operation.SetSuccess(200);
        }

        Assert.Null(startedActivity);
        Assert.False(playFabMeasurementRecorded);
    }

    /// <summary>
    /// A failure without a response is classified by its cause, and one PlayFab answered by
    /// its status, so a timeout isn't counted as a PlayFab API error.
    /// </summary>
    [Fact]
    public void ClassifyException_UsesTheCauseOnlyWhenPlayFabDidNotAnswer()
    {
        Assert.Equal(
            "cancelled",
            TelemetryInstrumentation.ClassifyException(
                PlayFabApiException.NoResponse("/Admin/SetTitleData", "the request timed out", new TaskCanceledException())));
        Assert.Equal(
            "transport",
            TelemetryInstrumentation.ClassifyException(
                PlayFabApiException.NotSent("/Admin/SetTitleData", new HttpRequestException())));
        Assert.Equal(
            "playfab_api",
            TelemetryInstrumentation.ClassifyException(
                new PlayFabApiException("/Admin/SetTitleData", 500, "InternalServerError", "", "lost", new IOException())));
    }
}
