---
title: Installation
description: Download and install the CLI for Windows, Linux, or macOS.
---

# Installation

Install the XBOX Developer CLI from a release archive, make it available in your
terminal, and verify which executable runs. For a source build instead, see
[Building from source](contributing/building-from-source.md).

## Download a release

Download the latest release for your platform from the
[GitHub Releases page](https://github.com/microsoft/XBOX-Developer-CLI/releases/latest).
Choose the archive matching your operating system and CPU:

| Platform     | Asset                      | Binary |
|--------------|----------------------------|--------|
| Windows x64  | `xbox-win-x64.zip`   | `xbox.exe` |
| Linux x64    | `xbox-linux-x64.zip` | `xbox` |
| macOS on Apple silicon | `xbox-osx-arm64.zip` | `xbox` |
| macOS on Intel | `xbox-osx-x64.zip` | `xbox` |

The Linux x64 binary requires glibc 2.27 or newer. The macOS binaries are signed by
Microsoft and notarized by Apple. No separate .NET runtime installation is required to
run a downloaded release.

Extract the **whole archive** into a new, dedicated installation directory owned
by your user. The archive holds the executable, `LICENSE`, and `NOTICE.md`; the CLI's
settings are built into the executable, so there's no settings file to keep beside it.
Do not run it from inside the ZIP.

If you move the installation, move the entire folder. For settings you can customize, see
[Configuration](configuration.md).

The examples assume the ZIP is in your Downloads folder. If the destination
already contains an installation, follow [Update a ZIP installation](#update-a-zip-installation)
instead of extracting over it.

`PATH` is the list of directories your shell searches when you type `xbox`.
Add the **directory containing the executable**, not the ZIP or the executable's
filename. Keep all other PATH entries. The XBOX Developer CLI does not edit PATH
or install itself.

## Windows (PowerShell)

### 1. Extract the archive

```powershell
$cliDir = Join-Path $env:LOCALAPPDATA 'Programs\XboxDevCLI'
Expand-Archive -LiteralPath "$HOME\Downloads\xbox-win-x64.zip" -DestinationPath $cliDir
& "$cliDir\xbox.exe" --version
```

### 2. Add the installation directory to PATH

Open **Edit environment variables for your account** from Windows Search. Under
**User variables**, edit **Path**, choose **New**, and add the full directory
printed by this command:

```powershell
$cliDir
```

With the example above, this is normally
`C:\Users\<you>\AppData\Local\Programs\XboxDevCLI`. Do not replace the rest of PATH.
This user-scoped installation needs no administrator privileges.

To use the installation immediately in the same PowerShell session used for
extraction:

```powershell
$env:Path = "$cliDir;$env:Path"
```

This assignment is temporary; the Windows settings step makes PATH persistent.
Close and reopen your terminal application to use the persistent setting.
Restart the IDE too if you use its integrated terminal.

### 3. Verify the selected executable and version

In the reopened terminal, check which command your shell resolves and then check
the version:

```powershell
Get-Command xbox -All | Select-Object CommandType, Source, Definition
xbox --version
```

Follow [Verify the installation](#verify-the-installation) below to compare the
version and resolve an older executable, alias, or function.

## Linux and macOS (Bash or Zsh)

### 1. Extract the archive

Download the archive for your platform from the table above. This example uses
Linux x64; on macOS, use `xbox-osx-arm64.zip` on Apple silicon or `xbox-osx-x64.zip`
on Intel. Keep the full extracted contents together:

```bash
cli_dir="$HOME/.local/opt/xboxdevcli"
mkdir -p "$cli_dir"
unzip "$HOME/Downloads/xbox-linux-x64.zip" -d "$cli_dir"
chmod u+x "$cli_dir/xbox"
"$cli_dir/xbox" --version
```

If the operating system blocks the downloaded executable, review the warning and
confirm its provenance before proceeding; do not disable platform security checks.

### 2. Add the installation directory to PATH

Add this line once to the startup file your shell actually reads:

```bash
export PATH="$HOME/.local/opt/xboxdevcli:$PATH"
```

Use `~/.zshrc` for interactive Zsh, the default shell on macOS, or `~/.bashrc` for
interactive non-login Bash.
Login Bash reads `~/.bash_profile`, `~/.bash_login`, or `~/.profile` (the first
one that exists); put the line in that file, or ensure it sources your `~/.bashrc`.
If you chose a different installation directory, use that path instead.

To use the installation immediately in the same shell used for extraction:

```bash
export PATH="$cli_dir:$PATH"
```

This command is temporary; the startup-file edit makes PATH persistent.
Open a new terminal to use that setting. Restart the IDE too if you use its
integrated terminal.

### 3. Verify the selected executable and version

In the reopened terminal, check which command your shell resolves and then check
the version:

```bash
type -a xbox
xbox --version
```

Follow [Verify the installation](#verify-the-installation) below to compare the
version and resolve an older executable, alias, or function.

## Verify the installation

Confirm that the selected executable is in your chosen installation directory
and its version matches the release you downloaded (without the tag's leading
`v`). An explicit executable path, as in the extraction examples, lets you check the new copy
independently of PATH.

Among executable matches, the first directory in PATH wins; aliases and shell
functions can override it. If lookup selects an older copy, alias, or function,
resolve that conflict before treating installation as complete. Inspect both
user and system PATH on Windows; update the appropriate shell startup file on
Linux and macOS. Remove or rename a conflicting alias/function in the configuration
that defines it, and keep backup installations off PATH. Do not overwrite
package-managed files. Reopen the terminal and repeat both checks.

## Sign in and get started

Continue with [Getting started](getting-started.md) to sign in, select your
Partner Center seller, and list its games. Installation does not grant service
access; use an account with access to the intended Partner Center seller account.

For a command overview:

```bash
xbox --help
```

See [Authentication](authentication.md) for sign-in details and
[Commands](commands.md) for the full reference. `xbox info` displays
getting-started information.

For the first-run launch screen, welcome, and terminal color behavior, see
[First run and terminal output](commands/info.md#first-run-and-terminal-output).
See [Telemetry](telemetry.md) for the disclosure and opt-out policy.

## Check for updates

```bash
xbox upgrade
xbox upgrade --check --output json-compact
xbox --version
```

`upgrade` only checks the latest public stable GitHub release and returns JSON
with the running version, available version, release link, and manual update
instructions. Both forms above are read-only: nothing is installed or downloaded
beyond release metadata. There are no startup/background checks or prompts.

The check can fail when no stable release is available. An HTTP 404 does not
mean the XBOX Developer CLI is up to date. Open the releases page to check for
releases.
See [Upgrade](commands/upgrade.md) for output and troubleshooting details,
including checking versions when running from source.

For ZIP installations, follow the steps below. For source or package-managed
installations, use the original build/installation mechanism instead of
overwriting managed files.

## Update a ZIP installation

1. Open the release page returned by `xbox upgrade --check`, or the
   [GitHub Releases page](https://github.com/microsoft/XBOX-Developer-CLI/releases/latest)
   if the update check cannot access it. Review its notes and download the
   ZIP for your OS and CPU. Extract the whole archive into a **new** directory,
   separate from your current installation and user state directory.
2. Keep the old installation as a backup. Settings come from the new release's
   built-in defaults, `XBOX_` environment variables, and command options, so
   there's no settings file to carry forward; keep any `XBOX_` variables you
   set. If the old installation has an `appsettings.json` or
   `appsettings.*.json` you customized, move those values to `XBOX_` variables;
   see [Upgrading from a release that read appsettings.json](configuration.md#upgrading-from-a-release-that-read-appsettingsjson).
3. **Leave saved settings and sign-in data alone.** On Windows these are in
   `%LOCALAPPDATA%\XboxDevCLI`, separate from the recommended
   `%LOCALAPPDATA%\Programs\XboxDevCLI` installation. Update under the same OS
   user; do not copy, import, or reset these files. Your saved context remains
   available, but you may need to sign in again if your session has expired.
   See [Configuration](configuration.md#saved-context) for saved
   settings locations on all platforms.
4. Close running XBOX Developer CLI processes, especially on Windows. Either
   replace your old ZIP installation directory with the prepared new one at the
   **same path** (PATH needs no change), or keep the new directory and change
   the old PATH entry to its location using the [Windows](#windows-powershell)
   or [Linux and macOS](#linux-and-macos-bash-or-zsh) steps above.
   Keep the backup outside PATH.
5. Reopen your terminal application and IDE, if applicable. Run
   `Get-Command xbox -All` (PowerShell) or `type -a xbox` (Bash/Zsh), confirm
   the selected path, and compare `xbox --version` with the downloaded release.
   `xbox context get` shows the saved context.

Moving the installation does not require moving your saved settings or sign-in
data. If verification fails, keep the old copy and restore its PATH entry or
installation directory. These update steps are manual; `xbox upgrade` does not
perform them.
