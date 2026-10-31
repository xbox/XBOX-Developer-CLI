// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.FuzzTest;

/// <summary>
/// Central FsCheck configuration for the fuzz suite. The number of generated cases
/// per property is controlled by the <c>FUZZ_ITERATIONS</c> environment variable so
/// CI (and local runs) can dial the fuzzing budget up or down without code changes.
/// </summary>
internal static class FuzzConfig
{
    /// <summary>Default number of cases per property when <c>FUZZ_ITERATIONS</c> is unset.</summary>
    public const int DefaultIterations = 500;

    /// <summary>Resolved number of cases per property, clamped to a sane range.</summary>
    public static int Iterations
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("FUZZ_ITERATIONS");
            if (int.TryParse(raw, out var parsed) && parsed > 0)
            {
                return Math.Min(parsed, 1_000_000);
            }

            return DefaultIterations;
        }
    }
}
