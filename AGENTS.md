# Agent instructions for the XBOX CLI

This repository holds the source of the XBOX CLI (`xbox`), a .NET 10 command-line tool for building,
releasing, and operating games on XBOX. It signs in with Microsoft Entra ID and calls
PlayFab and Partner Center services. Data commands write machine-readable JSON; a few
informational commands write text for people.

## Names and brand

- Call the product the **XBOX CLI**, or the **XBOX Developer CLI** when it needs to be more
  specific. Don't call it "XBOX Dev CLI" or "XboxDevCLI" in prose.
- Write the brand as **XBOX** in prose, never "Xbox".
- Call XBOX's online services **XBOX services**, as in "XBOX services configuration," not
  "XBOX Live", except in a literal name such as Partner Center's **Xbox Live configuration** page.
- Keep identifiers exactly as they are: the `xbox` command, `XboxDevCLI` in namespaces
  and paths, repository names, `aka.ms/xbox-cli` links, configuration keys, and generated
  PlayFab source. New URLs and repository names use lowercase kebab case.

## Public-facing language

- Describe supported behavior, not internal implementation. Do not name internal
  services such as `XboxPartner` or expose internal routes in public documentation,
  CLI Help, errors, warnings, or progress messages.
- A replacement service name is usually unnecessary. Prefer wording such as
  "The request was rejected" or "The request timed out." Use "the service" when
  a noun is needed. Do not substitute "Partner Center" unless that attribution
  is verified.
- Preserve what failed, relevant status codes, and actionable troubleshooting
  guidance. Removing internal terminology must not make a message less useful.
- Preserve exact configuration keys and environment variables that users need
  to set, such as `XboxPartner:BaseUrl`. This is not a request to rename code
  identifiers, configuration settings, or actual request routes.
- Help documentation links must point to relevant public documentation, not
  internal API routes. Omit the link when no suitable documentation exists.
- Write for a reader who has only this repository. Leave out how a decision was reached,
  who asked for it, "as discussed", and private issue or pull request numbers; say what
  is, and why. Explain or replace jargon and abbreviations an outside developer wouldn't
  know, and don't link to anything they can't open.
- A code comment explains the code in terms of the code, the product, or a public spec,
  not an internal incident, conversation, or system.

## Production safety

Agents working here run `xbox` against production titles and sellers with the user's
admin rights, and nothing on the server side stops a mistake. Before you run `xbox`, read
[Using the XBOX CLI from agents](docs/cli-for-agents.md).

- Only PlayFab operations whose name starts with `Get`, `List`, or `Search` are
  read-only. Treat every other operation, and every `xbox` command that creates, changes,
  or deletes something, as a production change.
- Before a production change, show the user the command and say in one sentence what it
  changes and on which title or seller, including whether that target comes from saved
  context. Run it only after they say yes; a yes covers only the calls you showed. Ask
  before `xbox pc sandbox set` and `xbox pc sandbox launch-apps` too.
- `--confirm` is a backstop for scripts and accidents, not approval. Passing it yourself
  doesn't stand in for asking.

## Layout

| Path | Holds |
|------|-------|
| `src/XboxDevCLI/` | The CLI |
| `tests/` | Unit tests, property-based fuzz tests, and opt-in end-to-end tests |
| `tools/` | Generators for the PlayFab models and the Unicode width table |
| `docs/` | Docs for people using the CLI, with `contributing/` for contributors |

## Build and test

- The CLI targets **.NET 10**. `Directory.Build.props` sets `Nullable=enable`,
  `ImplicitUsings=enable`, and `TreatWarningsAsErrors=true`, so builds have zero warnings.
- Validate product changes with `dotnet build`, then `dotnet test`. Unit tests are xUnit,
  in `tests/XboxDevCLI.UnitTest/`.
- Tests that redirect `Console.Out` or `Console.Error` join
  `[Collection("ConsoleRedirect")]`, because console streams are process-global.
