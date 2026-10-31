---
title: Context
description: Save default values, such as the seller and PlayFab title, for later commands, in named profiles if you need more than one set.
---

# Context

`xbox context` saves default values for options such as `--seller-id` and `--playfab-title-id`,
and shows what is saved. It manages saved values only. Context is local state: these commands
make no network request and change nothing in Partner Center or PlayFab.

## Commands

| Command | API path | Description |
| --- | --- | --- |
| `context get` | None | Show the saved values. |
| `context set` | None | Save one value. |
| `context unset` | None | Remove saved values. |
| `context key list` | None | List the keys you can save and the command groups that use each. Needs no sign-in. |
| `context profile list` | None | List your profiles, starting with `default`. |
| `context profile delete` | None | Delete a named profile and every value saved in it. |

## Keys

A key is the name of an option that commands take as a context value:

| Key | Option | Used by |
| --- | --- | --- |
| `playfab-title-id` | `--playfab-title-id` | [`playfab`](playfab.md) commands |
| `sandbox` | `--sandbox` | [`xbl`](xbl.md) `configuration get`, `publish start`, and `publish status` |
| `seller-id` | `--seller-id` | [`game`](game.md), [`sandbox`](sandbox.md), [`test-account`](test-account.md), and [`xbl`](xbl.md) commands |

No other option takes a saved value or a variable; `--confirm` and `--body` never do.

## How commands use saved values

When a command takes one of these options and you don't pass it, the CLI fills it in, as if
you'd typed it, from the first of:

1. The `XBOX_` variable named after the option: `XBOX_PLAYFAB_TITLE_ID`, `XBOX_SANDBOX`, or
   `XBOX_SELLER_ID`. A blank one counts as unset.
2. The value `context set` saved for the signed-in account in the selected profile.

The option's own validation applies, and a bad value fails naming where it came from, such as
`XBOX_SELLER_ID: --seller-id must be a positive numeric value.` Configuration files don't set
these values. If nothing supplies a value a command needs, it says how to set one; signed out,
it says to sign in.

A command that changes something writes each value it filled in, and where it came from, to
stderr, even with `--confirm`. With `--verbose`, every command does:

```text
Using --playfab-title-id ABC123 from saved context.
```

## Profiles

A profile is a named set of saved values, such as one for a test title and one for production.
Every command takes `--profile <name>`; without it, commands use the profile `XBOX_PROFILE`
names, or else the `default` profile, which holds everything saved without either.

```bash
xbox --profile test context set playfab-title-id TEST1   # creates "test"
xbox --profile test playfab title-data get               # uses TEST1
xbox playfab title-data get                              # uses the default profile
export XBOX_PROFILE=test                                 # this shell now uses "test"
xbox context profile list
xbox context profile delete test
```

- A profile is created by the first value saved to it. Names are letters, digits, and hyphens,
  start with a letter or digit, are at most 64 characters, and aren't case-sensitive.
- A profile your account doesn't have, such as a misspelled one, fails rather than falling back
  to `default`, whether `--profile` or `XBOX_PROFILE` names it. `--profile` is checked whenever
  a command takes a context value, even when a flag or variable supplies it; `XBOX_PROFILE` only
  when a saved value is needed.
- A profile doesn't borrow from `default`. If `test` has no seller, a `game` or `sandbox`
  command run with `--profile test` fails and says how to save one. Commands where the seller
  is optional (`xbl` and `test-account`) send none, and the service uses your only
  authorized seller, as it does with no saved seller at all.
- A profile holds saved values only, so the `XBOX_` variables still win over it. When one wins
  over a profile named with `--profile`, the command says so on stderr, even if it changes
  nothing:

  ```text
  Using --playfab-title-id PROD1 from XBOX_PLAYFAB_TITLE_ID, not profile test.
  ```
- `context unset --all` clears only the selected profile. `context profile delete` removes a
  named profile; `default` can't be deleted.
- No profile is remembered between commands, so choosing one never retargets another terminal
  or agent: `--profile` applies to one command, and `XBOX_PROFILE` to the shell that sets it
  and what that shell starts.
- Output that names a saved value names its profile too:
  `Using --playfab-title-id TEST1 from profile test.`

## Where values are saved

Saved values live in `context.json` in the CLI state directory: `%LOCALAPPDATA%\XboxDevCLI`
on Windows, `~/Library/Application Support/XboxDevCLI` on macOS, and
`$XDG_DATA_HOME/XboxDevCLI` (by default `~/.local/share/XboxDevCLI`) on Linux. It's separate
from the token cache. Each value is saved for the signed-in account and for the host and port
of the endpoint it belongs to, so another account, or an endpoint on another host or port,
never picks it up. The scheme and path of the endpoint don't matter. `auth logout`
keeps saved values, and signing back in as the same account restores them. Concurrent saves
from several shells or agents all take effect. A `context.json` written by a newer CLI is
refused rather than read or rewritten.

## Arguments and options

