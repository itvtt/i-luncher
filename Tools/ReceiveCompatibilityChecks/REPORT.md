# Receive swap compatibility report

Run: 2026-09-15 11:17 KST on Windows 10.0.26200 / .NET 8.0.31.

The diagnostic loaded the existing compiled production assemblies and invoked
`GitPullApp.GitPullService.SwapPreparedDirectory` through reflection. It did not
modify or rebuild Receiver or Sender.

- Receiver SHA-256: `0EF1BB659739460D19A3969AEE31F256E80FE3CDA2EB3F6C158BEA7C51291A68`
- Sender SHA-256: `5141374F676753CCD09E55BFEB1E370E4B5677177275249748D3340368F22F99`
- Structured evidence: `latest-results.json`

## Actual results

| Fixture in the old clone | Result from the real receiver swap |
| --- | --- |
| Plain populated `dist/bin/obj` | Swap succeeded; old generated trees were deleted. |
| Read-only + hidden generated file/directory | Swap succeeded; receiver cleared the attributes during cleanup. |
| Generated file at a 413-character path | Swap and cleanup succeeded. |
| File opened with `FileShare.None` under `dist` | Initial clone rename failed after its retries with `IOException` / access denied. Original clone and generated files remained intact. |
| Directory handle without delete sharing under `bin` | Initial clone rename failed with the same `IOException`. Original clone remained intact. |
| Receiver process current directory set under old `obj` | Initial clone rename failed with the same `IOException`. Original clone remained intact. |
| Running copied executable under old `bin` | New clone was installed. Deleting the old backup failed on the loaded EXE and produced the receiver warning; cleanup succeeded after the owned process exited. |
| Exclusively opened build artifact in a sibling directory outside the clone | Swap and cleanup succeeded; the external artifact remained present and unchanged. |
| Incoming tree missing the required `.git` marker | Receiver threw `IOException`, rolled the old tree back, and preserved all original files. |

The actual Sender package creator was also invoked twice with a fresh in-memory
test key: one source contained five files including populated `dist/bin/obj`,
and the other contained only the two source files. The actual Receiver extractor
produced identical two-file trees from both encrypted packages (same relative
paths and SHA-256 content hashes). This directly confirms that Sender omission
works, while the swap results show why omission does not neutralize generated
directories already present in the destination.

## Interpretation and mitigation

`SwapPreparedDirectory` renames the entire existing clone before installing the
incoming tree. Every old descendant therefore matters to Windows rename
semantics even if Sender omitted that descendant from the new snapshot.

The strongest mitigation supported by the matrix is to keep build output,
runtime copies, and process working directories outside the receive clone. The
external-build case succeeded while its artifact had the same exclusive file
lock that made the inside-clone case fail. Launchers and child applications
should receive an explicit working directory outside the clone, and builds
should run in a separate workspace/mirror with output and runtime paths there.
Existing applications with a current directory or non-delete-sharing handle
inside the old clone must exit or release that handle before the first clean
replacement can succeed.

Read-only/hidden flags and ordinary long paths did not need another mitigation
on this machine. A loaded executable permits the replacement here but leaves a
uniquely named `.pv-back-*` directory until its image is no longer loaded, so
running from outside the clone also prevents stale backup accumulation.

## Limits

- This is a method-level compatibility matrix plus a real Sender/Receiver
  package round trip. It does not perform a network fetch or exercise the full
  `PullConfiguredRepository` flow.
- Lock and rename behavior was measured on this Windows machine and local temp
  volume. Network shares, other filesystems, antivirus/filter drivers, and the
  corporate machine can differ.
- The loaded-image fixture used an owned copy of Windows `cmd.exe`; application
  launchers can hold additional handles with different sharing flags.
- The current-directory fixture used the diagnostic process itself. It proves
  the Windows failure mode without starting or stopping user processes.
- All lock holders and temporary trees belonged to the diagnostic. No user
  process was inspected, stopped, or modified, and no data was uploaded.
