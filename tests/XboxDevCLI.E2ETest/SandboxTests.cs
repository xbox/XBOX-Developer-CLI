// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Xunit;

namespace XboxDevCLI.E2ETest;

public class SandboxTests
{
    private static E2ESettings Settings => E2ESettings.Current;

    [LiveFact]
    public async Task List_IncludesTheSandboxAndRetail()
    {
        CliResult result = await Cli.RunForSellerAsync("sandbox", "list");

        JsonElement[] sandboxes = result.Json().Items("sandboxes");
        result.Check(sandboxes.Any(sandbox => sandbox.Is("sandboxId", Settings.Sandbox)), $"Expected {Settings.Sandbox}.");
        result.Check(sandboxes.Any(sandbox => sandbox.Is("sandboxId", "RETAIL")), "Expected RETAIL.");
    }

    [LiveFact]
    public async Task Get_ReturnsTheDevelopmentSandbox()
    {
        CliResult result = await Cli.RunForSellerAsync("sandbox", "get", Settings.Sandbox);

        JsonElement sandbox = result.Json();
        result.Check(sandbox.Is("sandboxId", Settings.Sandbox), $"Expected {Settings.Sandbox}.");
        result.Check(
            sandbox.Flag("global") == false && sandbox.Is("sellerId", Settings.SellerId),
            $"Expected a sandbox of seller {Settings.SellerId}.");
    }

    [LiveFact]
    public async Task Get_ReturnsRetail()
    {
        CliResult result = await Cli.RunForSellerAsync("sandbox", "get", "RETAIL");

        JsonElement sandbox = result.Json();
        result.Check(sandbox.Is("sandboxId", "RETAIL") && sandbox.Flag("global") == true, "Expected the global RETAIL sandbox.");
    }

    [LiveFact]
    public async Task Get_UnknownSandbox_FailsAsNotFound()
    {
        CliResult result = await Cli.RunForSellerAsync("sandbox", "get", "ZZZZ.999");

        result.Check(result.ExitCode == 1, "Expected exit code 1.");
        result.Check(string.IsNullOrWhiteSpace(result.Stdout), "Expected nothing on stdout.");
        result.Check(
            result.Stderr.Contains("x-ms-correlation-id:", StringComparison.Ordinal) &&
                result.Stderr.Contains("HTTP 404", StringComparison.Ordinal),
            "Expected the correlation ID and a 404 on stderr.");
    }

    // Verbose output includes the operation and full response, but must never carry the access token.
    [LiveFact]
    public async Task Get_UnknownSandbox_Verbose_ShowsTheDetailButNoToken()
    {
        CliResult result = await Cli.RunForSellerAsync("sandbox", "get", "ZZZZ.999", "--verbose");

        // First, and without the command's output, which would copy a token into the run's logs and test results.
        Assert.False(
            new[] { result.Stdout, result.Stderr }.Any(output =>
                output.Contains("eyJ", StringComparison.Ordinal) || output.Contains("Bearer", StringComparison.OrdinalIgnoreCase)),
            "Expected no access token or Authorization header in the output, which isn't shown so it can't repeat one.");

        result.Check(result.ExitCode == 1, "Expected exit code 1.");
        result.Check(string.IsNullOrWhiteSpace(result.Stdout), "Expected nothing on stdout.");
        result.Check(
            result.Stderr.Contains("[verbose] Reading sandbox ZZZZ.999", StringComparison.Ordinal) &&
                result.Stderr.Contains("\"status\":404", StringComparison.Ordinal),
            "Expected the operation and full response, with its 404, on stderr.");
    }
}
