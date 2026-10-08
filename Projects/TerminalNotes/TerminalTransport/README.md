# TerminalNotes.TerminalTransport

`net8.0` library for raw-byte interactive terminals. Windows uses ConPTY (Windows 10 version 1809 / build 17763 or newer); Linux and macOS use the native `forkpty` helper. The host application still needs its own portable UI/rendering stack.

Build the Unix helper on each target platform with `native/build-unix-helper.sh`. This produces `native/terminalnotes-pty`; package/copy it beside the host executable as `terminalnotes-pty`, or set `TerminalProcessOptions.UnixHelperPath`. Linux links `libutil`; macOS uses the system `forkpty` implementation. The helper is native to the build host and is not cross-compiled by this script.

The API starts an executable with its argument vector directly (no shell command-string construction), in the supplied working directory. Environment values inherit from the app and then apply the specified overrides; a null value removes an inherited variable. VT bytes are returned as-is. `CloseAsync` disposes only the Windows private Job Object or the Unix helper-owned PTY process group; it does not search for or terminate unrelated processes.

Validation status: Windows requires a real ConPTY smoke test on Windows 10 1809+; Linux and macOS require building the helper with the script and running the conditional PTY smoke tests there. A redirected pipe is not used as a terminal substitute.
