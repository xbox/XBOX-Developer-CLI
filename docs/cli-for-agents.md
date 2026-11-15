---
title: Using the XBOX CLI from agents
description: How an AI agent runs the XBOX CLI, from signing in and choosing a target to reading output and getting approval before it changes anything.
---

# Using the XBOX CLI from agents

The XBOX CLI (`xbox`) is built to be run by AI agents and scripts as well as by people. Its
commands take explicit arguments, its data commands write JSON, and its errors go to stderr
with a nonzero exit code. This page is for agents, and for the people who set them up: how to
sign in, choose what a command acts on, read its output, and find the right API, and what to
ask before changing anything.

## Before changing anything

The signed-in user usually has admin rights on production titles and sellers, and nothing on
the server side stops a mistaken call. [Safety and destructive operations](safety.md) lists
what the CLI asks about and what it doesn't. The rest is up to you:

- Treat a PlayFab operation as read-only only if its name starts with `Get`, `List`, or
  `Search`. Treat every other operation, and every `xbox` command that creates, changes, or
  deletes something, as a production change.
- Before a production change, show the user the command and say in one sentence what it
  changes and on which title or seller, including whether that target comes from saved
  context. Run it only after they say yes, even if your runtime would run `xbox` without
  asking. A yes covers only the calls you showed.
- Pass the title or seller as a flag on every production change. Saved context is shared by
  every session of the OS user, so it can change between the user's yes and your call.
- Ask before `xbox pc sandbox set`, which switches the sandbox for everyone on the PC, and
  before `xbox pc sandbox launch-apps`, which closes the Microsoft Store and XBOX app.
- Don't add `--confirm` (or `--yes`) in place of asking. The CLI can't tell who added it.
- Before asking to publish to RETAIL with `xbox xbl config publish`, run it with
  `--validate-only` and show the user the achievements it lists. Then publish with
  `--configset` set to the `SourceVersion` in its JSON output. Achievements in RETAIL can't be
  removed; see
  [Achievements a RETAIL publish adds](commands/xbl.md#achievements-a-retail-publish-adds).
- If a production change fails and its outcome is unknown, it might have been applied. Read
  the affected state before retrying; see
  [An interrupted write reports that its outcome is unknown](safety.md#an-interrupted-write-reports-that-its-outcome-is-unknown).
- Leave saved context alone unless the user asks you to change it. `xbox context set`,
  `xbox context unset`, and `xbox context profile delete` change the defaults for every
  session.

## Signing in

```bash
xbox auth login                          # once, in the system browser
xbox auth status --output json-compact   # {"signedIn":true,"username":"...",...}
```

`auth status` exits with code 0 and reports `{"signedIn":false}` when nobody is signed in, so
branch on `signedIn`. A nonzero exit means it couldn't check, and the error is on stderr.
PlayFab calls fail unless the signed-in user has Admin permission on the title. See
[Authentication](authentication.md).

## Choosing the title or seller

Pass the title or seller as a flag, such as `--playfab-title-id ABC123`. Without one, a
command uses an `XBOX_` environment variable or a value saved with `xbox context set`; see
[Context](commands/context.md) for which wins. Run `xbox context get --output json-compact` to
see what's saved, and confirm the title with the user once before calling anything
substantive.

## Reading output

Combine `--data-only` and `--output json-compact` to get only the payload, on a single line:

```bash
xbox --playfab-title-id ABC123 --data-only --output json-compact playfab api /Admin/GetTitleData
```

```json
{"Data":{"MyKey":"my-value"}}
```

A failed call exits nonzero and writes its error to stderr. With `--data-only`, stdout is
empty on an error, so check the exit code before parsing stdout.

`playfab file upload`, `download`, and `delete` write nothing to stdout, and their stderr
messages might change, so check their exit code. See [Files](commands/playfab/file.md#output).

Help marks **Experimental** commands, whose interface might change. See
[Experimental commands](cli-command-lifecycle.md).

### Rate limits

When an XBOX service answers HTTP 429, wait before trying again; the CLI doesn't retry for
you. When the service specifies a wait, the error says how many seconds, and structured errors
also carry it as `retryAfterSeconds`. A 429 with error code `rate_limited` means you sent too
many requests in a short time, so slow down.

## Finding the right PlayFab API

`xbox playfab api <api-path>` calls any PlayFab API operation. The path is
`/<Prefix>/<Operation>`, such as `/MultiplayerServer/ListBuildSummariesV2`, and the request
body is the operation's JSON request object. PlayFab documents both:

- The [PlayFab Swagger specs](https://github.com/PlayFab/API_Specs/tree/master/Swagger/PlayFab)
  are machine-readable. Each file lists its operations by path, with their request and
  response schemas and required fields. The files are named for PlayFab's API groups, not
  for the path's first segment, so search the files' `paths` for the operation: `/Catalog`
  and `/Inventory` are in `Economy.swagger.json`, and `/Match`, `/Lobby`, and
  `/MultiplayerServer` are in `Multiplayer.swagger.json`. Each operation's `security` field
  names the credential it accepts: `SecretKey`, `EntityToken`, `SessionTicket`, or `None`
  for operations that need no credential, such as player sign-in calls. Some operations also
  list their allowed entity token types in their description.
- The [PlayFab REST API reference](https://learn.microsoft.com/rest/api/playfab/) covers the
  same operations for people.

The CLI calls with the user's Microsoft Entra ID credentials in place of a title secret key or title
entity token, so operations that need a player `SessionTicket`, or accept only player or
game-server entity tokens, aren't authorized. When a call fails, its error response usually names what's wrong, such as
`InvalidParameter` or `MissingParameter`. See [PlayFab API integration](playfab-api.md) for
more.

In Git Bash on Windows, set `MSYS_NO_PATHCONV=1` for the command, because Git Bash otherwise
converts the API path into a Windows path before the CLI receives it. See
[Running from Git Bash on Windows](commands/playfab/api.md#running-from-git-bash-on-windows).

## Keeping secrets out of logs

- When a request body might hold secrets, pass it with `--body-file` rather than `--body`, so
  it stays out of shell history and process listings. `playfab mps certificate upload` and
  `playfab mps secret upload` refuse `--body`, because their body always holds one.
- Keep secrets out of configuration files and environment variables. `ClientId`, `TenantId`,
  and OAuth scopes are identifiers, not secrets.
- PlayFab responses can hold player, inventory, or economy data. Redacting, retaining, and
  controlling access to whatever an agent logs is up to the agent's owner.

## See also

- [Commands](commands.md): the full command reference
- [Safety and destructive operations](safety.md): what asks first, and what does more than its name says
