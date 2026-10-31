// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections;
using System.Text.Json;
using Xunit;

namespace XboxDevCLI.EndToEndTests;

/// <summary>
/// A fact that runs only in an opted-in live run whose profile enables <see cref="Service"/>.
/// </summary>
/// <remarks>
/// xUnit v2 has no runtime skip, so the decision is made when the attribute is constructed.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute(LiveService service)
    {
        Service = service;
        Skip = LiveSettings.GetSkipReason(service, Environment.GetEnvironmentVariable);
    }

    public LiveService Service { get; }
}

[CollectionDefinition(Name)]
public sealed class LiveCollection : ICollectionFixture<LiveCliFixture>
{
    public const string Name = "Live CLI";
}

/// <summary>
/// Loads the live settings and prepares the CLI copy once, on first use.
/// </summary>
/// <remarks>
/// Lazy so that a run without <c>E2E_GITHUB_ENVIRONMENT</c>, where every live test is skipped, does no
/// work, and so that configuration errors fail each live test rather than the collection.
/// </remarks>
public sealed class LiveCliFixture : IDisposable
{
    private readonly Lazy<(LiveSettings Settings, CliRunner Runner)> _state = new(() =>
    {
        var settings = LiveSettings.Load(Environment.GetEnvironmentVariable);
        return (settings, CliRunner.Create(settings, Environment.GetEnvironmentVariables()));
    });

    internal LiveSettings Settings => _state.Value.Settings;

    internal CliRunner Cli => _state.Value.Runner;

    /// <summary>Runs the CLI, requires a zero exit code, and parses stdout as JSON.</summary>
    internal async Task<JsonDocument> RunJsonAsync(params string[] arguments)
    {
        var result = await Cli.RunAsync(arguments);
        var command = "xbox " + string.Join(' ', arguments);
        Assert.True(result.ExitCode == 0, $"{command} failed: {result.Describe()}");
        try
        {
            return JsonDocument.Parse(result.StandardOutput);
        }
        catch (JsonException)
        {
            Assert.Fail($"{command} did not write JSON to stdout: {result.Describe()}");
            throw;
        }
    }

    /// <summary>Writes a request body into the run's private working directory.</summary>
    internal string WriteBodyFile(string name, object body)
    {
        var path = Path.Combine(Cli.WorkingDirectory, name);
        File.WriteAllText(path, JsonSerializer.Serialize(body));
        return path;
    }

    public void Dispose()
    {
        if (_state.IsValueCreated)
        {
            _state.Value.Runner.Dispose();
        }
    }
}
