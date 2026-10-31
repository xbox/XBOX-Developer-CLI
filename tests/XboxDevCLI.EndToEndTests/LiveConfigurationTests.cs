// Copyright (C) Microsoft Corporation. All rights reserved.

namespace XboxDevCLI.EndToEndTests;

/// <summary>Validates the live job's settings without acquiring a token or calling a service.</summary>
public sealed class LiveConfigurationTests
{
    [LiveFact(LiveService.Authentication)]
    public void EnvironmentSettings_AreComplete()
    {
        LiveSettings.Load(Environment.GetEnvironmentVariable);
    }
}
