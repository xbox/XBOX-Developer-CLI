---
title: Known issues
description: Problems and unfinished features in the latest release of the XBOX CLI, and how to work around them.
---

# Known issues

This page lists the known problems in the latest release of the XBOX CLI, including
unfinished features. If your problem is listed here, you don't need to report it.

Only the latest release is supported. [`xbox upgrade`](commands/upgrade.md) tells you whether
a newer one is available. An entry marked **Fixed** is fixed in the latest release, and it's
removed in the next one.

## If your problem isn't listed

1. Check that you're running the latest release with [`xbox upgrade`](commands/upgrade.md).
2. Search the [existing issues](https://github.com/xbox/cli/issues).
3. If it hasn't been reported, report it with [`xbox feedback`](commands/feedback.md), which
   opens a bug report with your environment details filled in. Issues are public, so leave
   out anything you wouldn't share publicly.

## Updating the CLI

### `xbox upgrade` fails with HTTP 301

**Fixed**

The CLI's public repository moved, and [`xbox upgrade`](commands/upgrade.md) in v0.2.1 fails
with `HTTP 301: GitHub release metadata is unavailable` every time.

**Workaround:** download the latest release by following [Installation](installation.md).

## Signing in and permissions

### Some commands don't work yet with a personal Microsoft account

When you sign in with a personal Microsoft account rather than a work or school account,
commands for XBOX services configuration, test accounts, packages, playtests, and audiences
don't work. The XBOX services configuration and test account commands fail with HTTP 502,
reported as the service being unavailable, but retrying doesn't help.

**Workaround:** join your seller in Partner Center with a work or school account, and sign
in to the CLI with that account.

### `xbox game`, `xbox xbl`, and similar commands don't accept service principals yet

A service principal can sign in, but the `audience`, `game`, `package`, `playtest`, `sandbox`,
`seller`, `test-account`, and `xbl` commands fail with an authorization error when it runs them,
because the service they call doesn't accept service principals yet.

**Workaround:** run those commands as a signed-in user. `xbox playfab` commands work with a
service principal once a person grants it access with
[`xbox playfab service-principal set`](commands/playfab/service-principal.md). See
[Automation (service principal) authentication](authentication.md#automation-service-principal-authentication).

### Commands are denied for some sellers when you belong to more than one

If you belong to more than one Partner Center seller, commands for XBOX services
configuration, test accounts, packages, playtests, and audiences can be denied for every
seller except the one you last selected in Partner Center.

**Workaround:** select the seller you want in Partner Center, then run the command again.

### `xbox seller list` sometimes leaves out one of your sellers

If you belong to more than one Partner Center seller, [`xbox seller list`](commands/seller.md)
sometimes leaves one of them out without an error, and the next run might list it. While it's
missing, commands for that seller, such as [`xbox audience list`](commands/audience.md), can
be denied, and selecting it in Partner Center isn't known to help.

### `xbox game` commands ignore `--seller-id`

[`xbox game list`, `xbox game get`, and `xbox game create`](commands/game.md) use the seller you
last selected in Partner Center, not the one you pass with `--seller-id` or save in
[context](commands/context.md), so `game create` can create the game in another seller. A
seller ID that isn't yours, such as a mistyped one, doesn't make them fail; they return your
own games.

**Workaround:** select the seller you want in Partner Center, then check that `xbox game list`
shows its games before you create one. The change can take up to 15 minutes to reach the CLI.

### `xbox sandbox` commands show only global sandboxes

When the seller you pass with `--seller-id` or save in [context](commands/context.md) isn't the
one you last selected in Partner Center,
[`xbox sandbox list` and `xbox sandbox get`](commands/sandbox.md) find only the `CERT`,
`CERT.DEBUG`, and `RETAIL` sandboxes, without failing. `xbox sandbox create` creates the
sandbox in the seller you last selected, and if that's another seller, it exits `4` because it
can't read the sandbox back. Don't create it again.

**Workaround:** select the seller you want in Partner Center, then run the command again with
that seller. The change can take up to 15 minutes to reach the CLI.

## Testing with playtests and audiences

### `xbox audience list` can time out

[`xbox audience list`](commands/audience.md) can take long enough that the request times
out. `--name` doesn't help, because it filters the list after every audience is read.

## XBOX services configuration

### Some downloaded XML documents declare the wrong encoding

Some published XML documents that
[`xbox xbl config download --view published`](commands/xbl.md) saves, such as
`mpsdtitleinfo.xml` and `titlename-localization.xml`, declare `encoding="utf-16"` but are
encoded as UTF-8, so an XML reader that trusts the declaration fails to load them.

**Workaround:** decode the file as UTF-8 before you parse it.

## PlayFab

### `xbox playfab api /Admin/UpdatePolicy` can overwrite the policy without confirmation

With `"OverwritePolicy": true` in the request body, this call replaces the title's whole API
access policy without the safeguards of `xbox playfab api-policy set --overwrite`: it doesn't
require `--confirm`, and it can use the title from saved context or `XBOX_PLAYFAB_TITLE_ID`.
A wrong policy can lock every player out of the title.

**Workaround:** use [`xbox playfab api-policy set --overwrite`](commands/playfab/api-policy.md)
instead, which requires `--confirm` and the title passed with `--playfab-title-id`.

### `playfab title-data delete` and `playfab player-data delete` don't ask first

Other commands that delete something ask before they run, or refuse without `--confirm`. These
two delete the keys you name immediately.

### `--keys` and `--member-ids` take comma-separated lists

`--keys` on `xbox playfab title-data` and `xbox playfab player-data`, and `--member-ids` on
[`xbox playfab group member`](commands/playfab/group.md), take comma-separated lists. They'll
change to repeated options, so scripts that use them will need updating.

Until then, `--keys` can't name a key that contains a comma. To read one, pass it with
`--key`, which takes the name whole, but `delete` has only `--keys`.

**Workaround:** to delete the key, call the API that `delete` uses with
[`xbox playfab api`](commands/playfab/api.md). For title data, set the key's value to `null`,
with `/Admin/SetTitleInternalData` instead for internal title data:

```bash
xbox --playfab-title-id ABC123 playfab api /Admin/SetTitleData --body '{"Key":"Level,Bonus","Value":null}'
```

For player data, list the key in `KeysToRemove`, using the update API for the data's
visibility from [Player data](commands/playfab/player-data.md):

```bash
xbox --playfab-title-id ABC123 playfab api /Admin/UpdateUserData --body '{"PlayFabId":"PLAYER123","KeysToRemove":["Level,Bonus"]}'
```

## Scripts and agents

### Most commands exit 1 when it's unknown whether a change went through

A few commands exit `3` when they can't tell whether a change was made, such as when the
request times out after it's sent, and their pages say so. Every other command, including
every PlayFab command, exits `1` for every failure, so a script can't tell this case from a
change that wasn't made.

**Workaround:** when a command that changes something fails, check the current state before
running it again.
