// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FsCheck.Xunit;

namespace XboxDevCLI.FuzzTest;

/// <summary>
/// A <see cref="PropertyAttribute"/> preconfigured for this fuzz suite: it wires in the
/// adversarial generators from <see cref="FuzzArbitraries"/> and reads the per-property
/// case count from <c>FUZZ_ITERATIONS</c> (see <see cref="FuzzConfig"/>). On failure,
/// FsCheck.Xunit prints the shrunk counterexample together with the replay seed, so any
/// crash surfaced in CI is deterministically reproducible.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class FuzzPropertyAttribute : PropertyAttribute
{
    public FuzzPropertyAttribute()
    {
        MaxTest = FuzzConfig.Iterations;
        Arbitrary = new[] { typeof(FuzzArbitraries) };
    }
}
