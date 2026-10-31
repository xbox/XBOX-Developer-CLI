// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Profile names: what <c>--profile</c> and <c>XBOX_PROFILE</c> accept, which one wins, and how a
/// name is stored, so <c>Test</c> and <c>test</c> select the same profile.
/// </summary>
/// <remarks>
/// Sets <c>XBOX_PROFILE</c>, which is process-global, so it shares the collection that serializes
/// every test that sets environment variables. Each test starts with it unset.
/// </remarks>
[Collection("ConsoleRedirect")]
public sealed class ContextProfilesTests : IDisposable
{
    private const string NameRule =
        "must be letters, digits, and hyphens, starting with a letter or digit, and at most 64 characters";

    private readonly string? _originalVariable =
        Environment.GetEnvironmentVariable(ContextProfiles.EnvironmentVariable);

    public ContextProfilesTests() =>
        Environment.SetEnvironmentVariable(ContextProfiles.EnvironmentVariable, null);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(ContextProfiles.EnvironmentVariable, _originalVariable);

    [Theory]
    [InlineData("test")]
    [InlineData("Prod-2")]
    [InlineData("a")]
    [InlineData(" test ")]
    public void Validate_AcceptsLettersDigitsAndHyphens(string name)
    {
        Assert.Null(ContextProfiles.Validate(name));
    }

    [Theory]
    [InlineData("", "must not be blank")]
    [InlineData("   ", "must not be blank")]
    [InlineData("-x", NameRule)]
    [InlineData("a b", NameRule)]
    [InlineData("a_b", NameRule)]
    [InlineData("tëst", NameRule)]
    public void Validate_RejectsOtherNames(string name, string problem)
    {
        Assert.Equal(problem, ContextProfiles.Validate(name));
    }

    [Fact]
    public void Validate_AllowsAtMost64Characters()
    {
        Assert.Null(ContextProfiles.Validate(new string('a', 64)));
        Assert.Equal(NameRule, ContextProfiles.Validate(new string('a', 65)));
    }

    [Theory]
    [InlineData(" Test ", "test")]
    [InlineData("PROD-2", "prod-2")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Normalize_TrimsAndLowercases(string? name, string? expected)
    {
        Assert.Equal(expected, ContextProfiles.Normalize(name));
    }

    /// <summary>
    /// <c>--profile</c> is global, so it parses before or after the command, and a command given
    /// none uses the default profile.
    /// </summary>
    [Theory]
    [InlineData("default", "context", "get")]
    [InlineData("test", "--profile", "Test", "context", "get")]
    [InlineData("test", "context", "get", "--profile", "test")]
    public void From_IsTheNormalizedProfileOrTheDefault(string expected, params string[] args)
    {
        var parseResult = Program.BuildRootCommand().Parse(args, Program.NoResponseFileConfig);

        Assert.Empty(parseResult.Errors);
        Assert.Equal(expected, ContextProfiles.From(parseResult));
    }

    /// <summary>
    /// Without <c>--profile</c>, <c>XBOX_PROFILE</c> picks the profile; a blank one counts as
    /// unset, the way CI commonly writes "unset". <c>--profile</c> wins over it.
    /// </summary>
    [Theory]
    [InlineData(" Test ", "test", "context", "get")]
    [InlineData("   ", "default", "context", "get")]
    [InlineData("test", "prod", "--profile", "prod", "context", "get")]
    [InlineData("a_b", "prod", "--profile", "prod", "context", "get")]
    public void From_UsesTheVariableWhenNoOptionNamesAProfile(string variable, string expected, params string[] args)
    {
        Environment.SetEnvironmentVariable(ContextProfiles.EnvironmentVariable, variable);
        var parseResult = Program.BuildRootCommand().Parse(args, Program.NoResponseFileConfig);

        Assert.Equal(expected, ContextProfiles.From(parseResult));
    }

    /// <summary>
    /// An invalid <c>XBOX_PROFILE</c> fails, like an invalid <c>--profile</c>, rather than quietly
    /// selecting the default profile. A parser never sees the variable, so it fails when read.
    /// </summary>
    [Fact]
    public void From_RejectsAnInvalidVariable()
    {
        Environment.SetEnvironmentVariable(ContextProfiles.EnvironmentVariable, "a_b");
        var parseResult = Program.BuildRootCommand().Parse(["context", "get"], Program.NoResponseFileConfig);

        var exception = Assert.Throws<InvalidOperationException>(() => ContextProfiles.From(parseResult));

        Assert.Equal($"XBOX_PROFILE {NameRule}.", exception.Message);
    }

    /// <summary>An invalid name is a parse error, so no command runs against a profile it can't name.</summary>
    [Theory]
    [InlineData("a_b", "--profile " + NameRule + ".")]
    [InlineData(" ", "--profile must not be blank.")]
    public void Option_RejectsAnInvalidNameAtParseTime(string name, string expected)
    {
        var parseResult = Program.BuildRootCommand().Parse(
            ["--profile", name, "context", "get"],
            Program.NoResponseFileConfig);

        Assert.Contains(parseResult.Errors, error => error.Message == expected);
    }

    [Fact]
    public void NotFoundMessage_SaysHowToSeeProfilesAndCreateOne()
    {
        Assert.Equal(
            "No profile named 'test'. Run 'xbox context profile list' to see yours, or save a "
            + "value to it with 'xbox --profile test context set <key> <value>'.",
            ContextProfiles.NotFoundMessage("test"));
    }

    /// <summary>
    /// A suggested <c>context</c> command names its profile unless an unqualified one would select
    /// it anyway, so following the suggestion never acts on the profile <c>XBOX_PROFILE</c> picks
    /// instead. A named profile is always written out.
    /// </summary>
    [Theory]
    [InlineData("default", null, "xbox context")]
    [InlineData("default", "  ", "xbox context")]
    [InlineData("default", "Default", "xbox context")]
    [InlineData("default", "prod", "xbox --profile default context")]
    [InlineData("default", "a_b", "xbox --profile default context")]
    [InlineData("test", null, "xbox --profile test context")]
    [InlineData("test", "test", "xbox --profile test context")]
    public void ContextCommand_NamesTheProfileUnlessAnUnqualifiedCommandWouldSelectIt(
        string profile,
        string? variable,
        string expected)
    {
        Environment.SetEnvironmentVariable(ContextProfiles.EnvironmentVariable, variable);

        Assert.Equal(expected, ContextProfiles.ContextCommand(profile));
    }
}
