# XBOX Developer CLI

The XBOX Developer CLI brings building, releasing, and operating XBOX games
together in one command-line tool, with one sign-in, one set of commands, and
consistent output.

Use it directly in your terminal, automate workflows with scripts and CI
pipelines, or work with AI agents.

## Install

Download the latest release for Windows x64, Linux x64, or macOS (Apple silicon or
Intel) from the [Releases page](https://github.com/microsoft/XBOX-Developer-CLI/releases/latest).

Follow [Installation](docs/installation.md) for complete setup instructions:
extract the **whole archive**, keep its files together, set PATH persistently,
reopen terminals and IDEs, and check for older executables or aliases.
No separate .NET runtime installation is required.

After setup, verify the installed version:

```bash
xbox --version
```

For source builds, see [Building from source](docs/contributing/building-from-source.md).

### Check for updates

`xbox upgrade --check` checks release metadata; it never installs an update.
See [Check for updates](docs/installation.md#check-for-updates), including
what to do when anonymous discovery cannot access a release.

### Update a ZIP installation

Follow [Update a ZIP installation](docs/installation.md#update-a-zip-installation)
to preserve configuration changes and saved settings, switch installations safely,
and verify the selected executable. Use the original installation mechanism for
source or package-managed installations.

## Documentation

Start at the [documentation index](docs/README.md). The docs are plain
Markdown that ships with the source and can be read on GitHub or locally.
For a guided first use, follow [Getting started](docs/getting-started.md).
For commands and options, see the [command reference](docs/commands.md).

## Quick start

Sign in with a developer account that has access to the intended Partner Center seller:

```bash
xbox auth login
xbox seller list --output table
```

Choose the seller from the results. Replace `1234567` below with that seller's ID.

Saving this default affects other CLI sessions using the same signed-in account
and profile. Save it, then list the seller's games:

```bash
xbox context set seller-id 1234567
xbox context get --output table
xbox game list --output table
```

The final command lists the selected seller's games. An explicit `--seller-id`
or `XBOX_SELLER_ID` overrides the saved value. See
[Getting started](docs/getting-started.md) for the full walkthrough and
[Authentication](docs/authentication.md) for sign-in help.

For PlayFab, follow the [PlayFab quick start](docs/playfab-api.md#setup).
It covers choosing a title and making a read-only request, and links to an example
response.
The signed-in developer needs **Admin** permission on that title.

## Commands

Discover commands with `xbox --help` or `xbox <command> --help`.
`xbox help <command>` is equivalent to the latter; see [Help](docs/commands/help.md).

| Work with | Documentation |
|---|---|
| Games and sellers | [Games](docs/commands/game.md), [Seller](docs/commands/seller.md) |
| Sandboxes and test accounts | [Sandbox](docs/commands/sandbox.md), [Test account](docs/commands/test-account.md) |
| Service configuration and publishing | [Service configuration](docs/commands/xbl.md) |
| Cross-platform services for your live game | [PlayFab command reference](docs/commands/playfab.md), [API integration](docs/playfab-api.md) |
| Multiplayer servers | [MPS reference and MpsPowershell migration](docs/commands/playfab/mps.md) |
| Saved defaults and profiles | [Context](docs/commands/context.md) |

The [complete command reference](docs/commands.md) includes authentication,
version information, upgrades, and every service command.

### Common options

See [Global options](docs/commands.md#global-options) and
[PlayFab options](docs/commands/playfab.md#options) for output,
diagnostics, title selection, profiles, and confirmation flags.

### Configuration

See [Configuration](docs/configuration.md) for files, environment
variables, precedence, and saved settings locations, and
[Context](docs/commands/context.md) for account-scoped saved targets.

### Output and exit codes

See [Output model](docs/commands.md#output-model) for JSON, opt-in tables,
stdout/stderr behavior, errors, and exit codes, including signed-out
`auth status`. Command pages document additional command-specific exit codes.

## Safety

Commands can change production state with your account's permissions. Review
the target and effect before running a command that writes. Some commands, such
as PlayFab `Delete*` and `Shutdown*` operations, require confirmation; **not every
mutation is gated**.
`--confirm` is not a substitute for an agent obtaining user approval.

Read [Safety and destructive operations](docs/safety.md) for confirmation
rules and exceptions, and [Keeping secrets out of logs](docs/cli-for-agents.md#keeping-secrets-out-of-logs)
for request-file, configuration, and sensitive-response handling.

## Using the XBOX CLI with AI agents

[Using the XBOX CLI from agents](docs/cli-for-agents.md) covers running the CLI from AI agents and
automation, including the safety rules an agent must follow before it changes anything.

Agents that work on this source, such as GitHub Copilot, Claude Code, and Codex, read
[`AGENTS.md`](AGENTS.md) automatically. It has the conventions for building, testing, and
documenting changes.

## Telemetry

Official release artifacts send limited anonymous usage and reliability telemetry to Microsoft.
Command arguments, PlayFab payloads, title IDs, identities, tokens, and local paths are never
collected. Opt out with `XBOX_TELEMETRY_OPTOUT=1` or `--disable-telemetry`; see
[docs/telemetry.md](docs/telemetry.md) for the full policy.

Microsoft handles this data as described in the
[Microsoft privacy statement](https://go.microsoft.com/fwlink/?LinkID=824704); see
[PRIVACY.md](PRIVACY.md) for the privacy notice.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development, architecture, generators,
and documentation guidance. Contributions currently go through
[GitHub Issues](https://github.com/microsoft/XBOX-Developer-CLI/issues);
the public repository does not accept pull requests yet.
This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/);
see [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## Support and security

The XBOX Developer CLI is a technical preview, supported through
[GitHub Issues](https://github.com/microsoft/XBOX-Developer-CLI/issues), not
Microsoft Customer Service & Support. See [SUPPORT.md](SUPPORT.md) for the full
policy and other developer support channels.
Report security vulnerabilities as described in [SECURITY.md](SECURITY.md) —
not through public issues.

## License

Licensed under the MIT License — see [LICENSE](LICENSE).

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft trademarks or logos is subject to and must follow [Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general). Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship. Any use of third-party trademarks or logos is subject to those third parties' policies.
