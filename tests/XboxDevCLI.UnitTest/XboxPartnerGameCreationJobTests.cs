// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class XboxPartnerGameCreationJobTests
{
    [Theory]
    [InlineData("notStarted", "pending", false, false)]
    [InlineData("running", "pending", false, false)]
    [InlineData("futureStatus", "succeeded", false, false)]
    [InlineData(null, null, false, false)]
    [InlineData("completed", "succeeded", true, true)]
    [InlineData("completed", "failed", true, false)]
    [InlineData("completed", "pending", true, false)]
    [InlineData("completed", null, true, false)]
    [InlineData("completed", "futureResult", true, false)]
    public void Completion_DependsOnStatusAndResult(
        string? status, string? result, bool completed, bool succeeded)
    {
        var job = new XboxPartnerGameCreationJob { JobId = "job-1", Status = status, Result = result };

        Assert.Equal(completed, job.Completed);
        Assert.Equal(succeeded, job.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData(""","game":null,"errors":null""")]
    [InlineData(""","errors":[]""")]
    public void Deserialize_AcceptsOptionalGameAndErrors(string fields)
    {
        var job = JsonSerializer.Deserialize<XboxPartnerGameCreationJob>(
            $$"""{"jobId":"job-1","status":"completed","result":"succeeded"{{fields}}}""");

        Assert.NotNull(job);
        Assert.Equal("job-1", job.JobId);
        Assert.Null(job.Game);
        Assert.True(job.Succeeded);
        Assert.True(job.Errors is null or { Length: 0 });
    }

    [Fact]
    public void Deserialize_MapsGameSummaryAndNestedErrors()
    {
        const string json = """
            {"jobId":"job-1","status":"completed","result":"succeeded",
             "game":{"productId":"000123","storeId":"9N0000000000","xboxTitleId":"FFFFFFFF",
                     "SCID":"00000000-0000-0000-0000-0000FFFFFFFF","primaryDevelopmentSandbox":"TESTSB.42",
                     "deviceFamilies":["xbox","desktop"],
                     "name":"Game","packageIdentityName":"Identity","packageIdentityPublisher":"Publisher",
                     "packageFamilyName":"Family","msaAppId":"App"},
             "errors":[{"code":"serviceUnavailable","message":"Enablement incomplete",
                        "details":[{"code":"nested","message":null,"details":null}]}]}
            """;

        var job = JsonSerializer.Deserialize<XboxPartnerGameCreationJob>(json)!;

        Assert.True(job.Succeeded);
        Assert.Equal("000123", job.Game!.ProductId);
        Assert.Equal("9N0000000000", job.Game.StoreId);
        Assert.Equal("FFFFFFFF", job.Game.XboxTitleId);
        Assert.Equal("00000000-0000-0000-0000-0000FFFFFFFF", job.Game.Scid);
        Assert.Equal("TESTSB.42", job.Game.PrimaryDevelopmentSandbox);
        Assert.NotNull(job.Game.DeviceFamilies);
        Assert.Equal(["xbox", "desktop"], job.Game.DeviceFamilies);
        Assert.Equal("Game", job.Game.Name);
        Assert.Equal("Identity", job.Game.PackageIdentityName);
        Assert.Equal("Publisher", job.Game.PackageIdentityPublisher);
        Assert.Equal("Family", job.Game.PackageFamilyName);
        Assert.Equal("App", job.Game.MsaAppId);
        var error = Assert.Single(job.Errors!);
        Assert.Equal("serviceUnavailable", error.Code);
        Assert.Equal("Enablement incomplete", error.Message);
        var detail = Assert.Single(error.Details!);
        Assert.Equal("nested", detail.Code);
        Assert.Null(detail.Message);
        Assert.Null(detail.Details);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"jobId":42}""")]
    [InlineData("""{"jobId":"job-1","status":false}""")]
    [InlineData("""{"jobId":"job-1","result":42}""")]
    [InlineData("""{"jobId":"job-1","errors":{}}""")]
    [InlineData("""{"jobId":"job-1","errors":[42]}""")]
    [InlineData("""{"jobId":"job-1","game":"invalid"}""")]
    [InlineData("""{"jobId":"job-1","game":{"xboxTitleId":-1}}""")]
    [InlineData("""{"jobId":"job-1","game":{"xboxTitleId":4294967296}}""")]
    [InlineData("""{"jobId":"job-1","game":{"SCID":42}}""")]
    [InlineData("""{"jobId":"job-1","game":{"primaryDevelopmentSandbox":42}}""")]
    [InlineData("""{"jobId":"job-1","game":{"deviceFamilies":"xbox"}}""")]
    [InlineData("""{"jobId":"job-1","game":{"deviceFamilies":{}}}""")]
    [InlineData("""{"jobId":"job-1","game":{"deviceFamilies":[42]}}""")]
    public void Deserialize_RejectsInvalidFieldTypes(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<XboxPartnerGameCreationJob>(json));

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"xboxTitleId":null,"SCID":null,"primaryDevelopmentSandbox":null,"deviceFamilies":null}""")]
    public void Deserialize_AcceptsNullOrMissingSummaryFields(string summary)
    {
        var job = JsonSerializer.Deserialize<XboxPartnerGameCreationJob>(
            $$"""{"jobId":"job-1","game":{{summary}}}""")!;

        Assert.NotNull(job.Game);
        Assert.Null(job.Game.XboxTitleId);
        Assert.Null(job.Game.Scid);
        Assert.Null(job.Game.PrimaryDevelopmentSandbox);
        Assert.Null(job.Game.DeviceFamilies);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""["xbox","desktop"]""")]
    [InlineData("""["FutureDevice","Desktop","xbox","FutureDevice"]""")]
    public void Deserialize_PreservesDeviceFamilyNamesOrderAndNullOrEmptyArrays(string families)
    {
        var job = JsonSerializer.Deserialize<XboxPartnerGameCreationJob>(
            """{"jobId":"job-1","game":{"deviceFamilies":""" + families + "}}")!;

        Assert.NotNull(job.Game);
        Assert.Equal(JsonSerializer.Deserialize<string[]>(families), job.Game.DeviceFamilies);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(job.Game));
        using var expected = JsonDocument.Parse(families);
        Assert.True(JsonElement.DeepEquals(
            expected.RootElement, serialized.RootElement.GetProperty("deviceFamilies")));
    }

    [Fact]
    public void Serialize_ExcludesTransportMetadata()
    {
        var job = new XboxPartnerGameCreationJob
        {
            JobId = "job-1",
            RawResponse = "original body",
            RetryAfter = TimeSpan.FromSeconds(9),
            CorrelationId = "correlation-1",
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(job));

        Assert.Equal("job-1", json.RootElement.GetProperty("jobId").GetString());
        Assert.Equal(5, json.RootElement.EnumerateObject().Count());
    }
}
