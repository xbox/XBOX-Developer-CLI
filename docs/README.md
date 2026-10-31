# XBOX CLI documentation

The documentation source for the XBOX CLI, kept in the repository so it ships with the
code and stays editable in the same pull request as the change it describes.

These files are plain Markdown with no site generator. Read them here on GitHub, or clone
the repository and read them locally — every link between them is a relative file path, so
both work.

## Start here

| Page | What it covers |
|------|----------------|
| [Installation](installation.md) | Download and install the CLI for your platform. |
| [Getting started](getting-started.md) | Install the XBOX Developer CLI, sign in, select a seller, and list its games. |
| [Authentication](authentication.md) | Sign in, check status, cached credentials, service principals, and PlayFab requirements. |

## Reference

| Page | What it covers |
|------|----------------|
| [Commands](commands.md) | The command reference: global options, output formats, and every command below. |
| [PlayFab API integration](playfab-api.md) | Finding and calling PlayFab APIs from the terminal. |

The command pages follow the command tree:

- [`xbox`](commands.md): global options and output formats
  - [`auth`](commands/auth.md): sign in and out
  - [`context`](commands/context.md): default values, such as the seller and PlayFab title
  - [`game`](commands/game.md): Partner Center games
  - [`seller`](commands/seller.md): Partner Center sellers
  - [`sandbox`](commands/sandbox.md): XBOX sandboxes
  - [`pc`](commands/pc.md): this PC's XBOX sandbox (Experimental)
  - [`test-account`](commands/test-account.md): XBOX test account batches and creation jobs, and looking up an account's batch and whether its access expired
  - [`playfab`](commands/playfab.md): PlayFab services
    - [`api`](commands/playfab/api.md): call any API endpoint directly
    - [`mps`](commands/playfab/mps.md): Multiplayer Servers (Experimental)
    - [`file`](commands/playfab/file.md): entity files
    - [`title-data`](commands/playfab/title-data.md): title data
    - [`api-policy`](commands/playfab/api-policy.md): API access policy
    - [`cloudscript`](commands/playfab/cloudscript.md): CloudScript and Azure Functions (Experimental)
    - [`group`](commands/playfab/group.md): groups and membership (Experimental)
    - [`player-data`](commands/playfab/player-data.md): player data
    - [`economy`](commands/playfab/economy.md): Economy v2 inventory
    - [`experiment`](commands/playfab/experiment.md): experiments (Experimental)
    - [`player`](commands/playfab/player.md): player accounts (Experimental)
    - [`privacy`](commands/playfab/privacy.md): privacy exports (Experimental)
  - [`xbl`](commands/xbl.md): XBOX Live configuration (Experimental)
    - [`achievement-image`](commands/xbl/achievement-image.md): XBOX Live achievement images
  - [`info`](commands/info.md): version and links
  - [`upgrade`](commands/upgrade.md): check for a newer release
  - [`feedback`](commands/feedback.md): open a bug-report draft with environment information
  - [`help`](commands/help.md): help for any command

## Behavior

| Page | What it covers |
|------|----------------|
| [Safety and destructive operations](safety.md) | Which operations are gated, and the subtleties of how. |
| [Configuration](configuration.md) | Layering settings from files, environment variables, and options. |
| [Telemetry](telemetry.md) | What is collected and how to opt out. |
| [Audit logging](audit-log.md) | Keeping a local record of mutating invocations. |
| [Using the XBOX CLI from agents](cli-for-agents.md) | Running the CLI from AI agents: approval, targets, output, and finding APIs. |

## Contributing

| Page | What it covers |
|------|----------------|
| [Building from source](contributing/building-from-source.md) | Build and run from the repository. |
| [Testing and coverage](contributing/testing.md) | Run tests and measure coverage against the project's bar. |
| [Fuzz testing](contributing/fuzzing.md) | Run and tune the property-based fuzz tests. |
| [Code generation](contributing/code-generation.md) | Regenerate PlayFab models and API metadata. |
| [Experimental commands](cli-command-lifecycle.md) | CLI compatibility commitments and Experimental labeling. |

## Editing these docs

Every command, verb, or option change updates this documentation in the same pull request.
The pages follow the command tree, so a command's path says where it goes: `xbox <command>`
is documented in `commands/<command>.md`, and `xbox playfab <service>` in
`commands/playfab/<service>.md`. A new group needs its own page in the matching place, a
row in its parent page's command table, and an entry in the tree above. Each command page
says whether its commands accept `--output table`.

Three conventions keep the source portable, because it is meant to be published elsewhere:

- **Link between pages with relative file paths** — `[Commands](commands.md)`,
  `[MPS](commands/playfab/mps.md)`. No absolute URLs, and no site-generator template syntax.
- **Front matter carries `title` and `description`, and nothing else.** `description` is
  one sentence saying what the page covers; the publishing target shows it under the title
  and uses it for search. Ordering and grouping live in this index rather than in per-file
  metadata.
- **The page's `#` heading matches its `title`**, so the page reads the same here as it
  does once published.
