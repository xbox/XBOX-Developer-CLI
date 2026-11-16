---
title: Installation
description: Download and install the CLI for Windows, Linux, or macOS.
---

# Installation

Install the XBOX CLI with WinGet on Windows, with the install script on Linux or macOS, or from
a release archive on any platform. To build it from source instead, see
[Building from source](contributing/building-from-source.md).

## Windows: install with WinGet

On Windows, install the CLI with [WinGet](https://learn.microsoft.com/windows/package-manager/winget/),
the Windows Package Manager:

```powershell
winget install xbox
xbox --version
```

WinGet adds `xbox` to your `PATH`, and needs no administrator rights. If the terminal can't find
`xbox` after installing, open a new terminal, or restart your IDE if you use its integrated
terminal.

To update the CLI:

```powershell
winget upgrade Microsoft.Gaming.XBOXDeveloperCLI
```

To remove it:

```powershell
winget uninstall Microsoft.Gaming.XBOXDeveloperCLI
```

Uninstalling leaves your saved settings and sign-in data in `%LOCALAPPDATA%\XboxDevCLI`. Update a
WinGet installation only with WinGet, not with the ZIP steps on this page. Continue with
[Next steps](#next-steps).

## Linux and macOS: install with a script

On Linux x64 or macOS, the install script downloads the latest release into
`~/.local/opt/xboxdevcli`, and adds that directory to your `PATH` in your shell's startup file.
It needs `curl` and `unzip`:

```bash
curl -fsSL https://raw.githubusercontent.com/xbox/cli/main/install.sh | sh
```

The script prints the startup file it changed. Open a new terminal, and check the version with
`xbox --version`. To update the CLI, run the script again. To remove it, delete
`~/.local/opt/xboxdevcli` and the line the script added to that startup file.

## Download a release

Install from a release archive when WinGet and the install script don't fit, such as on a
Windows PC without WinGet.

Download the archive for your operating system and CPU from the
[GitHub Releases page](https://github.com/xbox/cli/releases/latest):

| Platform     | Asset                      | Binary |
|--------------|----------------------------|--------|
| Windows x64  | `xbox-win-x64.zip`   | `xbox.exe` |
| Linux x64    | `xbox-linux-x64.zip` | `xbox` |
| macOS on Apple silicon | `xbox-osx-arm64.zip` | `xbox` |
| macOS on Intel | `xbox-osx-x64.zip` | `xbox` |

The Linux x64 binary requires glibc 2.27 or later. The macOS binaries are signed by Microsoft and
notarized by Apple. You don't need to install .NET.

Extract the whole archive into a new directory of its own, and add that directory to your `PATH`,
as the steps for your platform show. If the directory already holds an installation, follow
[Update a ZIP installation](#update-a-zip-installation) instead.

## Windows (PowerShell)

1. Extract the archive, and check the extracted executable:

   ```powershell
   $cliDir = Join-Path $env:LOCALAPPDATA 'Programs\XboxDevCLI'
   Expand-Archive -LiteralPath "$HOME\Downloads\xbox-win-x64.zip" -DestinationPath $cliDir
   & "$cliDir\xbox.exe" --version
   ```

2. Add the directory to your `PATH`. From Windows Search, open **Edit environment variables for
   your account**. Under **User variables**, edit **Path**, select **New**, and enter the
   directory, such as `C:\Users\<you>\AppData\Local\Programs\XboxDevCLI`.

3. Open a new terminal, and check the version:

   ```powershell
   xbox --version
   ```

## Linux and macOS (Bash or Zsh)

1. Extract the archive, and check the extracted executable. This example uses Linux x64; on
   macOS, use `xbox-osx-arm64.zip` on Apple silicon or `xbox-osx-x64.zip` on Intel:

   ```bash
   cli_dir="$HOME/.local/opt/xboxdevcli"
   mkdir -p "$cli_dir"
   unzip "$HOME/Downloads/xbox-linux-x64.zip" -d "$cli_dir"
   chmod u+x "$cli_dir/xbox"
   "$cli_dir/xbox" --version
   ```

2. Add the directory to your `PATH`. Add this line to `~/.zshrc` for Zsh, the default shell on
   macOS, or to `~/.bashrc` for Bash:

   ```bash
   export PATH="$HOME/.local/opt/xboxdevcli:$PATH"
   ```

3. Open a new terminal, and check the version:

   ```bash
   xbox --version
   ```

## Verify the installation

`xbox --version` shows the version of the release you downloaded, without the tag's leading `v`.
If it shows an older version, another copy of `xbox` comes first on your `PATH`. Find it with
`Get-Command xbox -All` in PowerShell or `type -a xbox` in Bash or Zsh, and remove it from your
`PATH`.

## Check for updates

`xbox upgrade` tells you whether there's a newer release, and how to install it. See
[Upgrade](commands/upgrade.md).

```bash
xbox upgrade
```

To update a WinGet installation, run `winget upgrade Microsoft.Gaming.XBOXDeveloperCLI`. To
update a script installation, run the [install script](#linux-and-macos-install-with-a-script)
again. To update a ZIP installation, follow the next section.

## Update a ZIP installation

Your saved settings and sign-in data are stored apart from the installation, so they carry over.
See [Where values are saved](commands/context.md#where-values-are-saved).

1. Download the new release's archive from the release page `xbox upgrade` shows, or from the
   [GitHub Releases page](https://github.com/xbox/cli/releases/latest), and extract it
   into a new directory.
2. Close any running `xbox` processes.
3. Either replace your old installation directory with the new one, at the same path, or change
   your `PATH` entry to the new directory as in [Windows (PowerShell)](#windows-powershell) or
   [Linux and macOS (Bash or Zsh)](#linux-and-macos-bash-or-zsh).
4. Open a new terminal, and check that `xbox --version` shows the new version.

Keep the old directory, off your `PATH`, until the new version works. If your sign-in has expired,
run `xbox auth login` again.

## Next steps

- [Getting started](getting-started.md): sign in, choose your Partner Center seller, and list its
  games.
- [Commands](commands.md): the full command reference.
- [Telemetry](telemetry.md): what the CLI collects, and how to turn it off.
