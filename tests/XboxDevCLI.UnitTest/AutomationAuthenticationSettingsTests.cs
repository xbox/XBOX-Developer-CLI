// Copyright (C) Microsoft Corporation. All rights reserved.

using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class AutomationAuthenticationSettingsTests
{
    private const string Tenant = "15fc7b3e-912c-4c49-ad3d-734293d93a23";
    private const string Client = "3778efb3-5411-47ab-a790-b5db0eed39a6";
    private const string P = AutomationAuthenticationSettings.Prefix;

    [Fact]
    public void Load_WithNoSettings_DefaultsToUserMode()
    {
        var settings = Load(new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin",
            // Ambient GitHub inputs alone never select a mode.
            [AutomationAuthenticationSettings.GitHubRequestUrlVariable] = "https://example/token",
            [AutomationAuthenticationSettings.GitHubRequestTokenVariable] = "t",
        });

        Assert.Equal(AuthenticationMode.User, settings.Mode);
        Assert.False(settings.IsAutomation);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("USER")]
    [InlineData("  ")]
    public void Load_WithUserOrBlankMode_IsUserMode(string mode)
    {
        Assert.Equal(AuthenticationMode.User, Load(new() { [P + "MODE"] = mode }).Mode);
    }

    [Fact]
    public void Load_ClientSecret_ReadsTenantClientAndFile()
    {
        var settings = Load(new()
        {
            [P + "MODE"] = "ClientSecret",
            [P + "TENANTID"] = Tenant,
            [P + "CLIENTID"] = Client,
            [P + "CLIENTSECRET__FILE"] = "/tmp/secret",
        });

        Assert.Equal(AuthenticationMode.ClientSecret, settings.Mode);
        Assert.Equal("clientsecret", settings.ModeName);
        Assert.Equal(Tenant, settings.TenantId);
        Assert.Equal(Client, settings.ClientId);
        Assert.Equal("/tmp/secret", settings.CredentialFile);
    }

    [Fact]
    public void Load_Certificate_PasswordFileIsOptional()
    {
        var settings = Load(new()
        {
            [P + "MODE"] = "certificate",
            [P + "TENANTID"] = "contoso.onmicrosoft.com",
            [P + "CLIENTID"] = Client,
            [P + "CERTIFICATE__PATH"] = "/tmp/cert.pfx",
        });

        Assert.Equal("/tmp/cert.pfx", settings.CredentialFile);
        Assert.Null(settings.CertificatePasswordFile);
        Assert.Equal("contoso.onmicrosoft.com", settings.TenantId);
    }

    [Fact]
    public void Load_FederatedToken_ReadsFile()
    {
        var settings = Load(new()
        {
            [P + "MODE"] = "federatedtoken",
            [P + "TENANTID"] = Tenant,
            [P + "CLIENTID"] = Client,
            [P + "FEDERATEDTOKEN__FILE"] = "/tmp/oidc",
        });

        Assert.Equal(AuthenticationMode.FederatedToken, settings.Mode);
        Assert.Equal("/tmp/oidc", settings.CredentialFile);
    }

    [Fact]
    public void Load_GitHubOidc_RequiresGitHubRequestInputs()
    {
        var environment = new Dictionary<string, string>
        {
            [P + "MODE"] = "githuboidc",
            [P + "TENANTID"] = Tenant,
            [P + "CLIENTID"] = Client,
        };

        var error = Assert.Throws<AuthenticationConfigurationException>(() => Load(environment));
        Assert.Contains("id-token: write", error.Message, StringComparison.Ordinal);

        environment[AutomationAuthenticationSettings.GitHubRequestUrlVariable] = "https://pipelines.example/token?api-version=2.0";
        environment[AutomationAuthenticationSettings.GitHubRequestTokenVariable] = "request-token";
        var settings = Load(environment);

        Assert.Equal("https://pipelines.example/token?api-version=2.0", settings.GitHubRequestUrl);
        Assert.Equal("request-token", settings.GitHubRequestToken);
    }

    /// <summary>
    /// The environment snapshot matches every name case-insensitively, as <c>Load</c> does, so
    /// lower-case GitHub inputs aren't dropped before they're read.
    /// </summary>
    [Fact]
    public void Capture_MatchesNamesCaseInsensitively()
    {
        var snapshot = AutomationAuthenticationSettings.Capture(new System.Collections.Hashtable
        {
            ["xbox_authentication__mode"] = "githuboidc",
            ["xbox_authentication__tenantid"] = Tenant,
            ["xbox_authentication__clientid"] = Client,
            ["actions_id_token_request_url"] = "https://pipelines.example/token",
            ["Actions_Id_Token_Request_Token"] = "request-token",
            ["PATH"] = "/usr/bin",
        });

        var settings = AutomationAuthenticationSettings.Load(snapshot, _ => true);

        Assert.DoesNotContain("PATH", snapshot.Variables.Keys);
        Assert.Equal("https://pipelines.example/token", settings.GitHubRequestUrl);
        Assert.Equal("request-token", settings.GitHubRequestToken);
    }

    [Theory]
    [InlineData(P + "MODE", "azurecli", "managedidentity", false)]
    [InlineData(P + "MODE", "azurecli", "managedidentity", true)]
    [InlineData(P + "CLIENTSECRET__FILE", "/tmp/first-secret", "/tmp/second-secret", false)]
    [InlineData(P + "CLIENTSECRET__FILE", "/tmp/first-secret", "/tmp/second-secret", true)]
    [InlineData(AutomationAuthenticationSettings.GitHubRequestUrlVariable, "https://first.example/token", "https://second.example/token", false)]
    [InlineData(AutomationAuthenticationSettings.GitHubRequestUrlVariable, "https://first.example/token", "https://second.example/token", true)]
    [InlineData(AutomationAuthenticationSettings.GitHubRequestTokenVariable, "first-request-token", "second-request-token", false)]
    [InlineData(AutomationAuthenticationSettings.GitHubRequestTokenVariable, "first-request-token", "second-request-token", true)]
    public void Capture_PreservesConflictingCaseVariantsForValidation(
        string name, string firstValue, string secondValue, bool reverse)
    {
        var environment = new System.Collections.Specialized.OrderedDictionary(StringComparer.Ordinal);
        environment.Add(reverse ? name.ToLowerInvariant() : name, firstValue);
        environment.Add(reverse ? name : name.ToLowerInvariant(), secondValue);

        var snapshot = AutomationAuthenticationSettings.Capture(environment);

        Assert.Equal("invalid", snapshot.SelectedModeName);
        Assert.True(snapshot.IsAutomationSelected);
        var error = Assert.Throws<AuthenticationConfigurationException>(() =>
            AutomationAuthenticationSettings.Load(snapshot, _ => throw new InvalidOperationException("Must not read files.")));

        Assert.Contains(name, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("differ only by case", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(firstValue, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secondValue, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(P + "MODE")]
    [InlineData(P + "CLIENTSECRET__FILE")]
    [InlineData(AutomationAuthenticationSettings.GitHubRequestUrlVariable)]
    [InlineData(AutomationAuthenticationSettings.GitHubRequestTokenVariable)]
    public void Capture_AllowsIdenticalCaseVariants(string name)
    {
        var snapshot = AutomationAuthenticationSettings.Capture(new System.Collections.Hashtable
        {
            [name] = "same-value",
            [name.ToLowerInvariant()] = "same-value",
        });

        Assert.Single(snapshot.Variables);
        Assert.Null(snapshot.ConflictingVariable);
        Assert.Equal("same-value", snapshot.Variables[name]);
    }

    [Fact]
    public void Load_GitHubOidc_RejectsNonHttpsRequestUrl()
    {
        var error = Assert.Throws<AuthenticationConfigurationException>(() => Load(new()
        {
            [P + "MODE"] = "githuboidc",
            [P + "TENANTID"] = Tenant,
            [P + "CLIENTID"] = Client,
            [AutomationAuthenticationSettings.GitHubRequestUrlVariable] = "http://pipelines.example/token",
            [AutomationAuthenticationSettings.GitHubRequestTokenVariable] = "request-token",
        }));

        Assert.Contains("https", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AzureCli_HasNoSettings()
    {
        var settings = Load(new() { [P + "MODE"] = "azurecli" });

        Assert.Equal(AuthenticationMode.AzureCli, settings.Mode);
        Assert.Null(settings.TenantId);
        Assert.Null(settings.ClientId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Client)]
    public void Load_ManagedIdentity_ClientIdIsOptional(string? clientId)
    {
        var environment = new Dictionary<string, string> { [P + "MODE"] = "managedidentity" };
        if (clientId is not null)
        {
            environment[P + "CLIENTID"] = clientId;
        }

        Assert.Equal(clientId, Load(environment).ClientId);
    }

    [Fact]
    public void Load_UnknownMode_ListsValidModes()
    {
        var error = Assert.Throws<AuthenticationConfigurationException>(
            () => Load(new() { [P + "MODE"] = "password" }));

        Assert.Contains("githuboidc", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("password", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingRequiredSetting_NamesIt()
    {
        var error = Assert.Throws<AuthenticationConfigurationException>(() => Load(new()
        {
            [P + "MODE"] = "clientsecret",
            [P + "TENANTID"] = Tenant,
            [P + "CLIENTSECRET__FILE"] = "/tmp/secret",
        }));

        Assert.Contains(P + "CLIENTID is required", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("user")]
    public void Load_AutomationSettingWithoutAutomationMode_FailsWithModeGuidance(string? mode)
    {
        var environment = new Dictionary<string, string> { [P + "CERTIFICATE__PATH"] = "/tmp/cert.pfx" };
        if (mode is not null)
        {
            environment[P + "MODE"] = mode;
        }

        var error = Assert.Throws<AuthenticationConfigurationException>(() => Load(environment));

        Assert.Contains($"Set {P}MODE=certificate", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/cert.pfx", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_InactiveModeSetting_Fails()
    {
        var error = Assert.Throws<AuthenticationConfigurationException>(() => Load(new()
        {
            [P + "MODE"] = "azurecli",
            [P + "CLIENTSECRET__FILE"] = "/tmp/secret",
        }));

        Assert.Contains("doesn't apply when", error.Message, StringComparison.Ordinal);
        Assert.Contains($"{P}MODE=clientsecret", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TENANTID")]
    [InlineData("CLIENTID")]
    public void Load_AzureCli_RejectsIdentifiers(string setting)
    {
        var error = Assert.Throws<AuthenticationConfigurationException>(() => Load(new()
        {
            [P + "MODE"] = "azurecli",
            [P + setting] = Client,
        }));

        Assert.Contains($"{P}{setting} doesn't apply when {P}MODE is 'azurecli'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ManagedIdentity_RejectsTenantId()
    {
        var error = Assert.Throws<AuthenticationConfigurationException>(() => Load(new()
        {
            [P + "MODE"] = "managedidentity",
            [P + "TENANTID"] = Tenant,
        }));

        Assert.Contains("one of: clientsecret, certificate, federatedtoken, githuboidc", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_SharedIdentifierWithoutMode_ListsModes()
    {
        var error = Assert.Throws<AuthenticationConfigurationException>(() => Load(new() { [P + "CLIENTID"] = Client }));

        Assert.Contains($"Set {P}MODE to one of:", error.Message, StringComparison.Ordinal);
        Assert.Contains("managedidentity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MatchesVariableNamesCaseInsensitively()
    {
        var settings = AutomationAuthenticationSettings.Load(
            AutomationAuthenticationSettings.Capture(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["xbox_authentication__mode"] = "clientsecret",
                ["Xbox_Authentication__TenantId"] = Tenant,
                ["XBOX_AUTHENTICATION__clientid"] = Client,
                ["xbox_authentication__clientsecret__file"] = "/tmp/secret",
            }),
            fileExists: _ => true);

        Assert.Equal(AuthenticationMode.ClientSecret, settings.Mode);
        Assert.Equal(Client, settings.ClientId);
    }

    [Fact]
    public void Load_OldModeSpecificIdentifier_IsUnrecognized()
    {
        var error = Assert.Throws<AuthenticationConfigurationException>(() => Load(new()
        {
            [P + "MODE"] = "githuboidc",
            [P + "GITHUBOIDC__CLIENTID"] = Client,
        }));

        Assert.Contains("not a recognized authentication setting", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_UnknownSetting_Fails()
    {
        var error = Assert.Throws<AuthenticationConfigurationException>(() => Load(new()
        {
            [P + "MODE"] = "azurecli",
            [P + "AZURECLI__TENANTID"] = Tenant,
        }));

        Assert.Contains("not a recognized authentication setting", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_EmptyInactiveSetting_IsIgnored()
    {
        // CI systems often expand an undefined variable to an empty string.
        var settings = Load(new()
        {
            [P + "MODE"] = "azurecli",
            [P + "CLIENTSECRET__FILE"] = string.Empty,
        });

        Assert.Equal(AuthenticationMode.AzureCli, settings.Mode);
    }

    [Theory]
    [InlineData("common")]
    [InlineData("organizations")]
    [InlineData("not a tenant")]
    public void Load_InvalidTenant_Fails(string tenant)
    {
        var error = Assert.Throws<AuthenticationConfigurationException>(() => Load(new()
        {
            [P + "MODE"] = "githuboidc",
            [P + "TENANTID"] = tenant,
            [P + "CLIENTID"] = Client,
        }));

        Assert.Contains(P + "TENANTID must be", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_NonGuidClientId_Fails()
    {
        var error = Assert.Throws<AuthenticationConfigurationException>(() => Load(new()
        {
            [P + "MODE"] = "managedidentity",
            [P + "CLIENTID"] = "my-identity",
        }));

        Assert.Contains(P + "CLIENTID must be", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingCredentialFile_FailsWithoutPath()
    {
        var error = Assert.Throws<AuthenticationConfigurationException>(() =>
            AutomationAuthenticationSettings.Load(
                AutomationAuthenticationSettings.Capture(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [P + "MODE"] = "federatedtoken",
                    [P + "TENANTID"] = Tenant,
                    [P + "CLIENTID"] = Client,
                    [P + "FEDERATEDTOKEN__FILE"] = "/secret/location/token",
                }),
                fileExists: _ => false));

        Assert.Contains(P + "FEDERATEDTOKEN__FILE", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/secret/location", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "user")]
    [InlineData("  ", "user")]
    [InlineData(" USER ", "user")]
    [InlineData("ClientSecret", "clientsecret")]
    [InlineData("Certificate", "certificate")]
    [InlineData("FederatedToken", "federatedtoken")]
    [InlineData("GitHubOidc", "githuboidc")]
    [InlineData(" AzureCli ", "azurecli")]
    [InlineData("ManagedIdentity", "managedidentity")]
    [InlineData("bogus", "invalid")]
    public void GetSelectedModeName_IsBounded(string? mode, string expected)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (mode is not null)
        {
            environment[P + "MODE"] = mode;
        }

        var snapshot = AutomationAuthenticationSettings.Capture(environment);

        Assert.Equal(expected, snapshot.SelectedModeName);
        Assert.Equal(expected != "user", snapshot.IsAutomationSelected);
    }

    [Fact]
    public void GetSelectedModeName_MatchesModeVariableCaseInsensitively()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["xbox_authentication__mode"] = "azurecli",
        };

        Assert.Equal("azurecli", AutomationAuthenticationSettings.Capture(environment).SelectedModeName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("user")]
    public void ModeInspection_OrphanSettingsNeverSelectUser(string? mode)
    {
        var environment = new Dictionary<string, string>
        {
            [P + "CLIENTSECRET__FILE"] = "private-path",
        };
        if (mode is not null)
        {
            environment[P + "MODE"] = mode;
        }

        var snapshot = AutomationAuthenticationSettings.Capture(environment);

        Assert.Equal("invalid", snapshot.SelectedModeName);
        Assert.True(snapshot.IsAutomationSelected);
        Assert.Throws<AuthenticationConfigurationException>(() =>
            AutomationAuthenticationSettings.Load(snapshot, _ => true));
    }

    [Fact]
    public void ModeInspection_AmbientGitHubAndEmptySettingsDoNotSelectAutomation()
    {
        var snapshot = AutomationAuthenticationSettings.Capture(new Dictionary<string, string>
        {
            [AutomationAuthenticationSettings.GitHubRequestTokenVariable] = "ambient-token",
            [P + "CLIENTID"] = " ",
        });

        Assert.Equal("user", snapshot.SelectedModeName);
        Assert.Equal(AuthenticationMode.User, AutomationAuthenticationSettings.Load(snapshot, _ => true).Mode);
    }

    private static AutomationAuthenticationSettings Load(Dictionary<string, string> environment) =>
        AutomationAuthenticationSettings.Load(
            AutomationAuthenticationSettings.Capture(environment),
            fileExists: _ => true);
}
