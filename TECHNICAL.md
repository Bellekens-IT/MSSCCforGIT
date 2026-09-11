# MSSCCforGIT — Technical Documentation

This document explains the internal design of MSSCCforGIT, the constraints that shaped it, and
the debugging journey that led to the current implementation. It's intended for contributors or
anyone curious about how a Git-backed MSSCCI provider for Enterprise Architect (EA) actually works
under the hood.

## 1. What MSSCCI is, and why it's awkward here

The Microsoft Source Code Control Interface (MSSCCI) is a decades-old, native, C-style plugin API
originally designed for tools like Visual SourceSafe. A source-control "provider" is a DLL that
exports a fixed set of `Scc*` functions (`SccInitialize`, `SccAdd`, `SccCheckin`, `SccGet`, ...)
using **stdcall** calling convention. The host application (here, EA) resolves these functions via
`GetProcAddress` and calls into them directly — there's no COM registration, no managed
interop layer, no structured type marshaling beyond raw pointers and fixed-width integers.

Git has none of MSSCCI's assumptions built in:

- MSSCCI assumes a **centralized** version control model where "adding" a file to source control
  also effectively checks it in and publishes it — there's no notion of a local staging area that
  isn't shared yet.
- MSSCCI operates on individual files with in/out check-out semantics (locks), while Git has no
  locking model at all and works on the whole repository via commits.
- MSSCCI has no concept of "push" — checking in a file *is* publishing it, so `SccCheckin`
  (and, per EA's model, `SccAdd`) had to be mapped to **stage + commit + push** as a single atomic
  step.

## 2. Why Native AOT

EA is a native (unmanaged) 32-bit application. For it to load a source control provider, that
provider must be a plain native DLL exporting the expected `Scc*` symbols with the correct
calling convention — EA has no way to host a .NET runtime or talk to managed code via any kind of
interop shim.

.NET's **Native AOT** compilation model solves this: the project is compiled directly to a
self-contained native `x86` DLL with no separate runtime dependency, and
`[UnmanagedCallersOnly]` is used to export plain, ABI-correct native entry points for each `Scc*`
function EA calls, e.g.:

```csharp
[UnmanagedCallersOnly(EntryPoint = "SccAdd", CallConvs = new[] { typeof(CallConvStdcall) })]
public static unsafe int SccAdd(
	IntPtr pContext,
	IntPtr hWnd,
	int nFiles,
	sbyte** lpFileNames,
	sbyte* lpComment,
	int* pFlags,
	int fOptions)
{ ... }
```

This is what makes the whole approach possible, but it also introduces a set of constraints that
drove most of the debugging described below:

- No reflection, no dynamic code generation, and — critically — **no support for custom
  marshalers** that some libraries (including LibGit2Sharp, in places) rely on internally.
- All interop with EA's memory (string pointers, buffers) has to be done manually via
  `Marshal`/raw pointer arithmetic rather than typical P/Invoke string marshaling.

### The `RuntimeIdentifier`/export table trap

EA's MSSCCI loader validates the *full* extended MSSCCI export table (not just the original 1.1
base set) before it will accept a provider — it calls `GetProcAddress` for every function in the
extended spec, including many that this provider only needs to stub out
(`SccRunScc`, `SccDirDiff`, `SccGetUserOption`, etc.). Missing even one caused EA to reject the
provider entirely with a generic `Failed to InitProcPointers` error at startup, which took some
trial and error (and comparison against other working Git MSSCCI providers such as
[pbsGitMSSCCI](https://github.com/pbrondum/pbsGitMSSCCI)) to fully resolve.

## 3. Locating the native LibGit2Sharp dependency

LibGit2Sharp is a managed wrapper around the native `git2-*.dll`. Under normal .NET hosting, the
runtime knows to look for native dependencies next to the managed assembly. When hosted as a
Native AOT DLL loaded by an unrelated host process (EA.exe), the default DLL search path only
includes the *host* executable's directory — not the directory containing our own DLL — so the
native `git2-*.dll` next to `MSSCCforGIT.dll` couldn't be found, causing `DllNotFoundException`
at first LibGit2Sharp use.

The fix is a `[ModuleInitializer]` that runs before any exported `Scc*` function can be invoked,
which locates our own module's directory (via `GetModuleHandleExW`/`GetModuleFileNameW` against
the address of a marker method compiled into this DLL) and explicitly adds it to the process's
DLL search path using `AddDllDirectory`/`SetDllDirectory`.

## 4. Repository discovery under Native AOT

LibGit2Sharp's `Repository.Discover` (walking up from a path to find the enclosing `.git`
directory) did not behave reliably under Native AOT in testing. Repository discovery is instead
implemented manually by walking up the directory tree from a candidate path and checking for a
`.git` directory/file at each level (`DiscoverRepositoryPath`).