- The E2E harness in `tests/XboxDevCLI.EndToEndTests/` runs offline checks during normal
  tests; live tests skip unless `E2E_GITHUB_ENVIRONMENT` is set. See
  [Testing and coverage](docs/contributing/testing.md#live-end-to-end-tests)
  before opting in to real services.
- The end-to-end tests in `tests/XboxDevCLI.E2ETest/` run the CLI against production as the
  signed-in account, so they run only commands that read, and `xbl document commit` and
  `xbl publish start` with `--validate-only`, which XBOX services validate without storing anything.
  A new test must do the same. The test files are the record of what runs, so `Cli.cs` has no
  allowlist of commands: don't add one or any other guard, and don't ask for one in a review. See
  [Testing and coverage](docs/contributing/testing.md#end-to-end-tests-with-your-own-account).
- Measure coverage with
  `dotnet test --collect:"XPlat Code Coverage" --settings coverage.runsettings`. Checked-in
  generated sources are excluded. Changes keep overall line coverage at or above **70%**,
  and coverage of changed lines at or above **80%**.
- Releases are single-file executables, so use `AppContext.BaseDirectory`, never
  `Assembly.Location`, which is empty there and fails the publish with IL3000.
- `NOTICE.md` lists every package the CLI ships, with its version, license, and copyright.
  Update it whenever a package is added, removed, or updated, including a transitive one; a
  unit test fails until it matches the build.

## Architecture

- `src/XboxDevCLI/Program.cs` composes the root command and owns configuration layering,
  shared API invocation, output formatting, and destructive confirmation.
- Top-level command factories live in `src/XboxDevCLI/Commands/` and are wired in
  `Program.BuildRootCommand`. PlayFab command families live in
  `src/XboxDevCLI/PlayFab/Commands/` and are wired through `PlayFabCommandFactory`.
- Add a command to the nearest command-family factory rather than growing `Program.cs`.
- Route PlayFab calls through `Program.InvokeApiAsync` or the shared command invoker,
  never an ad hoc `PlayFabApiClient` path. The shared route owns configuration,
  authentication, output, and destructive confirmation.
- Every PlayFab path must exist in
  `src/XboxDevCLI/PlayFab/Generated/PlayFabApiMetadata.cs`. Verify the exact path, request
  type, and response type; never invent an endpoint or infer a capability.
- Ship the read path (`Get`, `List`, `Search`) before anything that changes production.
- `Delete*` and `Shutdown*` operations require `--confirm` (`--yes`, `-y`) and fail closed
  in `PlayFabApiClient.CallApiAsync`, which throws `DestructiveOperationNotConfirmedException`
  unless `confirmedDestructive: true` reaches it. Every destructive call path must thread
  confirmation through. `ContextResolver` also refuses an operation whose direct target is
  a context value it filled in; today that's `/Admin/DeleteTitle` and
  `api-policy set --overwrite`, both on the title.
- Data output stays machine-readable JSON and honors `--output` (`-o`) and `--data-only`.
  Results go to stdout; failures and diagnostics go to stderr.
- Show progress for a long operation with `ProgressLine` in `src/XboxDevCLI/Output/`, which
  draws on stderr only when it's a terminal, and erases itself when disposed. A command that
  reads every page of a list uses `ListProgress`, and adds `ListProgress.HelpOutput` to its Help.
- Static offline data belongs in `src/XboxDevCLI/Resources/` as an `EmbeddedResource`.

### Table output

JSON is the default for every data command. `--output table` is opt-in: a command
declares a `TableView` in its `CommandMetadata`, and commands without one refuse `table`
rather than quietly returning JSON. When adding a command that returns a list or a
single record, decide whether it deserves a view, and if so:

- Write column and collection names as `nameof(SomeResponse.SomeField)`, never string
  literals. The generated models name properties exactly as the wire does, so `nameof`
  produces the right string and a swagger regeneration that renames the field breaks the
  build instead of rendering a column of dashes.
- Pick a handful of columns a reader wants at a glance, not every scalar on the model — a
  row has to fit a terminal, and inferring columns is what makes `az ... --output table`
  lossy. Columns must name scalar fields; use a dotted path to reach inside a nested
  object.
- Set `Redact: true` on any column holding a credential. Tables end up in screenshots and
  scrollback; JSON still returns the real value.
- If the command formats its own response rather than using the shared invoke path, it
  must consult `OutputOptions.TableView`. Declaring a view and then formatting JSON
  anyway has shipped once and is invisible to tests that check the renderer and the
  declaration separately.

A validating test walks every declared view against its generated response model and
fails the build on a field that does not exist, a non-scalar column, or an unredacted
credential. The command's page under `docs/commands/` says whether it accepts `--output
table`, and must be updated in the same change.

## Documentation

The `docs/` directory is plain Markdown that ships with the source; there is no site
generator. A unit test checks that every `xbox` command and option the docs name exists,
but nothing checks that every command is documented, or that the docs describe it
correctly. Every command, verb, or option change must update them in the same pull request.

The command reference follows the command tree. `docs/commands.md` covers `xbox` itself;
`xbox <command>` is documented in `docs/commands/<command>.md` (for example
`docs/commands/xbl.md`), and `xbox playfab <service>` in
`docs/commands/playfab/<service>.md` (for example `docs/commands/playfab/mps.md`). Add your
command to the page for the group it belongs to. A new group needs its own page in the
matching place, a row in its parent page's command table, and an entry in the tree in
`docs/README.md`.

Keep the source portable, because it is intended for publication elsewhere: link between
pages with relative file paths (`[Commands](commands.md)`), never absolute URLs or
site-generator template syntax. Front matter carries `title` and a one-sentence
`description`, and nothing else, and the page's `#` heading matches its `title`.

Follow the existing command table, option table, and runnable-example shape; explain
non-obvious requirements, conflicts, and deliberate omissions. Include an API path column
only for public APIs that readers can call, such as PlayFab, never for internal service
routes. Extend an option table (and add a subsection when needed) for a new option. Say on
the command's page whether it accepts `--output table`. Changes to destructive behavior or
confirmation must also update `docs/safety.md`.

## Example data

Docs, Help examples, and test fixtures are published, so they use fictitious names and
placeholder identifiers, never values copied from a real title, account, or response.

- **Companies:** Nod Publishers (`nodpublishers.com`), and Lucerne Publishing
  (`lucernepublishing.com`) when a second is needed. Both are cleared for use as
  fictitious names. Don't use Contoso, Fabrikam, real studios, or real game names.
- **Games:** names under those companies, such as `Nod Arena`, `Nod Rally`, and
  `Lucerne Rally`, with package names such as `Nod.Arena_abc123` and the publisher
  `CN=Nod Publishers`.
- **People and hosts:** `dev@nodpublishers.com`; storage hosts such as
  `nodpublishers.blob.core.windows.net`.
- **Identifiers:** reuse the placeholders already in the repository, in the right format:

  | Identifier | Placeholder |
  |---|---|
  | PlayFab title ID | `ABC123` |
  | PlayFab player ID (master player account) | `PLAYER123` |
  | PlayFab title player account ID | `TITLEPLAYER123` |
  | PlayFab GUID ID, such as a build, session, experiment, or catalog item ID | `11111111-2222-3333-4444-555555555555`, counting up (`22222222-3333-4444-5555-666666666666`) when an example needs more than one |
  | Economy operation token | `OPERATIONTOKEN123` |
  | Seller ID | `1234567` |
  | XBOX title ID | `1234ABCD` |
  | SCID | `aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee` |
  | Store ID | `9N0000000000` |
  | Product ID | `00000000000000000042` |
  | MSA app ID | `0000000040ABCDEF` |
  | Sandbox | `XDKS.1`, `ABCDEF.1`, `TESTSB.42`, or `RETAIL` |

Keep a placeholder's format, length, and casing when it matters to the test, such as a
display width or a case-sensitive sandbox name.
