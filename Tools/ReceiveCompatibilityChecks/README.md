# Receive compatibility checks

See `REPORT.md` for the latest measured outcomes and mitigation analysis.

This diagnostic invokes the compiled Receiver's private
`GitPullApp.GitPullService.SwapPreparedDirectory` method through reflection. It
does not compile, link, or modify Receiver/Send production sources.

It creates uniquely named fixtures below `%TEMP%\ReceiveCompatibilityChecks`,
tests old `dist`, `bin`, and `obj` directories under several Windows filesystem
conditions, writes structured observations to `latest-results.json`, and removes
only its own temporary fixtures. The loaded executable fixture starts and stops
only a copied `cmd.exe` owned by the fixture.

Run from this directory:

```powershell
dotnet run -c Release -- `
  -ReceiverAssembly "Z:\codex\Send&Receive\Receive\Receive_source\bin\Release\net8.0-windows\win-x64\GitPullApp.dll" `
  -SenderAssembly "Z:\codex\Send&Receive\Send\backend_source\bin\Release\net8.0\win-x64\PullverseSenderService.dll" `
  -ResultsPath ".\latest-results.json"
```

The explicit assembly options make the diagnostic runnable from an isolated
build mirror where the production source tree is not a sibling. Lowercase
aliases `--receiver`, `--sender`, and `--results` are also accepted.

The incoming fixture deliberately has no `dist`, `bin`, or `obj`; it contains a
minimal `.git` directory because the receiver validates that marker before the
second rename. One negative test omits `.git` to prove rollback preserves the
existing clone.

The first check also invokes the actual Sender package creator twice, once with
populated `dist/bin/obj` trees and once without them. It extracts both packages
with the actual Receiver package extractor and compares paths and SHA-256 file
hashes. A fresh random test key is generated in memory; no certificate or local
Sender/Receiver settings are read.
