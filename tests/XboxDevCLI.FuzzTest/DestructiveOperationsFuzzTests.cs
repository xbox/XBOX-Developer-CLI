// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Xunit;
using XboxDevCLI.Safety;

namespace XboxDevCLI.FuzzTest;

/// <summary>
/// Fuzz properties for the destructive-operation gate. This is the highest-priority
/// target: <see cref="DestructiveOperations.IsDestructive"/> feeds the <c>--confirm</c>
/// safety gate, so it must (a) never throw on adversarial input and (b) never let a
/// genuinely destructive operation slip past normalization tricks
/// (backslashes, percent-encoding, unicode, query/fragment).
/// </summary>
public class DestructiveOperationsFuzzTests
{
    /// <summary>Reduce a fuzzed value to a URI-safe, non-empty group segment.</summary>
    private static string SafeGroup(string? value)
    {
        var group = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        return group.Length == 0 ? "Admin" : group;
    }

    [FuzzProperty]
    public bool IsDestructive_NeverThrows(AdversarialPath path)
    {
        // The gate must be total: any string in, a bool out, no exceptions.
        _ = DestructiveOperations.IsDestructive(path.Value);
        return true;
    }

    [FuzzProperty]
    public bool WhitespaceAndTrailingSlashes_DoNotChangeClassification(AdversarialPath path)
    {
        var group = SafeGroup(path.Value);
        var canonical = $"/{group}/DeleteThing";
        var decorated = $"   /{group}/DeleteThing/  ";

        // Surrounding whitespace and trailing slashes are canonicalized away, so they
        // must never flip a destructive operation to non-destructive.
        return DestructiveOperations.IsDestructive(canonical)
            == DestructiveOperations.IsDestructive(decorated);
    }

    [FuzzProperty]
    public bool DeleteAndShutdown_StayClassifiedDestructive(AdversarialPath path)
    {
        var group = SafeGroup(path.Value);

        // A destructive operation must stay classified destructive regardless of which
        // group it lives under (guards against under-triggering the --confirm gate).
        return DestructiveOperations.IsDestructive($"/{group}/DeleteThing")
            && DestructiveOperations.IsDestructive($"/{group}/ShutdownThing");
    }

    [FuzzProperty]
    public bool ReadOnlyOperations_AreNeverDestructive(AdversarialPath path)
    {
        var group = SafeGroup(path.Value);

        // Get/List/Search operations are read-only and must never be gated.
        return !DestructiveOperations.IsDestructive($"/{group}/GetThing")
            && !DestructiveOperations.IsDestructive($"/{group}/ListThings")
            && !DestructiveOperations.IsDestructive($"/{group}/SearchThings");
    }

    [Fact]
    public void BackslashAndPercentEncoding_MatchCanonicalForm()
    {
        // Encodings that the URI layer folds to "/Admin/DeleteTitle" must all be
        // treated as destructive, matching the plain form.
        Assert.True(DestructiveOperations.IsDestructive("/Admin/DeleteTitle"));
        Assert.True(DestructiveOperations.IsDestructive(@"Admin\DeleteTitle"));
        Assert.True(DestructiveOperations.IsDestructive("/Admin/%44eleteTitle"));
    }
}
