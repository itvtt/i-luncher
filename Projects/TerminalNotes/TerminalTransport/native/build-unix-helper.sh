#!/bin/sh
set -eu
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
output="$script_dir/terminalnotes-pty"
case "$(uname -s)" in
  Linux) cc -std=c11 -O2 -Wall -Wextra -Werror "$script_dir/terminalnotes-pty.c" -o "$output" -lutil ;;
  Darwin) cc -std=c11 -O2 -Wall -Wextra -Werror "$script_dir/terminalnotes-pty.c" -o "$output" ;;
  *) echo "Supported build hosts are Linux and macOS." >&2; exit 2 ;;
esac
chmod 755 "$output"
printf 'Built %s for %s/%s\n' "$output" "$(uname -s)" "$(uname -m)"
