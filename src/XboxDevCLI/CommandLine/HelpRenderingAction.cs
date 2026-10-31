// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;

namespace XboxDevCLI.CommandLine;

/// <summary>
/// A command action that renders Help rather than running a command.
/// </summary>
/// <remarks>
/// Exists so callers can ask "does this invocation render Help?" without enumerating the ways to
/// reach Help. There are four — <c>--help</c>, the <c>help</c> command, a bare group, and the bare
/// root — and a check written against a list of them silently rots as new ones are added. That is
/// not hypothetical: the <c>--output table</c> gate originally recognised only <c>--help</c> and
/// wrongly rejected the other three, each of which was reported separately.
/// <para>
/// Marking the actions at construction inverts the problem: anything
/// <see cref="CommandLineExperience"/> installs to render Help is identifiable by type, so a fifth
/// route is covered the moment it is built this way.
/// </para>
/// </remarks>
internal sealed class HelpRenderingAction(Func<ParseResult, int> render) : SynchronousCommandLineAction
{
    private readonly Func<ParseResult, int> render =
        render ?? throw new ArgumentNullException(nameof(render));

    public override int Invoke(ParseResult parseResult) => this.render(parseResult);
}
