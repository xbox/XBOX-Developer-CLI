// Copyright (C) Microsoft Corporation. All rights reserved.

using System.CommandLine;
using XboxDevCLI.Services;

namespace XboxDevCLI.CommandLine;

internal static class RecoveryCommandArguments
{
    public static List<string> Create(ParseResult result)
    {
        var arguments = new List<string> { CliIdentity.CommandName };
        foreach (var (name, value) in new (string, string?)[]
        {
            ("--client-id", result.GetValue(Program.ClientIdOption)),
            ("--tenant-id", result.GetValue(Program.TenantIdOption)),
            ("--scopes", result.GetValue(Program.ScopesOption)),
        })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                arguments.Add(name);
                arguments.Add(ShellQuoting.Quote(value));
            }
        }

        string profile = ContextProfiles.From(result);
        if (profile != ContextProfiles.Default ||
            result.GetResult(ContextProfiles.Option) is { Implicit: false })
        {
            arguments.Add("--profile");
            arguments.Add(ShellQuoting.Quote(profile));
        }

        return arguments;
    }
}
