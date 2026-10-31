// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public sealed class ContextStoreTests : IDisposable
{
    private const string Gateway = "https://gateway.example";
    private const string GatewayScope = "tenant:user@gateway.example";
    private const string PlayFabEndpoint = "https://{0}.playfabapi.com";
    private static readonly SignedInIdentity Identity = new("tenant", "user");
    private static readonly DateTime LongAgo = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"xbox-context-store-{Guid.NewGuid():N}");

    private bool _directoryUnwritable;

    private string FilePath => Path.Combine(_directory, ContextStore.FileName);

    private string LockPath => FilePath + ".lock";

    public void Dispose()
    {
        MakeStateWritable();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateAndGet_ScopesValuesByIdentityAndEndpointAuthority()
    {
        var store = new ContextStore(FilePath);

        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "1111111")]);

        // Only the authority scopes a value, so case, a trailing slash, and a path don't matter.
        Assert.Equal(
            "1111111",
            await store.GetAsync(Identity, "seller-id", "https://GATEWAY.example/xboxpartner/"));
        Assert.Null(await store.GetAsync(
            new SignedInIdentity("tenant", "other-user"), "seller-id", Gateway));
        Assert.Null(await store.GetAsync(
            new SignedInIdentity("other-tenant", "user"), "seller-id", Gateway));
        Assert.Null(await store.GetAsync(Identity, "seller-id", "https://other.example"));
        Assert.Null(await store.GetAsync(Identity, "seller-id", "https://gateway.example:8443"));
        Assert.Null(await store.GetAsync(Identity, "playfab-title-id", Gateway));
    }

    [Theory]
    [InlineData("https://gateway.example", GatewayScope)]
    [InlineData("https://Gateway.Example/xboxpartner/", GatewayScope)]
    [InlineData("http://localhost:54848", "tenant:user@localhost:54848")]
    [InlineData("https://xboxdevcli.playfabapi.com", "tenant:user@xboxdevcli.playfabapi.com")]
    [InlineData("https://{0}.playfabapi.com", "tenant:user@title.playfabapi.com")]
    [InlineData("NOT-A-URL", "tenant:user@not-a-url")]
    public void CreateScope_IsTenantAndUserAtTheLowercasedAuthority(string endpoint, string expected)
    {
        Assert.Equal(expected, ContextStore.CreateScope(Identity, endpoint));
    }

    /// <summary>
    /// A PlayFab template can put the title in the host, which is not a parseable URL until the
    /// placeholder is filled in. The value must still be scoped to that host, not to the raw
    /// template text or to the default endpoint.
    /// </summary>
    [Fact]
    public async Task UpdateAndGet_TemplatedPlayFabEndpointIsScopedToItsHost()
    {
        const string template = "https://{0}.playfabapi.com";
        var store = new ContextStore(FilePath);

        await store.UpdateAsync(Identity, [new ContextChange("playfab-title-id", template, "ABC12")]);

        Assert.Equal("ABC12", await store.GetAsync(Identity, "playfab-title-id", template));
        Assert.Null(await store.GetAsync(
            Identity, "playfab-title-id", "https://xboxdevcli.playfabapi.com"));
        var contexts = (await ReadFileAsync()).GetProperty("contexts");
        Assert.True(contexts.TryGetProperty("tenant:user@title.playfabapi.com", out _));
    }

    [Fact]
    public async Task UpdateAsync_WritesAVersionedIndentedCamelCaseFile()
    {
        await new ContextStore(FilePath).UpdateAsync(
            Identity,
            [new ContextChange("seller-id", Gateway, "1234567")]);

        var text = await File.ReadAllTextAsync(FilePath);
        Assert.Contains('\n', text);
        var root = await ReadFileAsync();
        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.Equal(
            "1234567",
            root.GetProperty("contexts").GetProperty(GatewayScope).GetProperty("seller-id").GetString());

        // With no named profile, the file has the shape a version-1 CLI wrote.
        Assert.False(root.TryGetProperty("profiles", out _));
    }

    [Fact]
    public async Task UpdateAsync_LeavesNoStagingFileAfterASuccessfulWrite()
    {
        var store = new ContextStore(FilePath);

        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "1111111")]);
        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "2222222")]);

        // The lock file stays: it's only a lock while a writer holds it open.
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.Equal(
            [FilePath, LockPath],
            Directory.GetFileSystemEntries(_directory).Order(StringComparer.Ordinal));
        Assert.Equal("2222222", await store.GetAsync(Identity, "seller-id", Gateway));
    }

    /// <summary>
    /// A write is staged in its own file and renamed into place. When the rename keeps failing
    /// (here because a directory sits where the context file goes), it is retried briefly, then
    /// the error is reported as it is, and the staged file is removed rather than left behind for
    /// every failed attempt.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_FailedRenameLeavesNoStagingFile()
    {
        Directory.CreateDirectory(FilePath);

        var exception = await Record.ExceptionAsync(
            () => new ContextStore(FilePath).UpdateAsync(
                Identity,
                [new ContextChange("seller-id", Gateway, "1111111")]));

        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            $"Expected the rename to fail, but got {exception?.GetType().Name ?? "no exception"}.");
        Assert.DoesNotContain("not writable", exception!.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task UpdateAsync_SavesSeveralValuesInOneCall()
    {
        var store = new ContextStore(FilePath);

        await store.UpdateAsync(
            Identity,
            [
                new ContextChange("seller-id", Gateway, "1111111"),
                new ContextChange("playfab-title-id", "https://{0}.playfabapi.com", "ABC12"),
            ]);

        Assert.Equal("1111111", await store.GetAsync(Identity, "seller-id", Gateway));
        Assert.Equal(
            "ABC12",
            await store.GetAsync(Identity, "playfab-title-id", "https://{0}.playfabapi.com"));
    }

    /// <summary>
    /// A newer CLI may save keys this one doesn't know. Rewriting the file must not drop them, or
    /// running an older build once would silently lose the newer build's context.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_PreservesUnknownKeysAndOtherScopes()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            FilePath,
            $$"""
            {
              "version": 1,
              "contexts": {
                "{{GatewayScope}}": { "seller-id": "1", "future-key": "kept" },
                "tenant:user@other.example": { "seller-id": "2" }
              }
            }
            """);
        var store = new ContextStore(FilePath);

        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "3")]);

        var contexts = (await ReadFileAsync()).GetProperty("contexts");
        var gateway = contexts.GetProperty(GatewayScope);
        Assert.Equal("3", gateway.GetProperty("seller-id").GetString());
        Assert.Equal("kept", gateway.GetProperty("future-key").GetString());
        Assert.Equal("2", contexts.GetProperty("tenant:user@other.example").GetProperty("seller-id").GetString());

        // A scope that still holds an unknown key is not empty, so removing the last known key
        // keeps it.
        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, null)]);

        gateway = (await ReadFileAsync()).GetProperty("contexts").GetProperty(GatewayScope);
        Assert.False(gateway.TryGetProperty("seller-id", out _));
        Assert.Equal("kept", gateway.GetProperty("future-key").GetString());
    }

    [Fact]
    public async Task UpdateAsync_RemovingAKeyKeepsTheRestAndPrunesAnEmptiedScope()
    {
        var store = new ContextStore(FilePath);
        await store.UpdateAsync(
            Identity,
            [
                new ContextChange("seller-id", Gateway, "1111111"),
                new ContextChange("playfab-title-id", Gateway, "ABC12"),
                new ContextChange("seller-id", "https://other.example", "2222222"),
            ]);

        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, null)]);

        Assert.Null(await store.GetAsync(Identity, "seller-id", Gateway));
        Assert.Equal("ABC12", await store.GetAsync(Identity, "playfab-title-id", Gateway));
        Assert.Equal(
            "2222222",
            await store.GetAsync(Identity, "seller-id", "https://other.example"));

        await store.UpdateAsync(Identity, [new ContextChange("playfab-title-id", Gateway, null)]);

        var contexts = (await ReadFileAsync()).GetProperty("contexts");
        Assert.False(contexts.TryGetProperty(GatewayScope, out _));
        Assert.True(contexts.TryGetProperty("tenant:user@other.example", out _));
    }

    /// <summary>
    /// <c>context unset</c> of a key that isn't saved must succeed without a writable state
    /// directory, and without leaving a context file behind where there was none.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_RemovingFromAMissingFileWritesNothing()
    {
        var store = new ContextStore(FilePath);

        await store.UpdateAsync(
            Identity,
            [
                new ContextChange("seller-id", Gateway, null),
                new ContextChange("playfab-title-id", "https://{0}.playfabapi.com", null),
            ]);

        Assert.False(File.Exists(FilePath));
        Assert.False(Directory.Exists(_directory));
        Assert.Null(await store.GetAsync(Identity, "seller-id", Gateway));
    }

    [Fact]
    public async Task UpdateAsync_ChangingNothingDoesNotWrite()
    {
        var store = new ContextStore(FilePath);
        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "1111111")]);

        // A write replaces the file with a freshly staged one, so it can't keep this timestamp.
        File.SetLastWriteTimeUtc(FilePath, LongAgo);

        await store.UpdateAsync(
            Identity,
            [
                new ContextChange("seller-id", Gateway, "1111111"),
                new ContextChange("playfab-title-id", Gateway, null),
                new ContextChange("seller-id", "https://other.example", null),
            ]);

        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(FilePath));
        Assert.Equal("1111111", await store.GetAsync(Identity, "seller-id", Gateway));
    }

    [Fact]
    public async Task GetAsync_MissingFileReturnsNullWithoutCreatingIt()
    {
        var store = new ContextStore(FilePath);

        Assert.Null(await store.GetAsync(Identity, "seller-id", Gateway));
        Assert.False(Directory.Exists(_directory));
    }

    /// <summary>
    /// A syntactically valid file can still deserialize to a null map, which would otherwise be
    /// dereferenced as a null reference instead of reporting the file.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"version":1,"contexts":null}""")]
    [InlineData("""{"version":1,"contexts":{"tenant:user@gateway.example":null}}""")]
    [InlineData("""{"version":1,"contexts":{"tenant:user@gateway.example":"1111111"}}""")]
    [InlineData("""{"version":1,"contexts":{"tenant:user@gateway.example":{"seller-id":1234567}}}""")]
    [InlineData("""{"version":2,"contexts":{},"profiles":{"test":null}}""")]
    [InlineData("""{"version":2,"contexts":{},"profiles":{"test":{"tenant:user@gateway.example":null}}}""")]
    public async Task GetAsync_MalformedFileReportsTheFile(string content)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, content);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new ContextStore(FilePath).GetAsync(Identity, "seller-id", Gateway));

        Assert.Contains(FilePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("context set", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The message says to fix or delete the file, so a write must not overwrite it first.</summary>
    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"version":1,"contexts":{"tenant:user@gateway.example":null}}""")]
    public async Task Writes_MalformedFileIsReportedAndLeftAlone(string content)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, content);
        var store = new ContextStore(FilePath);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "1111111")]));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RemoveAllAsync(Identity));

        Assert.Equal(content, await File.ReadAllTextAsync(FilePath));
    }

    /// <summary>
    /// <c>context unset --all</c> forgets the account everywhere: on every endpoint, including keys
    /// this version of the CLI doesn't know. Other accounts' context stays, even one whose user ID
    /// starts with this one's.
    /// </summary>
    [Fact]
    public async Task RemoveAllAsync_RemovesEveryScopeForTheIdentityAndNothingElse()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            FilePath,
            """
            {
              "version": 1,
              "contexts": {
                "tenant:user@gateway.example": { "seller-id": "1111111", "future-key": "x" },
                "tenant:user@title.playfabapi.com": { "playfab-title-id": "ABC12" },
                "tenant:user2@gateway.example": { "seller-id": "2222222" },
                "other-tenant:user@gateway.example": { "seller-id": "3333333" }
              }
            }
            """);

        Assert.True(await new ContextStore(FilePath).RemoveAllAsync(Identity));

        var contexts = (await ReadFileAsync()).GetProperty("contexts");
        Assert.Equal(
            ["other-tenant:user@gateway.example", "tenant:user2@gateway.example"],
            contexts.EnumerateObject().Select(scope => scope.Name).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task RemoveAllAsync_RemovingNothingWritesNothing()
    {
        var store = new ContextStore(FilePath);
        var otherUser = new SignedInIdentity("tenant", "other-user");

        Assert.False(await store.RemoveAllAsync(Identity));

        Assert.False(Directory.Exists(_directory));

        await store.UpdateAsync(otherUser, [new ContextChange("seller-id", Gateway, "1111111")]);
        File.SetLastWriteTimeUtc(FilePath, LongAgo);

        Assert.False(await store.RemoveAllAsync(Identity));

        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(FilePath));
        Assert.Equal("1111111", await store.GetAsync(otherUser, "seller-id", Gateway));
    }

    /// <summary>
    /// A named profile is a separate set of values: saving to one leaves the default profile and
    /// every other profile as they were, and a key it lacks isn't filled in from the default one.
    /// The default profile stays at the top of the file, where a version-1 CLI reads it.
    /// </summary>
    [Fact]
    public async Task NamedProfiles_AreIsolatedFromTheDefaultAndFromEachOther()
    {
        var store = new ContextStore(FilePath);

        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "1111111")]);
        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "2222222")], "test");
        await store.UpdateAsync(
            Identity,
            [new ContextChange("playfab-title-id", PlayFabEndpoint, "PROD1")],
            "prod");

        Assert.Equal("1111111", await store.GetAsync(Identity, "seller-id", Gateway));
        Assert.Equal("2222222", await store.GetAsync(Identity, "seller-id", Gateway, "test"));
        Assert.Null(await store.GetAsync(Identity, "seller-id", Gateway, "prod"));
        Assert.Equal("PROD1", await store.GetAsync(Identity, "playfab-title-id", PlayFabEndpoint, "prod"));
        Assert.Null(await store.GetAsync(Identity, "playfab-title-id", PlayFabEndpoint));
        Assert.Null(await store.GetAsync(Identity, "playfab-title-id", PlayFabEndpoint, "test"));

        var root = await ReadFileAsync();
        Assert.Equal(
            "1111111",
            root.GetProperty("contexts").GetProperty(GatewayScope).GetProperty("seller-id").GetString());
        Assert.Equal(
            "2222222",
            root.GetProperty("profiles").GetProperty("test").GetProperty(GatewayScope)
                .GetProperty("seller-id").GetString());
    }

    /// <summary>
    /// The default profile always exists. A named one exists only while the account has something
    /// saved in it, so a misspelled name fails rather than quietly reading nothing.
    /// </summary>
    [Fact]
    public async Task GetAsync_MissingNamedProfileFailsButTheDefaultAlwaysExists()
    {
        var store = new ContextStore(FilePath);

        Assert.Null(await store.GetAsync(Identity, "seller-id", Gateway));
        Assert.True(await store.HasProfileAsync(Identity, ContextProfiles.Default));
        Assert.False(await store.HasProfileAsync(Identity, "test"));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync(Identity, "seller-id", Gateway, "test"));
        Assert.Equal(ContextProfiles.NotFoundMessage("test"), exception.Message);
        Assert.False(Directory.Exists(_directory));
    }

    /// <summary>
    /// A profile belongs to the account that saved to it: another account's, even one whose user
    /// ID starts with this one's, doesn't exist for this account and isn't listed.
    /// </summary>
    [Fact]
    public async Task NamedProfile_ExistsOnlyForTheAccountThatSavedToIt()
    {
        var store = new ContextStore(FilePath);
        var other = new SignedInIdentity("tenant", "user2");
        await store.UpdateAsync(other, [new ContextChange("seller-id", Gateway, "1111111")], "test");

        Assert.True(await store.HasProfileAsync(other, "test"));
        Assert.False(await store.HasProfileAsync(Identity, "test"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync(Identity, "seller-id", Gateway, "test"));
        Assert.Equal([ContextProfiles.Default], await store.ListProfilesAsync(Identity));
        Assert.Equal([ContextProfiles.Default, "test"], await store.ListProfilesAsync(other));
    }

    /// <summary>The default profile is listed first, even before a name that sorts ahead of it.</summary>
    [Fact]
    public async Task ListProfilesAsync_ListsTheDefaultFirstThenNamedProfilesByName()
    {
        var store = new ContextStore(FilePath);
        Assert.Equal([ContextProfiles.Default], await store.ListProfilesAsync(Identity));

        foreach (var profile in new[] { "test", "alpha", "prod" })
        {
            await store.UpdateAsync(
                Identity,
                [new ContextChange("seller-id", Gateway, "1111111")],
                profile);
        }

        Assert.Equal(
            [ContextProfiles.Default, "alpha", "prod", "test"],
            await store.ListProfilesAsync(Identity));
    }

    /// <summary>
    /// Clearing a named profile removes the account's values in it on every endpoint and nothing
    /// else, and says whether there was anything to remove.
    /// </summary>
    [Fact]
    public async Task RemoveAllAsync_ClearsOnlyTheNamedProfile()
    {
        var store = new ContextStore(FilePath);
        var other = new SignedInIdentity("tenant", "user2");
        Assert.False(await store.RemoveAllAsync(Identity, "test"));

        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "1111111")]);
        await store.UpdateAsync(
            Identity,
            [
                new ContextChange("seller-id", Gateway, "2222222"),
                new ContextChange("playfab-title-id", PlayFabEndpoint, "TEST1"),
            ],
            "test");
        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "3333333")], "prod");
        await store.UpdateAsync(other, [new ContextChange("seller-id", Gateway, "4444444")], "test");

        Assert.True(await store.RemoveAllAsync(Identity, "test"));

        Assert.False(await store.HasProfileAsync(Identity, "test"));
        Assert.Equal("1111111", await store.GetAsync(Identity, "seller-id", Gateway));
        Assert.Equal("3333333", await store.GetAsync(Identity, "seller-id", Gateway, "prod"));
        Assert.Equal("4444444", await store.GetAsync(other, "seller-id", Gateway, "test"));
        Assert.False(await store.RemoveAllAsync(Identity, "test"));
    }

    /// <summary>
    /// A named profile with nothing saved in it no longer exists, so removing its last value
    /// deletes it, and a file left with no named profile has the shape a version-1 CLI wrote.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_RemovingTheLastValueOfANamedProfileDeletesIt()
    {
        var store = new ContextStore(FilePath);
        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "1111111")]);
        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "2222222")], "test");

        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, null)], "test");

        Assert.False(await store.HasProfileAsync(Identity, "test"));
        Assert.Equal([ContextProfiles.Default], await store.ListProfilesAsync(Identity));
        Assert.Equal("1111111", await store.GetAsync(Identity, "seller-id", Gateway));
        Assert.False((await ReadFileAsync()).TryGetProperty("profiles", out _));
    }

    /// <summary>
    /// When saving is refused because nothing new can be created in the state directory, the
    /// error says why and that reading still works, and it does: the value saved before is still
    /// there and nothing staged is left behind.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_UnwritableDirectoryExplainsThatReadsStillWorkAndKeepsTheOldValue()
    {
        var store = new ContextStore(FilePath);
        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "1111111")]);
        MakeDirectoryUnwritable();

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "2222222")]));

        AssertNotWritable(exception);
        Assert.Equal("1111111", await store.GetAsync(Identity, "seller-id", Gateway));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    /// <summary>A lock file that can't be opened for writing is reported the same way.</summary>
    [Fact]
    public async Task UpdateAsync_UnwritableLockExplainsThatReadsStillWork()
    {
        var store = new ContextStore(FilePath);
        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "1111111")]);
        MakeReadOnly(LockPath);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => store.RemoveAllAsync(Identity));

        AssertNotWritable(exception);
        Assert.Equal("1111111", await store.GetAsync(Identity, "seller-id", Gateway));
    }

    /// <summary>
    /// The lock is the open handle, not the file: a <c>context.json.lock</c> left behind by an
    /// earlier write, or by a process that crashed mid-write, doesn't block the next one.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_LeftoverLockFileDoesNotBlockAWrite()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(LockPath, "left behind");
        var store = new ContextStore(FilePath);

        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "1111111")]);

        Assert.Equal("1111111", await store.GetAsync(Identity, "seller-id", Gateway));
        Assert.True(File.Exists(LockPath));
    }

    /// <summary>
    /// A write waits while another process holds the lock, and goes ahead once it's released, so
    /// two saves from different shells both take effect.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_WaitsForAnotherWriterToReleaseTheLock()
    {
        var store = new ContextStore(FilePath);
        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "1111111")]);

        Task update;
        await using (new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            update = Task.Run(() => store.UpdateAsync(
                Identity,
                [new ContextChange("playfab-title-id", PlayFabEndpoint, "ABC12")]));
            await Task.Delay(150);

            Assert.False(update.IsCompleted, "The write went ahead while another writer held the lock.");
        }

        await update;
        Assert.Equal("1111111", await store.GetAsync(Identity, "seller-id", Gateway));
        Assert.Equal("ABC12", await store.GetAsync(Identity, "playfab-title-id", PlayFabEndpoint));
    }

    /// <summary>
    /// Writers read without the lock first, and <c>context get</c> never takes it, so the file can
    /// be open for reading when a write replaces it. Windows refuses to replace an open file, so
    /// the replace is retried rather than failing the save. The reader here holds the file until
    /// the new version is staged, so the first attempt is certain to overlap it.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_ReaderHoldingTheFileBrieflyDoesNotFailTheSave()
    {
        var store = new ContextStore(FilePath);
        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "1111111")]);
        var reader = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var completedWhileHeld = false;

        var update = Task.Run(() => store.UpdateAsync(
            Identity,
            [new ContextChange("seller-id", Gateway, "2222222")]));
        var release = Task.Run(async () =>
        {
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!update.IsCompleted
                       && !Directory.EnumerateFiles(_directory, "*.tmp").Any()
                       && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(5);
                }

                await Task.Delay(100);
                completedWhileHeld = update.IsCompleted;
            }
            finally
            {
                await reader.DisposeAsync();
            }
        });

        await update;
        await release;

        Assert.Equal("2222222", await store.GetAsync(Identity, "seller-id", Gateway));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        if (OperatingSystem.IsWindows())
        {
            Assert.False(completedWhileHeld, "The file was replaced while a reader had it open.");
        }
    }

    /// <summary>
    /// Shells, scripts, and agents run the CLI at once. Concurrent saves, each for its own
    /// account, overlap each other's unlocked reads and the readers alongside them, and every one
    /// still takes effect: none is lost to another's read-modify-write, and none fails.
    /// </summary>
    [Fact]
    public async Task ConcurrentWritesAndReads_KeepEveryValue()
    {
        const int writers = 20;
        var seed = new SignedInIdentity("tenant", "seed");
        var identities = Enumerable.Range(0, writers)
            .Select(index => new SignedInIdentity("tenant", $"user-{index}"))
            .ToArray();

        // Start from a file, so every writer and reader has one to open.
        await new ContextStore(FilePath).UpdateAsync(
            seed,
            [new ContextChange("seller-id", Gateway, "1000000")]);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = identities
            .Select((identity, index) => Task.Run(async () =>
            {
                await start.Task;
                await new ContextStore(FilePath).UpdateAsync(
                    identity,
                    [
                        new ContextChange("seller-id", Gateway, $"{index + 1}000000"),
                        new ContextChange("playfab-title-id", PlayFabEndpoint, $"TITLE{index}"),
                    ]);
            }))
            .ToArray();
        var allWritten = Task.WhenAll(writing);
        var reading = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                var store = new ContextStore(FilePath);

                // Read at least once: on a fast machine every write can finish before a reader
                // is scheduled, and the test must not depend on winning that race.
                do
                {
                    Assert.Equal("1000000", await store.GetAsync(seed, "seller-id", Gateway));
                    await Task.Delay(1);
                }
                while (!allWritten.IsCompleted);
            }))
            .ToArray();

        start.SetResult();
        await Task.WhenAll([.. writing, .. reading]);

        var result = new ContextStore(FilePath);
        for (var index = 0; index < writers; index++)
        {
            Assert.Equal(
                $"{index + 1}000000",
                await result.GetAsync(identities[index], "seller-id", Gateway));
            Assert.Equal(
                $"TITLE{index}",
                await result.GetAsync(identities[index], "playfab-title-id", PlayFabEndpoint));
        }

        Assert.Equal("1000000", await result.GetAsync(seed, "seller-id", Gateway));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    /// <summary>
    /// A newer CLI may have changed the format. This one refuses to read the file, so it can't
    /// misread it, and refuses to write it, so it can't downgrade it under the newer CLI: the
    /// file is left exactly as it was, and no lock is taken.
    /// </summary>
    [Fact]
    public async Task NewerVersion_IsReportedAndLeftAlone()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            FilePath,
            """{"version":3,"contexts":{"tenant:user@gateway.example":{"seller-id":"1111111"}},"newerField":true}""");
        var before = await File.ReadAllBytesAsync(FilePath);
        var store = new ContextStore(FilePath);

        var read = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync(Identity, "seller-id", Gateway));
        var update = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "2222222")]));
        var removeAll = await Assert.ThrowsAsync<InvalidDataException>(() => store.RemoveAllAsync(Identity));

        Assert.Equal(
            $"The saved context file '{FilePath}' was written by a newer version of the XBOX CLI. "
            + "Update the CLI to use it.",
            read.Message);
        Assert.Equal(read.Message, update.Message);
        Assert.Equal(read.Message, removeAll.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(FilePath));
        Assert.False(File.Exists(LockPath));
    }

    /// <summary>
    /// An older version is read as it is and written back as the current one. A version-1 file,
    /// written before profiles existed, is the default profile as it is.
    /// </summary>
    [Fact]
    public async Task OlderVersion_IsReadAndUpgradedOnTheNextWrite()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            FilePath,
            """{"version":1,"contexts":{"tenant:user@gateway.example":{"seller-id":"1111111"}}}""");
        var store = new ContextStore(FilePath);

        Assert.Equal("1111111", await store.GetAsync(Identity, "seller-id", Gateway));
        Assert.Equal([ContextProfiles.Default], await store.ListProfilesAsync(Identity));

        await store.UpdateAsync(Identity, [new ContextChange("playfab-title-id", Gateway, "ABC12")]);

        var root = await ReadFileAsync();
        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.Equal("1111111", root.GetProperty("contexts").GetProperty(GatewayScope).GetProperty("seller-id").GetString());
    }

    /// <summary>
    /// A write that changes nothing returns before taking the lock, so it needs no writable state
    /// directory and leaves no lock file behind.
    /// </summary>
    [Fact]
    public async Task Writes_ChangingNothingTakeNoLock()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            FilePath,
            """{"version":1,"contexts":{"tenant:user@gateway.example":{"seller-id":"1111111"}}}""");
        var store = new ContextStore(FilePath);

        await store.UpdateAsync(Identity, [new ContextChange("seller-id", Gateway, "1111111")]);
        await store.UpdateAsync(Identity, [new ContextChange("playfab-title-id", Gateway, null)]);
        await store.RemoveAllAsync(new SignedInIdentity("tenant", "other-user"));

        Assert.False(File.Exists(LockPath));
        Assert.Equal("1111111", await store.GetAsync(Identity, "seller-id", Gateway));
    }
    [Fact]
    public async Task SequentialWrites_PreserveEveryScopedValue()
    {
        var identities = Enumerable.Range(0, 5)
            .Select(index => new SignedInIdentity("tenant", $"user-{index}"))
            .ToArray();

        // Each write is a read-modify-write over a file shared by every scope, so a later value
        // must merge into the existing map rather than replace it.
        for (var index = 0; index < identities.Length; index++)
        {
            await new ContextStore(FilePath).UpdateAsync(
                identities[index],
                [new ContextChange("seller-id", Gateway, $"{index + 1}000000")]);
        }

        var store = new ContextStore(FilePath);
        for (var index = 0; index < identities.Length; index++)
        {
            Assert.Equal(
                $"{index + 1}000000",
                await store.GetAsync(identities[index], "seller-id", Gateway));
        }
    }

    private static void AssertNotWritable(UnauthorizedAccessException exception)
    {
        Assert.StartsWith("Cannot save the context because '", exception.Message, StringComparison.Ordinal);
        Assert.Contains("is not writable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Reading saved context still works", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Makes the state directory refuse new files while the files already in it stay usable:
    /// Windows denies creating files in it; Unix makes the directory read-only.
    /// </summary>
    private void MakeDirectoryUnwritable()
    {
        _directoryUnwritable = true;
        if (OperatingSystem.IsWindows())
        {
            SetCreatingFilesDenied(new DirectoryInfo(_directory), denied: true);
        }
        else
        {
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    private static void MakeReadOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
        }
    }

    private void MakeStateWritable()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        if (_directoryUnwritable)
        {
            if (OperatingSystem.IsWindows())
            {
                SetCreatingFilesDenied(new DirectoryInfo(_directory), denied: false);
            }
            else
            {
                File.SetUnixFileMode(
                    _directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        foreach (var path in new[] { FilePath, LockPath }.Where(File.Exists))
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetCreatingFilesDenied(DirectoryInfo directory, bool denied)
    {
        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles,
            AccessControlType.Deny);
        var security = directory.GetAccessControl();
        if (denied)
        {
            security.AddAccessRule(rule);
        }
        else
        {
            security.RemoveAccessRule(rule);
        }

        directory.SetAccessControl(security);
    }

    private async Task<JsonElement> ReadFileAsync()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath));
        return document.RootElement.Clone();
    }
}