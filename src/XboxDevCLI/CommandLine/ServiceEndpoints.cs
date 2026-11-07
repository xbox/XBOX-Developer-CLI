// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Runtime.CompilerServices;

namespace XboxDevCLI.CommandLine;

/// <summary>The service endpoints a command sends requests and access tokens to.</summary>
[Flags]
internal enum ServiceEndpoints
{
    /// <summary>Sends no token to a service endpoint the CLI configures.</summary>
    None = 0,

    /// <summary>The PlayFab API, at <c>PlayFab:BaseUrlTemplate</c>.</summary>
    PlayFab = 1,

    /// <summary>XBOX services, at <c>XboxPartner:BaseUrl</c> or the command's <c>--base-url</c>.</summary>
    XboxPartner = 2,
}

/// <summary>
/// Side table holding the <see cref="ServiceEndpoints"/> each command declares it calls.
/// Preparation writes the warning for each declared endpoint that's been changed, before the
/// command runs, so a warning is written by every command that sends a token to that endpoint and
/// by no other. A command inherits its nearest ancestor's declaration.
/// </summary>
internal static class CommandEndpoints
{
    private static readonly ConditionalWeakTable<Command, StrongBox<ServiceEndpoints>> Declared = new();

    /// <summary>Declares the endpoints <paramref name="command"/> and its subcommands call.</summary>
    internal static TCommand Calls<TCommand>(this TCommand command, ServiceEndpoints endpoints)
        where TCommand : Command
    {
        Declared.AddOrUpdate(command, new StrongBox<ServiceEndpoints>(endpoints));
        return command;
    }

    /// <summary>The endpoints declared on <paramref name="command"/> or its nearest ancestor, if any.</summary>
    internal static ServiceEndpoints? FindDeclared(Command command)
    {
        for (Command? current = command; current is not null; current = current.Parents.OfType<Command>().FirstOrDefault())
        {
            if (Declared.TryGetValue(current, out var endpoints))
            {
                return endpoints.Value;
            }
        }

        return null;
    }

    /// <summary>The endpoints <paramref name="command"/> calls.</summary>
    internal static ServiceEndpoints For(Command command) => FindDeclared(command) ?? ServiceEndpoints.None;
}
