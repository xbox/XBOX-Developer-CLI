// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.CommandLine;

namespace XboxDevCLI.Commands;

/// <summary>The table layouts of <c>xbox playtest</c>.</summary>
internal static class PlaytestTableViews
{
    public static TableView List { get; } = new(
        "playtests",
        [
            new TableColumn("PLAYTEST ID", "playtestId", 40),
            new TableColumn("NAME", "name", 40),
            new TableColumn("STATUS", "status", 16),
            new TableColumn("TYPE", "type", 12),
            new TableColumn("START", "startDate", 25),
            new TableColumn("END", "endDate", 25),
        ]);

    public static TableView Fields { get; } = new(
        "fields",
        [
            new TableColumn("FIELD", "field", 40),
            new TableColumn("VALUE", "value", 80),
        ]);

    public static TableView Diff { get; } = new(
        "fields",
        [
            new TableColumn("FIELD", "field", 40),
            new TableColumn("CURRENT", "current", 40),
            new TableColumn("COMPARED", "compared", 40),
        ]);
}