## 5. Decoding EA's string pointers

This turned out to be one of the trickiest parts of the whole project, and it's split into two
distinct problems depending on what kind of string is being read.

### File name pointers

Diagnostics showed that the `lpFileNames` pointers EA passes are **not** plain
null-terminated ANSI C strings at the given address. There's a variable-length, non-obvious header
or wrapper (consistent with how MFC/ATL-style string buffers are commonly laid out) preceding the
actual path text, and the offset of the real string varies. Naive approaches — fixed offsets,
"first printable run wins" — produced truncated or garbage paths.

The working approach (`ReadMfcCStringOrAnsi`) scans a bounded window of memory (260 bytes,
MAX_PATH-ish) after the raw pointer, decodes a candidate ANSI string at every position that looks
like the start of a new printable run, and scores each candidate:

1. Prefer the longest candidate that starts with a drive letter (`C:\`) or UNC prefix (`\\`) *and*
   actually resolves to a real file or existing parent directory on disk.
2. Otherwise, prefer the longest "path-like" candidate (contains both a path separator and a dot),
   since EA's layout sometimes truncates the leading portion of the real path while leaving the
   trailing filename fragment intact and reliable.
3. Otherwise, fall back to the single longest printable candidate found.
4. As a last resort, treat the raw pointer itself as a plain null-terminated ANSI string.

Because the leading portion of a decoded path can still be corrupted/truncated even when a
candidate is found, `ResolveEaPath` additionally re-derives the trailing filename segment and
re-combines it with a cached project root path (captured earlier from `SccOpenProject`, keyed by
EA's `pContext` handle, with a "last opened project" fallback since `pContext` isn't always a
stable/non-zero key across calls).

### Comment pointers

Initially, comment strings (`lpComment`) were decoded with the same `ReadMfcCStringOrAnsi`
heuristic used for file paths. This was wrong: diagnostics showed EA actually passes comment text
as a **plain, direct** null-terminated ANSI string at the given pointer — no wrapper, no scanning
needed. Reusing the path-oriented heuristic on a comment could pick up an unrelated path-like
string sitting later in the same memory window instead of the real (often short) comment text,
which is what caused commit messages to sometimes contain garbage path fragments, or nothing at
all.

The fix was a separate, much simpler decoder for comments (`ReadPlainAnsiString`) that does a
straight `Marshal.PtrToStringAnsi` with no scanning or heuristics, used consistently everywhere
`lpComment` is decoded (`SccCheckin`, `SccAdd`, `SccAddFromScc`, `SccAddFilesFromSCC`).

## 6. SccQueryInfo status flags

EA calls `SccQueryInfo` after operations like Add to confirm the resulting file status, and is
picky about the returned bit flags (`SCC_STATUS_CONTROLLED`, `SCC_STATUS_CHECKEDOUT`,
`SCC_STATUS_DIFFERENT`, etc., mapped from LibGit2Sharp's `FileStatus`). Returning an unexpected
combination (e.g. reporting a freshly-added, unmodified file as "different") caused EA to report
"Unexpected status after adding file." The mapping was tuned so a clean, tracked file only reports
`SCC_STATUS_CONTROLLED`.

## 7. Commit identity fallback

`Repository.Config.BuildSignature` returns `null` (rather than throwing) when Git's
`user.name`/`user.email` aren't resolvable from any visible config scope — which then caused
`Repository.Commit` to throw `ArgumentNullException` deep inside LibGit2Sharp's native commit
creation path. `GetFallbackSignature` provides a sensible default (current OS username /
`username@machinename`) when config-based resolution fails, so commits never fail purely due to
missing identity configuration.

## 8. Why commit and push are done via the `git` CLI, not LibGit2Sharp

Two separate, unrelated interop issues under Native AOT led to bypassing LibGit2Sharp for these
two operations specifically — everything else (status queries, staging, removal, checkout, pull,
repository discovery) still goes through LibGit2Sharp, since it works correctly for those.

### Push: no transparent credential helper support

`Repository.Network.Push` requires the caller to supply an explicit `CredentialsHandler` callback;
it does not transparently use the Windows Git Credential Manager the way a plain `git push` from a
terminal does. Without one, push failed immediately with
`LibGit2Sharp.LibGit2SharpException: remote authentication required but no callback set` — even
though `git push` run manually in the same repository worked fine.

Rather than reimplementing a full credentials callback (and duplicating whatever credential
helper/PAT/SSH setup the user already has working for their normal Git usage), `PushViaGitCli`
shells out to the real `git push` command as a child process, which transparently reuses the
user's existing Git Credential Manager session/config.

### Commit: message string silently lost under Native AOT

After identity and push were fixed, commits were succeeding, but repeatedly showed up with a
**completely empty commit message** even though the correct comment text was confirmed present
immediately before the call (verified via diagnostic logging of the string right before calling
`Repository.Commit`, and via `git cat-file -p` on the resulting raw commit object showing no
message body at all).

The cause is believed to be LibGit2Sharp's internal commit-message marshaling: some LibGit2Sharp
native interop paths use a custom `ICustomMarshaler`
(`LibGit2Sharp.Core.StrictUtf8Marshaler`-style marshaling) to pass UTF-8 strings across the P/Invoke
boundary. Native AOT does not support reflection-based custom marshalers, so this class of
marshaling silently fails rather than throwing a hard, obvious error — the call still succeeds and
produces a valid commit object, just with the message dropped.

`CommitViaGitCli` sidesteps this entirely by shelling out to `git commit -m "<comment>"` as a
child process, passing the comment as a normal process argument rather than through LibGit2Sharp's
native interop layer.

> If other string-bearing LibGit2Sharp operations (e.g. `Commands.Pull` creating a merge commit
> with a message) are ever observed producing similarly empty/garbled text under Native AOT, the
> same CLI-based workaround should be applied there too. This has not been necessary so far for
> the operations currently implemented.

## 9. Preventing stray files from being swept into a commit

An earlier version of `StageCommitAndPush` staged only the intended file and then committed
whatever was currently in the Git index. If a *previous* operation had staged a file but failed
before completing its commit (e.g. due to the missing-identity or push-authentication issues
above), that stale staged entry remained in the index and was silently included in the *next*
successful commit — resulting in commits containing unrelated files the user never asked to
check in.

The fix resets the index back to `HEAD` (`repo.Reset(ResetMode.Mixed, repo.Head.Tip)`) at the
start of every commit operation, before staging only the specific file(s) for that operation. This
guarantees each EA-triggered commit contains exactly the file(s) EA asked for, regardless of any
leftover state from prior failed attempts.

## 10. Show History

`SccHistory` has no structured output parameters in the MSSCCI spec for returning a list of
revisions to the host — the provider is expected to present its own UI. The current
implementation shells out to `git log --follow` for the resolved file's relative path and displays
the resulting log text (short hash, date, author, subject) in a simple `MessageBoxW` owned by EA's
window handle.

This is intentionally a read-only view for now. A more complete implementation — an interactive
list of revisions with a "Get this version" action that retrieves historical file content via
`git show <rev>:<path>` and overwrites the working copy — is a known, deferred enhancement (see
the Known Limitations section in [README.md](README.md)).

## 11. Diagnostics

Nearly every exported function logs its key inputs/outputs (and any exception) to
`%TEMP%\MSSCCforGIT.log` via `LogDiagnostic`. Because Native AOT + `UnmanagedCallersOnly` code
running inside a foreign host process is very difficult to debug with a conventional attached
debugger, this log file was the primary tool used throughout development to inspect exactly what
EA was passing in and what the provider was doing with it — including the raw decoded paths,
comments, and git CLI stdout/stderr for every commit/push/history invocation.

## Summary of current architecture

| Operation | Implementation |
|---|---|
| Repository discovery | Manual `.git` directory walk (LibGit2Sharp's `Repository.Discover` avoided) |
| Status query (`SccQueryInfo`) | LibGit2Sharp `RetrieveStatus` |
| Stage (`SccAdd`, `SccCheckin`, `SccRename`) | LibGit2Sharp `Commands.Stage` |
| Remove (`SccRemove`) | LibGit2Sharp `Commands.Remove` |
| Checkout / Get (`SccGet`, `SccUncheckout`) | LibGit2Sharp `repo.CheckoutPaths` |
| Pull | LibGit2Sharp `Commands.Pull` |
| Commit | `git commit` via CLI (see §8) |
| Push | `git push` via CLI (see §8) |
| History | `git log` via CLI, shown in a `MessageBoxW` (see §10) |
