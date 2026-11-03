// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using Xunit;

namespace XboxDevCLI.E2ETest;

/// <remarks>
/// The test account service allows few calls a minute, so the list that finds the tagged batch
/// runs once for every test that needs the batch's ID.
/// </remarks>
public class TestAccountTests
{
    private static readonly Lazy<Task<CliResult>> TaggedBatches = new(() =>
        Cli.RunForSellerAsync("test-account", "batch", "list", "--mine", "--tag", Settings.BatchKeyword));

    private static E2ESettings Settings => E2ESettings.Current;

    [LiveFact]
    public async Task BatchList_Mine_FindsTheTaggedBatch() => await TaggedBatchIdAsync();

    [LiveFact]
    public async Task BatchList_Limit_ReturnsAtMostThatMany()
    {
        CliResult result = await Cli.RunForSellerAsync("test-account", "batch", "list", "--limit", "4");

        int count = result.Json().Items("batches").Length;
        result.Check(count is >= 1 and <= 4, "Expected one to four batches.");
    }

    [LiveFact]
    public async Task BatchGet_ShowsTheBatchAndItsAccounts()
    {
        long batchId = await TaggedBatchIdAsync();
        CliResult result = await Cli.RunForSellerAsync(
            "test-account", "batch", "get", batchId.ToString(CultureInfo.InvariantCulture));

        // First, and without the command or its output, which would copy the passwords into the
        // run's logs and test results.
        Assert.False(
            result.Stdout.Contains("password", StringComparison.OrdinalIgnoreCase),
            "Expected no passwords in the output, which isn't shown so it can't repeat them.");

        JsonElement output = result.Json();
        JsonElement batch = output.Field("batch");
        result.Check(batch.Number("id") == batchId, $"Expected batch {batchId}.");
        result.Check(batch.Items("keywords").HasString(Settings.BatchKeyword), $"Expected the tag {Settings.BatchKeyword}.");
        result.Check(output.Items("accounts").Length > 0, "Expected the batch's accounts.");
    }

    [LiveFact]
    public async Task JobGet_ShowsTheSucceededCreationJob()
    {
        long batchId = await TaggedBatchIdAsync();
        CliResult result = await Cli.RunForSellerAsync(
            "test-account", "job", "get", Settings.BatchJobId,
            "--batch-id", batchId.ToString(CultureInfo.InvariantCulture));

        JsonElement job = result.Json();
        result.Check(
            job.Is("jobId", Settings.BatchJobId) && job.Number("batchId") == batchId,
            $"Expected job {Settings.BatchJobId} of batch {batchId}.");
        result.Check(job.Is("state", "succeeded"), "Expected a job that succeeded.");
    }

    private static async Task<long> TaggedBatchIdAsync()
    {
        CliResult result = await TaggedBatches.Value;

        JsonElement[] batches = result.Json().Items("batches");
        result.Check(batches.Length == 1, $"Expected exactly one of your batches tagged {Settings.BatchKeyword}.");
        long id = batches[0].Number("id") ?? 0;
        result.Check(id > 0, "Expected the batch's ID.");
        return id;
    }
}
