---
title: Using the XBOX CLI from agents
description: How an AI agent runs the XBOX CLI, from signing in and choosing a target to reading output and getting approval before it changes anything.
---

# Using the XBOX CLI from agents

The XBOX Developer CLI (`xbox`) is built to be run by AI agents and scripts as well as by people. Its
commands take explicit arguments, its data commands write JSON, and its errors go to stderr
with a nonzero exit code. This page is for agents, and for the people who set them up: how to
sign in, choose what a command acts on, read its output, and find the right API, and what to
ask before changing anything.

## Before changing anything

The signed-in user usually has admin rights on production titles and sellers, and PlayFab
doesn't stop a mistaken call. [Safety and destructive operations](safety.md) describes what
the CLI enforces. The rest is up to the agent:

- Treat a PlayFab operation as read-only only if its name starts with `Get`, `List`, or
  `Search`. Treat every other operation, and every `xbox` command that creates, changes, or
  deletes something, as a production change.
- Before a production change, show the user the command and say in one sentence what it
  changes and on which title or seller, including whether that target comes from saved
  context. Run it only after they say yes, even if your runtime would run `xbox` without
  asking. A yes covers only the calls you showed.
- Pass the title or seller as a flag on every production change. Saved context is shared by
  every session of the OS user, so it can change between the user's yes and your call.
- Ask before `xbox pc sandbox set`, which switches the XBOX sandbox for every user and game
  on the PC and restarts XBOX services, and before `xbox pc sandbox launch-apps`, which
  closes the Microsoft Store and XBOX app.
- Don't add `--confirm` (or `--yes`) in place of asking. It's a backstop for scripts and
  accidents, and the CLI can't tell who added it.
- If a production change fails and its outcome is unknown, it may have been applied. Read
  the affected state before retrying. See
  [An interrupted write reports that its outcome is unknown](safety.md#an-interrupted-write-reports-that-its-outcome-is-unknown).
- Leave saved context alone unless the user asks you to change it. `xbox context set`,
  `xbox context unset`, and `xbox context profile delete` change the defaults for every
  session.

## Signing in

```bash
xbox auth login                          # once, in the system browser
xbox auth status --output json-compact   # {"signedIn":true,"username":"...",...}
```

`auth status` exits with code 0 even when nobody is signed in, and reports
`{"signedIn":false}`, so an agent can check for credentials and branch on the result. A
configuration or token cache that can't be read, and in a service-principal mode
(`XBOX_AUTHENTICATION__MODE`) invalid settings, a selected profile, or a token acquisition
failure, instead exit with code 1 and write the error JSON to stderr, leaving stdout empty.
PlayFab calls fail unless the signed-in user has Admin permission on the title. See
[Authentication](authentication.md).

## Choosing the title or seller

A command takes its PlayFab title from the first of these that's set:

1. A flag on the command, which is the best choice for agents:
   `xbox --playfab-title-id <TITLE_ID> playfab api /Admin/GetTitleData`
2. An environment variable, for the shell and the processes it starts:
   `$env:XBOX_PLAYFAB_TITLE_ID = "<TITLE_ID>"` in PowerShell, or
   `export XBOX_PLAYFAB_TITLE_ID=<TITLE_ID>` in bash or zsh.
3. A default saved with [`xbox context set`](commands/context.md), in the `default` profile
   or in the profile that `--profile <name>` or `XBOX_PROFILE` selects.

The seller works the same way, with `XBOX_SELLER_ID`. For sandbox IDs, only
`xbl configuration get`, `xbl publish start`, and `xbl publish status` read
`XBOX_SANDBOX_ID` or the saved `sandbox-id` value. `game create`,
`test-account batch create`, and the `xbl document` commands also accept `--sandbox-id`,
but don't read it from the environment or saved context; pass the flag explicitly.

Run `xbox context get --output json-compact` to see what's saved. A command that changes
something writes each value it didn't get from its own command line to stderr, such as
`Using --playfab-title-id ABC123 from saved context.`, and `--verbose` does the same for
any command. Confirm the title with the user once before calling anything substantive.

## Reading output

Combine `--data-only` and `--output json-compact` to get only the payload, on a single line:

```bash
xbox --playfab-title-id ABC123 --data-only --output json-compact playfab api /Admin/GetTitleData
```

```json
{"Data":{"MyKey":"my-value"}}
```

| Option | Effect |
|---|---|
| `--data-only` | Returns the `data` payload without the `{code,status,data}` envelope |
| `--output json-compact` | Writes single-line JSON, for piping into `jq` or another tool |

When a PlayFab call fails, the CLI writes the error JSON to stderr and exits with code 1.
With `--data-only`, stdout is empty on an error, so check both streams.

`playfab file upload`, `download`, and `delete` are file operations, not data commands. They
write nothing to stdout, even on success, and report what they did on stderr in messages that
may change. Check their exit code, not their output. See
[Files](commands/playfab/file.md#output).

Help marks experimental commands, whose interface may change based on feedback. See
[Experimental commands](cli-command-lifecycle.md).

### Rate limits

When an XBOX service request returns HTTP 429 with a `Retry-After` header, the error says how
many seconds to wait, rounded up to a whole second with a minimum of 1. Structured errors
from XBOX services also include that wait as `retryAfterSeconds`, even when the response
body is empty or unreadable. Commands that normally print plain text errors, such as
`seller list` and `game list`, keep that format and include the wait in the message.

A 429 with error code `rate_limited` means you sent too many requests in a short time; the
error says so before the wait. If a script runs these commands, slow it down. Other 429s mean
a service is throttling requests.

Without a header, no wait duration is invented. A single request is not retried
automatically; commands that explicitly wait for a job keep their documented polling
behavior. Before retrying a change, follow the approval and unknown outcome rules above.

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
  for operations that need no credential, such as player login calls. Some operations also
  list their allowed entity token types in their description.
- The [PlayFab REST API reference](https://learn.microsoft.com/rest/api/playfab/) covers the
  same operations for people.

The CLI calls with the user's Entra ID credentials in place of a title secret key or title
entity token, so operations that need a player `SessionTicket`, or accept only player or
game-server entity tokens, aren't authorized. When a call fails, its error response usually names what's wrong, such as
`InvalidParameter` or `MissingParameter`. See [PlayFab API integration](playfab-api.md) for
more.

In Git Bash on Windows, set `MSYS_NO_PATHCONV=1` for the command, because Git Bash otherwise
converts the API path into a Windows path before the CLI receives it. See
[Running from Git Bash on Windows](commands/playfab/api.md#running-from-git-bash-on-windows).

## Keeping secrets out of logs

- When a request body may hold secrets, pass it with `--body-file` rather than `--body`, so
  it stays out of shell history and process listings. `playfab mps certificate upload` and
  `playfab mps secret upload` refuse `--body`, because their body always holds one.
- Keep secrets out of configuration files and environment variables. `ClientId`, `TenantId`,
  and OAuth scopes are identifiers, not secrets.
- PlayFab responses can hold player, inventory, or economy data. Redacting, retaining, and
  controlling access to whatever an agent logs is up to the agent's owner.

## See also

- [Commands](commands.md): the full command reference
- [Safety and destructive operations](safety.md): what the CLI enforces, and what it can't
