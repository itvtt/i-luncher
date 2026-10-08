#!/bin/sh
set -eu
project_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
sh "$project_dir/../TerminalTransport/native/build-unix-helper.sh"
dotnet build "$project_dir/TerminalNotes.TerminalDesktop.csproj" -c Release
printf '%s\n' 'Run: dotnet run --project TerminalDesktop/TerminalNotes.TerminalDesktop.csproj -c Release --no-build'
