---
title: Groups
description: Manage PlayFab entity groups and their roles, members, invitations, applications, and blocks.
---

# Groups

The `playfab group` command family is Experimental. Its CLI interface may change
or be removed based on feedback.

Manage PlayFab entity groups, roles, members, invitations, applications, blocks,
and entity memberships. The command name is singular: `playfab group`.

Most commands target a group with `--group-id`. `playfab group get` can instead
use `--group-name`, but `--group-id` and `--group-name` are alternatives. Entity
options come in pairs: when you provide `--entity-id`, also provide
`--entity-type` such as `title_player_account`. For a player, pass their title
player account ID, not their master player ID;
[`xbox playfab player account get`](player.md) returns it as
`UserInfo.TitleInfo.TitlePlayerAccount.Id`.

Mutating commands change production title state and require explicit operator
review. Commands backed by `Delete*` PlayFab operations are hard-gated by
`--confirm` / `--yes` / `-y`; see [Safety and destructive operations](../../safety.md).
The hard gate classifies by the PlayFab operation name, not the CLI verb — which
is why `group member remove` (`/Group/RemoveMembers`) and `group block remove`
(`/Group/UnblockEntity`) mutate production without being gated, while
`group delete` (`/Group/DeleteGroup`) and `group role delete`
(`/Group/DeleteRole`) are. Each leaf command's Help states its own
classification under a `SAFETY` heading and names its API under `LEARN MORE`.

## Table output

`--output table` is opt-in per command. Four `group` commands declare a table
view; every other `group` command refuses `table` rather than quietly returning
JSON. Columns may change between releases, so use `json` or `json-compact` for
scripts.

| Command | Columns |
| --- | --- |
| `playfab group invite list` | `INVITED ENTITY`, `ROLE ID`, `EXPIRES` |
| `playfab group application list` | `ENTITY`, `EXPIRES` |
| `playfab group block list` | `BLOCKED ENTITY`, `ENTITY TYPE` |
| `playfab group membership list` | `GROUP ID`, `GROUP NAME` |

Two list commands deliberately have no view, because a flat table cannot
represent their responses without hiding the part you asked for:

| Command | Why |
| --- | --- |
| `playfab group member list` | Members are nested inside roles. A table of roles would hide the members, which are the point of the command. |
| `playfab group membership opportunity list` | Returns two independent arrays — invitations and applications. Showing one silently hides the other. |

## Group lifecycle

| Command | PlayFab API path | Options | Safety |
| --- | --- | --- | --- |
| `playfab group create` | `/Group/CreateGroup` | `--group-name` required; optional owner `--entity-id`, `--entity-type`. | Mutating. |
| `playfab group get` | `/Group/GetGroup` | `--group-id` or `--group-name`. | Read-only. |
| `playfab group update` | `/Group/UpdateGroup` | `--group-id` required; optional `--group-name`, `--admin-role-id`, `--member-role-id`, `--expected-profile-version`. | Mutating; not hard-gated. |
| `playfab group delete` | `/Group/DeleteGroup` | `--group-id` required. | Destructive and hard-gated by `--confirm` / `--yes` / `-y`. |

## Group members

| Command | PlayFab API path | Options | Safety |
| --- | --- | --- | --- |
| `playfab group member add` | `/Group/AddMembers` | `--group-id`, `--member-ids`, `--member-type` required; optional `--role-id`. | Mutating; not hard-gated. |
| `playfab group member remove` | `/Group/RemoveMembers` | `--group-id`, `--member-ids`, `--member-type` required; optional `--role-id`. | Mutating; not hard-gated. |
| `playfab group member change-role` | `/Group/ChangeMemberRole` | `--group-id`, `--member-ids`, `--member-type`, `--origin-role-id`, and `--destination-role-id` required. | Mutating; not hard-gated. |
| `playfab group member list` | `/Group/ListGroupMembers` | `--group-id` required. | Read-only. |
| `playfab group member is-member` | `/Group/IsMember` | `--group-id`, `--entity-id`, and `--entity-type` required; optional `--role-id`. | Read-only — classified explicitly, because `IsMember` is not named `Get*`/`List*`/`Search*`. |

## Group roles

