---
title: Experiments
description: Read a title's A/B experiments, scorecards, treatment assignments, and exclusion groups.
---

# Experiments

The `playfab experiment` command family is Experimental. Its CLI interface may change
or be removed based on feedback.

Read-only access to PlayFab Experimentation.

| Command | PlayFab API path | Description |
| --- | --- | --- |
| `playfab experiment list` | `/Experimentation/GetExperiments` | List the title's experiments. |
| `playfab experiment scorecard get` | `/Experimentation/GetLatestScorecard` | Get the latest scorecard for an experiment. |
| `playfab experiment treatment get` | `/Experimentation/GetTreatmentAssignment` | Get treatment assignments for an entity. |
| `playfab experiment exclusion-group list` | `/Experimentation/GetExclusionGroups` | List exclusion groups. |

| Command | Options |
| --- | --- |
| `experiment list` | None. |
| `experiment scorecard get` | `--experiment-id` (required). |
| `experiment treatment get` | `--entity-id` and `--entity-type` (both required). For a player, pass their title player account ID with `--entity-type title_player_account`, not their master player ID; [`xbox playfab player account get`](player.md) returns it as `UserInfo.TitleInfo.TitlePlayerAccount.Id`. |
| `experiment exclusion-group list` | None. |

```bash
xbox --playfab-title-id ABC123 playfab experiment list
xbox --playfab-title-id ABC123 playfab experiment scorecard get --experiment-id 11111111-2222-3333-4444-555555555555
xbox --playfab-title-id ABC123 playfab experiment treatment get --entity-id TITLEPLAYER123 --entity-type title_player_account
xbox --playfab-title-id ABC123 playfab experiment exclusion-group list
```

Every command in this group is a read. The mutating Experimentation operations
(`CreateExperiment`, `UpdateExperiment`, `DeleteExperiment`, `StartExperiment`,
`StopExperiment`) are deliberately not exposed as CLI verbs — starting or
stopping a live experiment changes player-facing behavior. Use `playfab api`
if you need them.

## Table output

`--output table` is opt-in per command. Two `experiment` commands declare a
table view; the other two refuse `table` rather than quietly returning JSON:

```console
$ xbox --playfab-title-id ABC123 playfab experiment scorecard get --experiment-id 11111111-2222-3333-4444-555555555555 --output table
Output format 'table' is not available for this command, because it does not declare a
table view. Omit --output to get this command's default output.
```

Each command's own Help lists its columns under a `TABLE OUTPUT` heading. The
columns are a human convenience and **not a stability contract** — use `json` or
`json-compact` for scripts and agents.

| Command | Columns |
| --- | --- |
| `playfab experiment list` | `ID`, `NAME`, `STATE`, `TYPE` |
| `playfab experiment exclusion-group list` | `GROUP ID`, `NAME`, `DESCRIPTION` |

`STATE` is one of `New`, `Started`, `Stopped`, or `Deleted`; `TYPE` is `Active`
or `Snapshot`. The variants, segment, and scheduling fields of an experiment are
only in the JSON output.

The two `get` commands deliberately have no view, because a flat table cannot
represent their responses without hiding the part you asked for:

| Command | Why |
| --- | --- |
| `playfab experiment scorecard get` | A single record wrapping a `ScorecardDataRows` array. A one-row table of the scorecard header would drop the per-variant analysis, which is the point of the command. |
| `playfab experiment treatment get` | Returns a treatment assignment holding two independent arrays — variables and variants. Showing one silently hides the other. |

`--data-only` has no effect with `--output table`: a table has no response
envelope to strip. A collection view prints `No results.` when the collection is
empty, rather than headers with no rows.

```bash
xbox --playfab-title-id ABC123 playfab experiment list --output table
```

```text
ID                                    NAME            STATE    TYPE
------------------------------------  --------------  -------  --------
11111111-2222-3333-4444-555555555555  weapon-balance  Started  Active
22222222-3333-4444-5555-666666666666  store-layout    New      Snapshot
```

See [Commands](../../commands.md) for the general output rules.
