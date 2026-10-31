// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Assertions about UTF-16 well-formedness, shared by the two table renderers' truncation tests.
/// </summary>
internal static class SurrogateAssert
{
    /// <summary>
    /// Asserts <paramref name="value"/> contains no lone surrogate — every high surrogate is
    /// followed by its low half, and no low half appears on its own.
    /// </summary>
    /// <remarks>
    /// Written as an explicit walk that steps <em>over</em> a validated low half. The obvious
    /// formulation — asserting no character is a surrogate at all — passes only while truncation
    /// happens to discard every emoji, and starts failing the moment a complete pair survives,
    /// which is the case these tests exist to cover.
    /// </remarks>
    internal static void NoLoneSurrogates(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                Assert.True(
                    index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]),
                    $"Truncation left a lone high surrogate at index {index}.");

                // Step over the low half just validated, so it is not mistaken for a lone one.
                index++;
                continue;
            }

            Assert.False(
                char.IsLowSurrogate(value[index]),
                $"Truncation left a lone low surrogate at index {index}.");
        }
    }
}