| Command | PlayFab API path | Options | Safety |
| --- | --- | --- | --- |
| `playfab group role create` | `/Group/CreateRole` | `--group-id`, `--role-id`, and `--role-name` required. | Mutating; not hard-gated. |
| `playfab group role update` | `/Group/UpdateRole` | `--group-id`, `--role-id`, and `--role-name` required; optional `--expected-profile-version`. | Mutating; not hard-gated. |
| `playfab group role delete` | `/Group/DeleteRole` | `--group-id` and `--role-id` required. | Destructive and hard-gated by `--confirm` / `--yes` / `-y`. |

## Group invitations

| Command | PlayFab API path | Options | Safety |
| --- | --- | --- | --- |
| `playfab group invite send` | `/Group/InviteToGroup` | `--group-id`, `--entity-id`, and `--entity-type` required; optional `--role-id`, `--auto-accept`. | Mutating; not hard-gated. |
| `playfab group invite accept` | `/Group/AcceptGroupInvitation` | `--group-id` required; optional accepting `--entity-id`, `--entity-type`. | Mutating; not hard-gated. |
| `playfab group invite remove` | `/Group/RemoveGroupInvitation` | `--group-id`, `--entity-id`, and `--entity-type` required. | Mutating; not hard-gated. |
| `playfab group invite list` | `/Group/ListGroupInvitations` | `--group-id` required. | Read-only. |

`--auto-accept` defaults to `true` for `invite send`, which means an outstanding
application from the same entity is accepted instead of creating a separate
invitation.

## Group applications

| Command | PlayFab API path | Options | Safety |
| --- | --- | --- | --- |
| `playfab group application apply` | `/Group/ApplyToGroup` | `--group-id` required; optional applicant `--entity-id`, `--entity-type`, `--auto-accept`. | Mutating; not hard-gated. |
| `playfab group application accept` | `/Group/AcceptGroupApplication` | `--group-id` required; optional applicant `--entity-id`, `--entity-type`. | Mutating; not hard-gated. |
| `playfab group application remove` | `/Group/RemoveGroupApplication` | `--group-id`, `--entity-id`, and `--entity-type` required. | Mutating; not hard-gated. |
| `playfab group application list` | `/Group/ListGroupApplications` | `--group-id` required. | Read-only. |

`--auto-accept` defaults to `true` for `application apply`, which means an
outstanding invitation from the group is accepted instead of creating a separate
application.

## Group blocks

| Command | PlayFab API path | Options | Safety |
| --- | --- | --- | --- |
| `playfab group block add` | `/Group/BlockEntity` | `--group-id`, `--entity-id`, and `--entity-type` required. | Mutating; not hard-gated. |
| `playfab group block remove` | `/Group/UnblockEntity` | `--group-id`, `--entity-id`, and `--entity-type` required. | Mutating; not hard-gated. |
| `playfab group block list` | `/Group/ListGroupBlocks` | `--group-id` required. | Read-only. |

## Entity memberships

| Command | PlayFab API path | Options | Safety |
| --- | --- | --- | --- |
| `playfab group membership list` | `/Group/ListMembership` | Optional `--entity-id`, `--entity-type`; omit both to target the calling entity. | Read-only. |
| `playfab group membership opportunity list` | `/Group/ListMembershipOpportunities` | Optional `--entity-id`, `--entity-type`; omit both to target the calling entity. | Read-only. |

## Examples

Read a group, list its members, and check whether a player belongs to it:

```bash
xbox --playfab-title-id ABC123 playfab group get --group-id GROUP123
xbox --playfab-title-id ABC123 playfab group member list --group-id GROUP123 --data-only --output json-compact
xbox --playfab-title-id ABC123 playfab group member is-member --group-id GROUP123 --entity-id TITLEPLAYER123 --entity-type title_player_account
```

Invite a player, list outstanding invitations, then delete the group with the
required destructive-operation confirmation:

```bash
xbox --playfab-title-id ABC123 playfab group invite send --group-id GROUP123 --entity-id TITLEPLAYER123 --entity-type title_player_account
xbox --playfab-title-id ABC123 playfab group invite list --group-id GROUP123 --output table
xbox --playfab-title-id ABC123 playfab group delete --group-id GROUP123 --confirm
```

