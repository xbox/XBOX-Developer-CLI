// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Safety;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class DestructiveOperationsTests
{
    [Theory]
    [InlineData("/Admin/DeleteTitle")]
    [InlineData("/Admin/DeleteMasterPlayerAccount")]
    [InlineData("/Admin/DeleteStore")]
    [InlineData("/Admin/DeleteSegment")]
    [InlineData("/Group/DeleteGroup")]
    [InlineData("/MultiplayerServer/DeleteBuild")]
    [InlineData("/MultiplayerServer/DeleteAsset")]
    [InlineData("/MultiplayerServer/DeleteCertificate")]
    [InlineData("/MultiplayerServer/DeleteSecret")]
    [InlineData("/MultiplayerServer/ShutdownMultiplayerServer")]
    [InlineData("Admin/DeleteTitle")]
    [InlineData("DeleteTitle")]
    [InlineData("/admin/deletetitle")]
    [InlineData("/multiplayerserver/shutdownmultiplayerserver")]
    [InlineData("  /Admin/DeleteTitle  ")]
    // Obfuscated paths that System.Uri normalizes to a destructive operation before sending.
    // These must be classified destructive so the --confirm gate cannot be bypassed.
    [InlineData("Admin\\DeleteTitle")]
    [InlineData("/Admin\\DeleteTitle")]
    [InlineData("Admin/%44eleteTitle")]
    [InlineData("/Admin/%44eleteTitle")]
    [InlineData("/MultiplayerServer/%53hutdownMultiplayerServer")]
    [InlineData("/Admin/DeleteTitle?foo=bar")]
    public void IsDestructive_DestructiveOperations_ReturnTrue(string apiPath)
    {
        Assert.True(DestructiveOperations.IsDestructive(apiPath));
    }

    [Theory]
    [InlineData("/Admin/GetTitleData")]
    [InlineData("/Admin/SetTitleData")]
    [InlineData("/Catalog/SearchItems")]
    [InlineData("/Admin/RemovePlayerTag")]
    [InlineData("/Admin/UndeleteTitle")]
    [InlineData("/Some/UndeletePath")]
    [InlineData("/MultiplayerServer/ListBuildSummariesV2")]
    [InlineData("/MultiplayerServer/GetBuild")]
    public void IsDestructive_NonDestructiveOperations_ReturnFalse(string apiPath)
    {
        Assert.False(DestructiveOperations.IsDestructive(apiPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsDestructive_NullOrEmpty_ReturnFalse(string? apiPath)
    {
        Assert.False(DestructiveOperations.IsDestructive(apiPath));
    }

    /// <summary>
    /// <c>DeleteTitle</c> deletes the title it's sent to, so its target is the PlayFab title in
    /// context, classified the way the gate classifies it, disguises included.
    /// </summary>
    [Theory]
    [InlineData("/Admin/DeleteTitle")]
    [InlineData("Admin/DeleteTitle")]
    [InlineData("/admin/deletetitle")]
    [InlineData("  /Admin/DeleteTitle?foo=bar  ")]
    [InlineData("Admin\\DeleteTitle")]
    [InlineData("/Admin/%44eleteTitle")]
    public void GetContextTarget_DeleteTitleActsOnThePlayFabTitle(string apiPath)
    {
        Assert.Equal(new ContextTarget("--playfab-title-id"), DestructiveOperations.GetContextTarget(apiPath));
    }

    /// <summary>Everything else acts on what its request names, or isn't destructive at all.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/Admin/DeleteTitleDataOverride")]
    [InlineData("/Admin/DeletePlayer")]
    [InlineData("/MultiplayerServer/DeleteBuild")]
    [InlineData("/Admin/GetTitleData")]
    public void GetContextTarget_OtherOperationsHaveNone(string? apiPath)
    {
        Assert.Null(DestructiveOperations.GetContextTarget(apiPath));
    }
}
