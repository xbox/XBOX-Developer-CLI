#!/bin/sh
# Installs the latest XBOX CLI release for Linux x64 or macOS into ~/.local/opt/xboxdevcli,
# and adds that directory to PATH in your shell's startup file.
#
#   curl -fsSL https://raw.githubusercontent.com/xbox/cli/main/install.sh | sh
set -eu

repo="xbox/cli"
install_dir="$HOME/.local/opt/xboxdevcli"

fail() {
    echo "error: $1" >&2
    exit 1
}

case "$(uname -s)/$(uname -m)" in
    Linux/x86_64) asset="xbox-linux-x64.zip" ;;
    Darwin/arm64) asset="xbox-osx-arm64.zip" ;;
    Darwin/x86_64) asset="xbox-osx-x64.zip" ;;
    *) fail "no release for $(uname -s) $(uname -m). See https://github.com/$repo#quickstart." ;;
esac

command -v curl >/dev/null 2>&1 || fail "curl is required."
command -v unzip >/dev/null 2>&1 || fail "unzip is required."

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading $asset..."
curl -fsSL "https://github.com/$repo/releases/latest/download/$asset" -o "$tmp/$asset"
unzip -q "$tmp/$asset" -d "$tmp/xbox"
chmod u+x "$tmp/xbox/xbox"

# Replace an earlier installation as a whole, so no files from it are left behind.
mkdir -p "$(dirname "$install_dir")"
rm -rf "$install_dir"
mv "$tmp/xbox" "$install_dir"

path_line="export PATH=\"$install_dir:\$PATH\""
case "$(uname -s)/${SHELL:-}" in
    */zsh) profile="$HOME/.zshrc" ;;
    Darwin/*/bash)
        # Terminal on macOS starts login shells, which read the first of these files that exists.
        profile="$HOME/.bash_profile"
        for file in "$HOME/.bash_profile" "$HOME/.bash_login" "$HOME/.profile"; do
            if [ -f "$file" ]; then profile="$file"; break; fi
        done
        ;;
    */bash) profile="$HOME/.bashrc" ;;
    */fish)
        profile="$HOME/.config/fish/conf.d/xboxdevcli.fish"
        path_line="contains -- \"$install_dir\" \$PATH; or set -gx PATH \"$install_dir\" \$PATH"
        mkdir -p "$(dirname "$profile")"
        ;;
    *) profile="$HOME/.profile" ;;
esac
if ! grep -qsxF "$path_line" "$profile"; then
    printf '\n%s\n' "$path_line" >> "$profile"
    echo "Added $install_dir to PATH in $profile."
fi

"$install_dir/xbox" --version
echo
echo "Installed. Open a new terminal, then run: xbox auth login"
