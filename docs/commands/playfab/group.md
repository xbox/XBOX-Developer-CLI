---
title: Groups
description: Manage PlayFab entity groups and their roles, members, invitations, applications, and blocks.
---

# Groups

The `playfab group` command family is Experimental. Its CLI interface might change
or be removed based on feedback.

Manage PlayFab entity groups and their roles, members, invitations, applications, and
blocks, and list the groups an entity belongs to.

Most commands target a group with `--group-id`; `group get` can use `--group-name`
instead. Pass `--entity-id` and `--entity-type` together, such as `--entity-type
title_player_account`. For a player, pass their title player account ID, not their master
player account ID; [`playfab player account get`](player.md) returns it as
`UserInfo.TitleInfo.TitlePlayerAccount.Id`.

`group delete` and `group role delete` ask first in a terminal, and need `--confirm`
elsewhere. Other commands that change a group, including `group member remove`, run
without asking. See [Safety and destructive operations](../../safety.md).

## Group lifecycle

| Command | PlayFab API path | Options |
| --- | --- | --- |
| `playfab group create` | `/Group/CreateGroup` | `--group-name` required; optional owner `--entity-id`, `--entity-type`. |
| `playfab group get` | `/Group/GetGroup` | `--group-id` or `--group-name`. |
| `playfab group update` | `/Group/UpdateGroup` | `--group-id` required; optional `--group-name`, `--admin-role-id`, `--member-role-id`, `--expected-profile-version`. |
| `playfab group delete` | `/Group/DeleteGroup` | `--group-id` required. |

## Group members

| Command | PlayFab API path | Options |
| --- | --- | --- |
| `playfab group member add` | `/Group/AddMembers` | `--group-id`, `--member-ids`, `--member-type` required; optional `--role-id`. |
| `playfab group member remove` | `/Group/RemoveMembers` | `--group-id`, `--member-ids`, `--member-type` required; optional `--role-id`. |
| `playfab group member change-role` | `/Group/ChangeMemberRole` | `--group-id`, `--member-ids`, `--member-type`, `--origin-role-id`, and `--destination-role-id` required. |
| `playfab group member list` | `/Group/ListGroupMembers` | `--group-id` required. |
| `playfab group member is-member` | `/Group/IsMember` | `--group-id`, `--entity-id`, and `--entity-type` required; optional `--role-id`. Only reads. |

## Group roles

| Command | PlayFab API path | Options |
| --- | --- | --- |
| `playfab group role create` | `/Group/CreateRole` | `--group-id`, `--role-id`, and `--role-name` required. |
| `playfab group role update` | `/Group/UpdateRole` | `--group-id`, `--role-id`, and `--role-name` required; optional `--expected-profile-version`. |
| `playfab group role delete` | `/Group/DeleteRole` | `--group-id` and `--role-id` required. |

## Group invitations

| Command | PlayFab API path | Options |
| --- | --- | --- |
| `playfab group invite send` | `/Group/InviteToGroup` | `--group-id`, `--entity-id`, and `--entity-type` required; optional `--role-id`, `--auto-accept`. |
| `playfab group invite accept` | `/Group/AcceptGroupInvitation` | `--group-id` required; optional accepting `--entity-id`, `--entity-type`. |
| `playfab group invite remove` | `/Group/RemoveGroupInvitation` | `--group-id`, `--entity-id`, and `--entity-type` required. |
| `playfab group invite list` | `/Group/ListGroupInvitations` | `--group-id` required. |

`--auto-accept` defaults to `true` for `invite send`: when the entity has already applied
to the group, PlayFab accepts the application instead of sending an invitation.

## Group applications

| Command | PlayFab API path | Options |
| --- | --- | --- |
| `playfab group application apply` | `/Group/ApplyToGroup` | `--group-id` required; optional applicant `--entity-id`, `--entity-type`, `--auto-accept`. |
| `playfab group application accept` | `/Group/AcceptGroupApplication` | `--group-id` required; optional applicant `--entity-id`, `--entity-type`. |
| `playfab group application remove` | `/Group/RemoveGroupApplication` | `--group-id`, `--entity-id`, and `--entity-type` required. |
| `playfab group application list` | `/Group/ListGroupApplications` | `--group-id` required. |

`--auto-accept` defaults to `true` for `application apply`: when the group has already
invited the entity, PlayFab accepts the invitation instead of creating an application.

## Group blocks

| Command | PlayFab API path | Options |
| --- | --- | --- |
| `playfab group block add` | `/Group/BlockEntity` | `--group-id`, `--entity-id`, and `--entity-type` required. |
| `playfab group block remove` | `/Group/UnblockEntity` | `--group-id`, `--entity-id`, and `--entity-type` required. |
| `playfab group block list` | `/Group/ListGroupBlocks` | `--group-id` required. |

## Entity memberships

| Command | PlayFab API path | Options |
| --- | --- | --- |
| `playfab group membership list` | `/Group/ListMembership` | Optional `--entity-id`, `--entity-type`; omit both to target the calling entity. |
| `playfab group membership opportunity list` | `/Group/ListMembershipOpportunities` | Optional `--entity-id`, `--entity-type`; omit both to target the calling entity. |

## Table output

These commands accept `--output table`; the others don't. See
[Table output](../../commands.md#table-output).

| Command | Columns |
| --- | --- |
| `playfab group member list` | `MEMBER ID`, `MEMBER TYPE`, `ROLE ID` |
| `playfab group invite list` | `INVITED ENTITY`, `ROLE ID`, `EXPIRES` |
| `playfab group application list` | `ENTITY`, `EXPIRES` |
| `playfab group block list` | `BLOCKED ENTITY`, `ENTITY TYPE` |
| `playfab group membership list` | `GROUP ID`, `GROUP NAME` |
| `playfab group membership opportunity list` | `TYPE`, `GROUP ID`, `ROLE ID`, `EXPIRES` |

`member list` has a row for each member in each role, so a member in two roles has two
rows, and a role with no members has one row with `-` for the member.
`membership opportunity list` shows invitations and applications in one table: `TYPE` is
`invitation` or `application`, and `ROLE ID` is `-` for an application.

## Examples

Read a group, list its members, check whether a player belongs to it, and list its
outstanding invitations:

```bash
xbox --playfab-title-id ABC123 playfab group get --group-id GROUP123
xbox --playfab-title-id ABC123 playfab group member list --group-id GROUP123 --data-only --output json-compact
xbox --playfab-title-id ABC123 playfab group member is-member --group-id GROUP123 --entity-id TITLEPLAYER123 --entity-type title_player_account
xbox --playfab-title-id ABC123 playfab group invite list --group-id GROUP123 --output table
```

Invite a player to the group:

```bash
xbox --playfab-title-id ABC123 playfab group invite send --group-id GROUP123 --entity-id TITLEPLAYER123 --entity-type title_player_account
```

Delete the group:

```bash
xbox --playfab-title-id ABC123 playfab group delete --group-id GROUP123 --confirm
```
