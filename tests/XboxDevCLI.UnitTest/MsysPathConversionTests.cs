// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.CommandLine;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class MsysPathConversionTests
{
    [Theory]
    [InlineData("C:/Program Files/Git/Catalog/GetCatalogConfig", "C:/Program Files/Git", "/Catalog/GetCatalogConfig")]
    [InlineData("C:/msys64/Admin/DeleteTitle", "C:/msys64", "/Admin/DeleteTitle")]
    [InlineData("D:/Tools/Git/Catalog/GetCatalogConfig/", "D:/Tools/Git", "/Catalog/GetCatalogConfig/")]
    public void TryGetOriginalPath_StripsTheMsysRoot(string argument, string root, string expected)
    {
        Assert.True(MsysPathConversion.TryGetOriginalPath(argument, directory => directory == root, out var originalPath));
        Assert.Equal(expected, originalPath);
    }

    [Fact]
    public void TryGetOriginalPath_UsesTheShortestRoot()
    {
        // A directory inside the install that also looks like a root mustn't shorten the path.
        Assert.True(MsysPathConversion.TryGetOriginalPath(
            "C:/Program Files/Git/usr/Catalog/GetCatalogConfig",
            directory => directory is "C:/Program Files/Git" or "C:/Program Files/Git/usr",
            out var originalPath));
        Assert.Equal("/usr/Catalog/GetCatalogConfig", originalPath);
    }

    [Theory]
    [InlineData("C:/Users/dev/AppData/Local/Temp/foo")]
    [InlineData("C:/foo")]
    public void TryGetOriginalPath_WithoutAnMsysRoot_ReturnsFalse(string argument)
    {
        Assert.False(MsysPathConversion.TryGetOriginalPath(argument, _ => false, out _));
    }

    [Theory]
    [InlineData("Catalog/GetCatalogConfig")]
    [InlineData("/Catalog/GetCatalogConfig")]
    [InlineData("//Catalog/GetCatalogConfig")]
    [InlineData("C:\\Program Files\\Git\\Catalog\\GetCatalogConfig")]
    [InlineData("C:")]
    [InlineData("1:/Catalog/GetCatalogConfig")]
    public void TryGetOriginalPath_NotAConvertedPath_ReturnsFalseWithoutProbing(string argument)
    {
        Assert.False(MsysPathConversion.TryGetOriginalPath(
            argument,
            directory => throw new InvalidOperationException($"Probed {directory}"),
            out _));
    }

    [Fact]
    public void TryGetOriginalPath_RootWithNothingAfterIt_ReturnsFalse()
    {
        Assert.False(MsysPathConversion.TryGetOriginalPath(
            "C:/Program Files/Git/", directory => directory == "C:/Program Files/Git", out _));
    }

    [Fact]
    public void IsMsysRoot_FindsTheMsysRuntimeOnWindowsOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root, "usr", "bin"));
            var forwardSlashRoot = root.Replace('\\', '/');
            Assert.False(MsysPathConversion.IsMsysRoot(forwardSlashRoot));

            File.WriteAllBytes(Path.Combine(bin.FullName, "msys-2.0.dll"), []);
            Assert.Equal(OperatingSystem.IsWindows(), MsysPathConversion.IsMsysRoot(forwardSlashRoot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
