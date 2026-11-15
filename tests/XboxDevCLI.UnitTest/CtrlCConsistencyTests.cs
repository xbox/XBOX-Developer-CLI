// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Ctrl+C during a read stops every command the same way: exit 130, and nothing written. Ctrl+C
/// before a write sends its change says nothing changed and exits 1.
/// </summary>
[Collection("ConsoleRedirect")]
public class CtrlCConsistencyTests
{
    public static TheoryData<string[]> Reads => new()
    {
        { new[] { "sandbox", "list" } },
        { new[] { "sandbox", "get", "ABCDEF.1" } },
        { new[] { "game", "list" } },
        { new[] { "game", "job", "get", "11111111-2222-3333-4444-555555555555" } },
        { new[] { "audience", "list" } },
        { new[] { "audience", "get", "1152921504606846976" } },
        { new[] { "test-account", "batch", "list" } },
        { new[] { "xbl", "schema", "get", "--type", "userstats", "--version", "2" } },
        { new[] { "xbl", "config", "status", "--scid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "--sandbox-id", "XDKS.1" } },
        { new[] { "xbl", "achievement-image", "list", "--scid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" } },
        { new[] { "xbl", "achievement-image", "get", "--scid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "--asset-id", "11111111-2222-3333-4444-555555555555" } },
        { new[] { "package", "list", "--store-id", "9N0000000000" } },
        { new[] { "playtest", "list", "--store-id", "9N0000000000" } },
        { new[] { "seller", "list" } },
        { new[] { "xbl", "config", "list", "--scid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" } },
        { new[] { "xbl", "config", "download", "--scid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "--sandbox-id", "XDKS.1", "--view", "draft", "--dest", "{dir}" } },
        { new[] { "xbl", "achievement-image", "download", "--scid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "--all", "--dest", "{dir}" } },
    };

    /// <summary>
    /// Each write, the sign-in that cancels it (the reads before the write each sign in first), and
    /// what it says. {file}, {png}, and {dir} are replaced with files and a folder in a temporary folder.
    /// </summary>
    public static TheoryData<string[], int, string> WritesCanceledBeforeSending => new()
    {
        { new[] { "sandbox", "create", "TESTSB.42", "-y" }, 1, "so no sandbox was created." },
        {
            new[] { "playtest", "create", "--store-id", "9N0000000000", "--name", "Beta", "--start", "2027-01-01", "--end", "2027-01-15", "-y" },
            1, "so no playtest was created."
        },
        {
            new[] { "playtest", "update", "11111111-2222-3333-4444-555555555555", "--store-id", "9N0000000000", "--name", "Beta", "-y" },
            1, "so playtest 11111111-2222-3333-4444-555555555555 wasn't changed."
        },
        {
            new[] { "playtest", "publish", "11111111-2222-3333-4444-555555555555", "--store-id", "9N0000000000", "-y" },
            3, "so playtest 11111111-2222-3333-4444-555555555555 wasn't published."
        },
        {
            new[] { "playtest", "delete", "11111111-2222-3333-4444-555555555555", "--store-id", "9N0000000000", "-y" },
            2, "so playtest 11111111-2222-3333-4444-555555555555 wasn't deleted."
        },
        {
            new[] { "xbl", "config", "save", "--scid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "--sandbox-id", "XDKS.1", "--file", "{file}", "-y" },
            1, "so no documents were saved."
        },
        {
            new[] { "xbl", "config", "publish", "--scid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "--sandbox-id", "XDKS.1", "-y" },
            1, "so the publish to XDKS.1 didn't start."
        },
        {
            new[] { "xbl", "achievement-image", "upload", "--scid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "--file", "{png}", "-y" },
            2, "so no image was registered."
        },
    };

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task CancelingTheRequestExitsQuietlyWith130(string[] arguments)
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            arguments = await PrepareAsync(arguments, directory);
            using var cancellation = new CancellationTokenSource();
            var handler = new CountingHandler(() =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });

            var result = await InvokeAsync(handler, CreateAuth(), arguments, cancellation.Token);

            Assert.Equal(130, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Empty(TestEnvironment.WithoutEndpointWarnings(result.Stderr));
            Assert.Equal(1, handler.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("save", "Check with: xbox --tenant-id lucernepublishing.com xbl config download --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 --sandbox-id XDKS.1 --view draft --dest <directory>")]
    [InlineData("publish", "Check with: xbox --tenant-id lucernepublishing.com xbl config status --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 --sandbox-id XDKS.1")]
    public async Task CancelingAConfigWriteAfterItIsSentKeepsTheSignInOptionsInTheCheck(string action, string check)
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string[] arguments = await PrepareAsync(
                ["--tenant-id", "lucernepublishing.com", "xbl", "config", action,
                    "--scid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "--sandbox-id", "XDKS.1",
                    .. action == "save" ? new[] { "--file", "{file}" } : [], "-y"],
                directory);
            using var cancellation = new CancellationTokenSource();
            var handler = new CountingHandler(() =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });

            var result = await InvokeAsync(handler, CreateAuth(), arguments, cancellation.Token);

            Assert.Equal(3, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Contains(check, result.Stderr);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(WritesCanceledBeforeSending))]
    public async Task CancelingBeforeAWriteIsSentSaysNothingChanged(string[] arguments, int canceledSignIn, string message)
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            arguments = await PrepareAsync(arguments, directory);
            using var cancellation = new CancellationTokenSource();
            var auth = CreateAuth();
            int signIns = 0;
            auth.Setup(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    if (++signIns < canceledSignIn)
                    {
                        return Task.FromResult<AuthenticationResult?>(Token());
                    }

                    cancellation.Cancel();
                    return Task.FromCanceled<AuthenticationResult?>(cancellation.Token);
                });
            var handler = new CountingHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath switch
                {
                    var path when path.EndsWith("/readiness", StringComparison.Ordinal) => """{"ready":true,"checks":[]}""",
                    var path when path.EndsWith("/upload/initialize", StringComparison.Ordinal) =>
                        """{"uploadUri":"https://nodpublishers.blob.core.windows.net/images/badge.png?sig=secret","xfusId":"11111111-2222-3333-4444-555555555555","uploadToken":"token","expiresAt":"2099-01-01T00:00:00Z"}""",
                    _ => """{"details":{"playtestId":"11111111-2222-3333-4444-555555555555","name":"Beta","status":"Draft"},"etag":"\"v1\""}""",
                }),
            });

            var result = await InvokeAsync(handler, auth, arguments, cancellation.Token);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Contains($"Canceled before the {(arguments[2] == "upload" ? "registration " : "")}request was sent, {message}", result.Stderr);
            Assert.Equal(canceledSignIn - 1, handler.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Writes the files the arguments name into <paramref name="directory"/>, and fills them in.</summary>
    private static async Task<string[]> PrepareAsync(string[] arguments, string directory)
    {
        string file = Path.Combine(directory, "nsal.xml");
        string png = Path.Combine(directory, "badge.png");
        await File.WriteAllTextAsync(file, "<nsal />");
        await File.WriteAllBytesAsync(png, AchievementImageCommandTests.CreatePngHeader(1920, 1080));
        return [.. arguments.Select(argument => argument
            .Replace("{file}", file)
            .Replace("{png}", png)
            .Replace("{dir}", Path.Combine(directory, "out")))];
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(
        HttpMessageHandler handler,
        Mock<AuthenticationService> auth,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient(handler);
        XboxPartnerClient CreateClient(IConfiguration configuration) => new(configuration, httpClient, auth.Object);

        var root = Program.BuildRootCommand(
            sellerCommandFactory: () => SellerCommandFactory.Create(CreateClient),
            gameCommandFactory: () => GameCommandFactory.Create(CreateClient),
            sandboxCommandFactory: () => SandboxCommandFactory.Create(CreateClient),
            audienceCommandFactory: () => AudienceCommandFactory.Create(CreateClient),
            packageCommandFactory: () => PackageCommandFactory.Create(CreateClient),
            playtestCommandFactory: () => PlaytestCommandFactory.Create(CreateClient));
        string[] variables = ["XBOX_XBOXPARTNER__BASEURL", "XBOX_SELLER_ID"];
        var previous = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("XBOX_XBOXPARTNER__BASEURL", "https://gateway.example");
            Environment.SetEnvironmentVariable("XBOX_SELLER_ID", "1234567");
            Program.XboxPartnerClientFactory = CreateClient;
            Program.AchievementImageUploadClientFactory = () => new HttpClient(new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)));
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var parseResult = await Program.ResolveContextAsync(root.Parse(arguments, Program.NoResponseFileConfig), arguments);
            Assert.NotNull(parseResult);
            int exitCode = await parseResult.InvokeAsync(
                new InvocationConfiguration { EnableDefaultExceptionHandler = false }, cancellationToken);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            Program.XboxPartnerClientFactory = null;
            Program.AchievementImageUploadClientFactory = null;
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            TestEnvironment.Reset();
        }
    }

    private static Mock<AuthenticationService> CreateAuth()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AzureAd:ClientId"] = "test-client",
                ["AzureAd:TenantId"] = "test-tenant",
                ["AzureAd:Scopes"] = "api://test/.default",
            }).Build();
        var account = Mock.Of<IAccount>(
            account => account.HomeAccountId == new AccountId("id", "user", "test-tenant"));
        var auth = new Mock<AuthenticationService>(configuration);
        auth.Setup(service => service.GetCachedAccountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(account);
        auth.Setup(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Token());
        return auth;
    }

    private static AuthenticationResult Token()
    {
        var account = Mock.Of<IAccount>(
            account => account.HomeAccountId == new AccountId("id", "user", "test-tenant"));
        return new AuthenticationResult(
            "access-token", false, "user", DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow.AddHours(1), "test-tenant", account,
            "id-token", ["api://test/.default"], Guid.NewGuid());
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public CountingHandler(Func<HttpResponseMessage> respond)
            : this(_ => respond())
        {
        }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(respond(request));
        }
    }
}
