// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Commands;
using Xunit;

namespace XboxDevCLI.UnitTest;

public sealed class ListProgressTests
{
    [Fact]
    public void CountsItemsAndPagesIncludingEmptyPages()
    {
        using var progress = new ListProgress("Reading games", TextWriter.Null, enabled: false);
        Assert.Matches(@"^Reading games\.\.\. \d+s$", progress.Describe());

        progress.PageRead(1);
        Assert.Matches(@"^Reading games\.\.\. 1 found, 1 page, \d+s$", progress.Describe());

        // An empty page still counts, so the line keeps moving while pages hold nothing.
        progress.PageRead(0);
        progress.PageRead(99);
        Assert.Matches(@"^Reading games\.\.\. 100 found, 3 pages, \d+s$", progress.Describe());
    }
}
