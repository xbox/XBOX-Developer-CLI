// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.E2ETest;

/// <summary>
/// The tests' settings, read from <c>XBOXDEVCLI_E2E_</c> environment variables. They don't start
/// with <c>XBOX_</c>, because the CLI reads every <c>XBOX_</c> variable as its own configuration.
/// </summary>
/// <remarks>
/// Each value names something that already exists for the signed-in account, because the tests
/// only read. The tests are skipped until every setting is set, unless
/// <c>XBOXDEVCLI_E2E_REQUIRED</c> is <c>true</c>, which makes them fail instead.
/// </remarks>
internal sealed class E2ESettings
{
    private const string Prefix = "XBOXDEVCLI_E2E_";

    private static readonly E2ESettings Instance = new();

    private readonly List<string> _missing = [];

    private E2ESettings()
    {
        Cli = Read("CLI");
        Username = Read("USERNAME");
        SellerId = Read("SELLER_ID");
        StoreId = Read("STORE_ID");
        TitleId = Read("TITLE_ID");
        Scid = Read("SCID");
        GameKeyword = Read("GAME_KEYWORD");
        GameJobId = Read("GAME_JOB_ID");
        Sandbox = Read("SANDBOX");
        AchievementImageId = Read("ACHIEVEMENT_IMAGE_ID");
        BatchKeyword = Read("BATCH_KEYWORD");
        BatchJobId = Read("BATCH_JOB_ID");
    }

    /// <summary>The settings, once every one is set.</summary>
    public static E2ESettings Current =>
        Instance.Problem is { } problem ? throw new InvalidOperationException(problem) : Instance;

    /// <summary>Why the tests are skipped, or null when they run.</summary>
    public static string? SkipReason =>
        bool.TryParse(Environment.GetEnvironmentVariable(Prefix + "REQUIRED"), out bool required) && required
            ? null
            : Instance.Problem;

    /// <summary>The absolute path of the <c>xbox</c> executable under test.</summary>
    public string Cli { get; }

    /// <summary>The account <c>xbox auth login</c> signed in.</summary>
    public string Username { get; }

    /// <summary>A Partner Center seller the account belongs to.</summary>
    public string SellerId { get; }

    /// <summary>The Store ID of a game on that seller.</summary>
    public string StoreId { get; }

    /// <summary>The game's XBOX title ID, as the CLI prints it: eight uppercase hexadecimal digits.</summary>
    public string TitleId { get; }

    /// <summary>The game's service configuration ID.</summary>
    public string Scid { get; }

    /// <summary>Text that, ignoring case, the game's name contains and no other game's name on the seller does.</summary>
    public string GameKeyword { get; }

    /// <summary>The job that created the game.</summary>
    public string GameJobId { get; }

    /// <summary>A development sandbox where the game has published XBOX services configuration.</summary>
    public string Sandbox { get; }

    /// <summary>One of the game's achievement image asset IDs.</summary>
    public string AchievementImageId { get; }

    /// <summary>A tag that exactly one of the account's test account batches has.</summary>
    public string BatchKeyword { get; }

    /// <summary>The job that created that batch.</summary>
    public string BatchJobId { get; }

    private string? Problem =>
        _missing.Count == 0
            ? null
            : $"End-to-end test settings aren't set: {string.Join(", ", _missing)}. "
                + "See docs/contributing/testing.md.";

    private string Read(string name)
    {
        string value = Environment.GetEnvironmentVariable(Prefix + name)?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            _missing.Add(Prefix + name);
        }

        return value;
    }
}
