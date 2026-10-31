// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;

namespace XboxDevCLI.Services;

/// <summary>
/// Named sets of saved context. A command uses the profile <c>--profile</c> names, else the one
/// <see cref="EnvironmentVariable"/> names, else <see cref="Default"/>. No "current profile" is
/// saved, so choosing one for a command or a shell never retargets another terminal or agent.
/// </summary>
internal static class ContextProfiles
{
    public const string Default = "default";

    /// <summary>Picks a profile for a shell or a process and its children, below <c>--profile</c>.</summary>
    public const string EnvironmentVariable = "XBOX_PROFILE";

    private const int MaxNameLength = 64;

    /// <summary>The global <c>--profile</c> option.</summary>
    public static Option<string?> Option { get; } = CreateOption();

    /// <summary>
    /// The profile the invocation selected: <c>--profile</c>, else <see cref="EnvironmentVariable"/>,
    /// else <see cref="Default"/>. A blank variable counts as unset; an invalid one fails, as an
    /// invalid <c>--profile</c> does, rather than quietly selecting the default.
    /// </summary>
    public static string From(ParseResult parseResult)
    {
        if (Normalize(parseResult.GetValue(Option)) is { } option)
        {
            return option;
        }

        var variable = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (Normalize(variable) is not { } name)
        {
            return Default;
        }

        return Validate(name) is { } problem
            ? throw new InvalidOperationException($"{EnvironmentVariable} {problem}.")
            : name;
    }

    /// <summary>The name as it's stored: trimmed and lowercase, or <c>null</c> when blank.</summary>
    public static string? Normalize(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim().ToLowerInvariant();

    /// <summary>Returns why <paramref name="name"/> isn't a valid profile name, or <c>null</c>.</summary>
    public static string? Validate(string name)
    {
        var normalized = Normalize(name);
        return normalized is null
            ? "must not be blank"
            : normalized.Length > MaxNameLength
              || !char.IsAsciiLetterOrDigit(normalized[0])
              || !normalized.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                ? $"must be letters, digits, and hyphens, starting with a letter or digit, and at most {MaxNameLength} characters"
                : null;
    }

    /// <summary>What a command says when a named profile has nothing saved for the account.</summary>
    public static string NotFoundMessage(string profile) =>
        $"No profile named '{profile}'. Run '{CliIdentity.CommandName} context profile list' to "
        + "see yours, or save a value to it with "
        + $"'{CliIdentity.CommandName} --profile {profile} context set <key> <value>'.";

    /// <summary>
    /// The start of a <c>context</c> command that acts on <paramref name="profile"/>, for messages
    /// that suggest one. It names the profile unless it's the default one and
    /// <see cref="EnvironmentVariable"/> doesn't pick another, so the suggestion acts on the
    /// profile it's about rather than on whatever the shell selects.
    /// </summary>
    public static string ContextCommand(string profile) =>
        profile == Default
        && (Normalize(Environment.GetEnvironmentVariable(EnvironmentVariable)) ?? Default) == Default
            ? $"{CliIdentity.CommandName} context"
            : $"{CliIdentity.CommandName} --profile {profile} context";

    private static Option<string?> CreateOption()
    {
        var option = new Option<string?>("--profile")
        {
            Description =
                $"Use the context values saved in this profile. Defaults to {EnvironmentVariable}, "
                + $"then '{Default}'.",
            HelpName = "name",
            Recursive = true,
        };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && Validate(result.Tokens[0].Value) is { } problem)
            {
                result.AddError($"--profile {problem}.");
            }
        });
        return option;
    }
}