| Argument or option | Commands | Description |
| --- | --- | --- |
| `<key> <value>` | `set` | The key to save and its value. Like `git config set`, both are positional. The value has to pass the validation of the options named after the key: `seller-id` must be a positive number (see [`xbox seller list`](seller.md)), and `playfab-title-id` letters and digits. |
| `<key>...` | `unset` | Keys to remove. |
| `<name>` | `profile delete` | The profile to delete. |
| `--profile <name>` | All | Use the values saved in this profile instead of `XBOX_PROFILE`'s or `default`. A global option, so every command takes it. |
| `--all` | `unset` | Remove every value saved in the selected profile for the signed-in account, on every endpoint, including keys saved by newer CLI versions. Cannot be combined with key names. |
| `--base-url <url>` | `get`, `set`, `unset` | Base URL for requests, overriding the `XboxPartner:BaseUrl` configuration key. Saved seller values are scoped to its host. |
| `--output <format>` | All | `json` (default), `json-compact`, or `table`. |

## Behavior

`get` returns the signed-in `account` (`username` and `tenantId`), the `profile`, and a
`values` array of `key` and `value`, with `null` for a key that isn't saved; tables show it as
`-`. It shows what is saved, not what a command would use, since a flag or variable wins; run
the command with `--verbose` to see that. A problem with sign-in or saved context doesn't fail
`get`: signed out, `account` is `null`, no values are shown, and it says on stderr how to sign
in; a `context.json` that can't be read is reported on stderr, and `get` still exits 0. An invalid option or configuration file, or an invalid `XBOX_PROFILE`, still exits 1,
and so does a profile that doesn't exist when someone is signed in. Signed out, `get` can't tell
whether a profile exists, so it reports the name it was given.

`set`, `unset`, and the `profile` commands need a signed-in account. `set` returns the profile
and the saved value. It doesn't check a seller with Partner Center; a seller you can't use fails
on the first command that sends it. Removing a value that isn't saved succeeds and changes
nothing.

Saved context belongs to a signed-in user, so a service principal has none. When
`XBOX_AUTHENTICATION__MODE` selects a service-principal mode, every command refuses `--profile`
and `XBOX_PROFILE`, `get`, `set`, `unset`, and the `profile` commands exit 1 and say so on
stderr, and no command reads saved values; pass each value with its flag or `XBOX_*`
variable instead. `key list` still works. See
[Automation (service principal) authentication](../authentication.md#automation-service-principal-authentication).

## Values that don't come from the command line

Saved context is shared by every session of your OS user, and environment variables don't
appear on the command line. So commands guard against acting on a value you didn't type:

- A command that changes something names each value it filled in, as
  [above](#how-commands-use-saved-values).
- An operation whose target is the context value itself must have it on the command line.
  `/Admin/DeleteTitle` deletes the title, and `playfab api-policy set --overwrite` replaces
  its whole API access policy, so each refuses a title from saved context or an environment
  variable, and prints the command to run instead:

  ```text
  $ xbox playfab api /Admin/DeleteTitle --confirm
  Using --playfab-title-id ABC123 from saved context.
  '/Admin/DeleteTitle' acts on playfab-title-id ABC123, which comes from saved context.
  The target of a destructive operation must be named on the command line. To proceed, run:
    xbox --playfab-title-id ABC123 playfab api /Admin/DeleteTitle --confirm
  ```

  The command is quoted for PowerShell on Windows and for a POSIX shell such as bash or zsh
  elsewhere, and an inline `--body` or `--statements` value is left out of it, since stderr
  ends up in logs and a request body can hold secrets: put it back in place of `<body>` or
  `<statements>`.
- Saved values belong to the account that saved them. If another account signs in while a
  command that uses them runs, for example while it waits for confirmation, the command stops
  before it sends anything and says to run it again.

See [Safety and destructive operations](../safety.md) for the confirmation rules these sit
alongside.

## Table output

Every context command accepts `--output table`:

| Command | Columns |
| --- | --- |
| `context get`, `set`, `unset` | `KEY`, `VALUE` |
| `context key list` | `KEY`, `USED BY` |
| `context profile list`, `profile delete` | `NAME` |

Table output is a human convenience, not a stability contract: columns may change without
a breaking-change bump. Use `--output json-compact` for anything parsed by a script.

## Examples

```bash
xbox context key list --output table                    # the keys you can set
xbox context set seller-id 1234567
xbox context set playfab-title-id ABC123
xbox context get --output table
xbox game list                                          # uses the saved seller
xbox --playfab-title-id DEF456 playfab title-data get    # the flag wins; nothing is saved
XBOX_PLAYFAB_TITLE_ID=DEF456 xbox playfab title-data get # so does the variable
xbox --verbose playfab title-data get                   # says where the title came from
xbox context unset playfab-title-id
xbox context unset --all
```

See [Commands](../commands.md) for the global options that apply across the CLI, and
[Configuration](../configuration.md) for the settings that aren't context.
