// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Commands;

/// <summary>
/// Clock, delay, and terminal seams for XboxPartner commands that submit a job and wait for it.
/// </summary>
internal sealed record XboxPartnerJobDependencies(
    Func<TimeSpan, CancellationToken, Task> DelayAsync,
    Func<DateTimeOffset> GetUtcNow,
    Func<bool> IsInteractive)
{
    internal static XboxPartnerJobDependencies Default { get; } = new(
        Task.Delay,
        () => DateTimeOffset.UtcNow,
        () => XboxPartnerCommandSupport.IsInteractiveTerminal());
}
