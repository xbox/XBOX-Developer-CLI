---
title: Experiments
description: Read a title's A/B experiments, scorecards, treatment assignments, and exclusion groups.
---

# Experiments

The `playfab experiment` command family is Experimental. Its CLI interface might change
or be removed based on feedback.

Read a title's PlayFab Experimentation A/B experiments. These commands only read; to create,
start, or stop an experiment, call its API with [`playfab api`](api.md).

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
| `experiment treatment get` | `--entity-id` and `--entity-type` (both required). For a player, pass their title player account ID with `--entity-type title_player_account`, not their master player account ID; [`playfab player account get`](player.md) returns it as `UserInfo.TitleInfo.TitlePlayerAccount.Id`. |
| `experiment exclusion-group list` | None. |

```bash
xbox --playfab-title-id ABC123 playfab experiment list
xbox --playfab-title-id ABC123 playfab experiment scorecard get --experiment-id 11111111-2222-3333-4444-555555555555
xbox --playfab-title-id ABC123 playfab experiment treatment get --entity-id TITLEPLAYER123 --entity-type title_player_account
xbox --playfab-title-id ABC123 playfab experiment exclusion-group list
```

## Table output

These commands accept `--output table`; the other two don't. See
[Table output](../../commands.md#table-output).

| Command | Columns |
| --- | --- |
| `playfab experiment list` | `ID`, `NAME`, `STATE`, `TYPE` |
| `playfab experiment exclusion-group list` | `GROUP ID`, `NAME`, `DESCRIPTION` |

`STATE` is `New`, `Started`, `Stopped`, or `Deleted`, and `TYPE` is `Active` or
`Snapshot`. An experiment's variants, segment, and schedule are only in the JSON output.

```bash
xbox --playfab-title-id ABC123 playfab experiment list --output table
```

```text
ID                                    NAME            STATE    TYPE
------------------------------------  --------------  -------  --------
11111111-2222-3333-4444-555555555555  weapon-balance  Started  Active
22222222-3333-4444-5555-666666666666  store-layout    New      Snapshot
```
