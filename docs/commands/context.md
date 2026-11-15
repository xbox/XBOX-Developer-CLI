---
title: Context
description: Save default values, such as the seller and PlayFab title, for later commands, in named profiles if you need more than one set.
---

# Context

`xbox context` saves default values for the seller, PlayFab title, XBOX title, and sandbox, so
you don't have to pass them to every command. This page also describes where a command gets
these values when you don't pass them. Context is local: these commands change nothing in
Partner Center or PlayFab.

## Commands

| Command | Description |
| --- | --- |
| `context get` | Show the saved values. |
| `context set` | Save one value. |
| `context unset` | Remove saved values. |
| `context key list` | List the keys you can save and the commands that use each. Needs no sign-in. |
| `context profile list` | List your profiles, starting with `default`. |
| `context profile delete` | Delete a named profile and every value saved in it. |

## Keys

A key is the name of the option it fills in:

| Key | Option | Variable | Used by |
| --- | --- | --- | --- |
| `playfab-title-id` | `--playfab-title-id` | `XBOX_PLAYFAB_TITLE_ID` | [`playfab`](playfab.md) commands |
| `sandbox-id` | `--sandbox-id` | `XBOX_SANDBOX_ID` | [`xbl`](xbl.md) `config status` and `config publish` |
| `seller-id` | `--seller-id` | `XBOX_SELLER_ID` | [`audience`](audience.md), [`game`](game.md), [`package`](package.md), [`playtest`](playtest.md), [`sandbox`](sandbox.md), [`test-account`](test-account.md), and [`xbl`](xbl.md) commands |
| `title-id` | `--title-id` | `XBOX_TITLE_ID` | [`xbl`](xbl.md#title-targeting) `config list`, `config status`, `config publish`, and sandbox-scoped `config download` and `config save` |

No other option takes a saved value or a variable.

`title-id` is an XBOX title ID in hexadecimal, such as `1234ABCD`, not a PlayFab title ID. The
`xbl` commands use it to find the title's service configuration identifier (SCID), and ignore it
when you pass `--scid`. See [Title targeting](xbl.md#title-targeting).

## How commands use saved values

When a command takes one of these options, it uses the first value it finds:

1. The option on the command line, such as `--seller-id`.
2. The option's `XBOX_` variable, such as `XBOX_SELLER_ID`. A blank variable counts as unset.
3. The value saved with `context set` in the selected [profile](#profiles).

```bash
xbox context set playfab-title-id ABC123
xbox playfab title-data get                                # uses ABC123
XBOX_PLAYFAB_TITLE_ID=DEF456 xbox playfab title-data get   # uses DEF456
xbox --playfab-title-id GHI789 playfab title-data get      # uses GHI789
```

A value from a variable or saved context is checked like a typed one, and an error names where it
came from, such as `XBOX_SELLER_ID: --seller-id must be a positive numeric value.` If nothing
supplies a value that a command needs, it says how to set one. Commands where the seller is
optional, `xbl` and `test-account`, use your only authorized seller.

A command that changes something writes each value it filled in, and where it came from, to
stderr, even with `--confirm`. With `--verbose`, every command does:

```text
Using --playfab-title-id ABC123 from saved context.
```

[Known issues](../known-issues.md) describes two problems that make `game` and `sandbox` commands
use a different seller from the one chosen this way.

## Profiles

A profile is a named set of saved values, such as one for a test title and one for production.
Commands use the profile `--profile` names, else the one `XBOX_PROFILE` names, else `default`.

```bash
xbox --profile test context set playfab-title-id TEST1   # creates "test"
xbox --profile test playfab title-data get               # uses TEST1
xbox playfab title-data get                              # uses the default profile
```

To use a profile for every command in a shell, set `XBOX_PROFILE`:

```bash
export XBOX_PROFILE=test
```

To list your profiles:

```bash
xbox context profile list
```

To delete a profile and every value saved in it:

```bash
xbox context profile delete test
```

- Saving the first value to a profile creates it. Names are letters, digits, and hyphens, up to
  64 characters, and aren't case-sensitive.
- A profile that doesn't exist, such as a misspelled one, fails rather than falling back to
  `default`.
- A profile doesn't borrow values from `default`.
- `XBOX_` variables still win over a profile. When one wins over the profile `--profile` names,
  the command says so, even if it changes nothing:

  ```text
  Using --playfab-title-id PROD1 from XBOX_PLAYFAB_TITLE_ID, not profile test.
  ```

- The CLI doesn't remember a profile between commands, so choosing one never changes what
  another terminal or agent uses.

## Where values are saved

Saved values are in `context.json` in the CLI's state directory:

| OS | Directory |
| --- | --- |
| Windows | `%LOCALAPPDATA%\XboxDevCLI` |
| macOS | `~/Library/Application Support/XboxDevCLI` |
| Linux | `$XDG_DATA_HOME/XboxDevCLI`, by default `~/.local/share/XboxDevCLI` |

Each value belongs to the account that saved it, so another account doesn't pick it up.
`auth logout` keeps saved values, and signing back in as the same account restores them.

## Arguments and options

| Argument or option | Commands | Description |
| --- | --- | --- |
| `<key> <value>` | `set` | The key to save and its value. The value must be valid for the key's option: `seller-id` is a positive number (see [`xbox seller list`](seller.md)), and `playfab-title-id` is letters and digits. |
| `<key>...` | `unset` | Keys to remove. Removing a key that isn't saved succeeds. |
| `<name>` | `profile delete` | The profile to delete. `default` can't be deleted. |
| `--profile <name>` | All | Use this profile instead of `XBOX_PROFILE`'s or `default`. A [global option](../commands.md). |
| `--all` | `unset` | Remove every value saved in the selected profile. Can't be combined with key names. |
| `--base-url <url>` | `get`, `set`, `unset` | Overrides the `XboxPartner:BaseUrl` setting. Saved seller values belong to the endpoint they were saved for. |
| `--output <format>` | All | `json` (default), `json-compact`, or `table`. |

## Behavior

`get` returns the signed-in `account`, the `profile`, and a `values` array of `key` and `value`,
with `null` for a key that isn't saved. It shows what's saved, not what a command would use; run
the command with `--verbose` to see that. Signed out, `get` shows no values and says how to sign
in.

`set`, `unset`, and the `profile` commands need a signed-in account. `set` doesn't check the
value with the service, so a seller you can't use fails on the first command that sends it.

A service principal has no saved context. In a service-principal mode, commands that sign in
refuse `--profile` and `XBOX_PROFILE`, and every `context` command except `key list` fails. Pass
each value with its option or `XBOX_` variable instead. See
[Automation (service principal) authentication](../authentication.md#automation-service-principal-authentication).

## Values that don't come from the command line

Saved context is shared by every terminal and agent your OS user runs, and environment variables
don't appear on the command line. So besides naming each value it filled in:

- An operation whose target is the context value itself must have it on the command line.
  `/Admin/DeleteTitle` deletes the title, and `playfab api-policy set --overwrite` replaces its
  whole API access policy, so each refuses a title from saved context or a variable, and prints
  the command to run instead:

  ```text
  $ xbox playfab api /Admin/DeleteTitle --confirm
  Using --playfab-title-id ABC123 from saved context.
  '/Admin/DeleteTitle' acts on playfab-title-id ABC123, which comes from saved context.
  The target of a destructive operation must be named on the command line. To proceed, run:
    xbox --playfab-title-id ABC123 playfab api /Admin/DeleteTitle --confirm
  ```

  The printed command leaves out an inline `--body` or `--statements` value, because a request
  body can hold secrets. Put the value back in place of `<body>` or `<statements>`.

- If another account signs in while a command that uses saved values runs, for example while it
  waits for confirmation, the command stops before it sends anything. Run it again.

See [Safety and destructive operations](../safety.md) for the confirmation rules.

## Table output

Every context command accepts `--output table`:

| Command | Columns |
| --- | --- |
| `context get`, `set`, `unset` | `KEY`, `VALUE` |
| `context key list` | `KEY`, `USED BY` |
| `context profile list`, `profile delete` | `NAME` |

## Examples

List the keys you can save:

```bash
xbox context key list --output table
```

Save a seller and a PlayFab title, and check what's saved:

```bash
xbox context set seller-id 1234567
xbox context set playfab-title-id ABC123
xbox context get --output table
```

Run commands that use the saved values, and see where the title came from:

```bash
xbox game list
xbox --verbose playfab title-data get
```

Remove one saved value:

```bash
xbox context unset playfab-title-id
```

Remove every value saved in the selected profile:

```bash
xbox context unset --all
```

See [Configuration](../configuration.md) for settings, which context doesn't change.
