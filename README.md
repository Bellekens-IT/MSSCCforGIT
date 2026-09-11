# MSSCCforGIT

**MSSCCforGIT** is a Microsoft Source Code Control Interface (MSSCCI) provider that lets
[Sparx Systems Enterprise Architect](https://sparxsystems.com/) use a **Git** repository as its
version control backend.

Enterprise Architect (EA) has built-in support for source control, but only understands the
classic MSSCCI protocol used by tools like Visual SourceSafe, Team Foundation Server, and
Perforce. It has no native concept of Git. This project bridges that gap: it implements the
MSSCCI provider interface that EA expects, and translates every operation EA makes (add, check
in, check out, get, history, ...) into the equivalent Git operation on a local repository (using
[LibGit2Sharp](https://github.com/libgit2/libgit2sharp) and the `git` command line tool).

With MSSCCforGIT installed and registered, you can right-click a package in your EA model and
choose **Add to Source Control**, **Check In**, **Check Out**, **Get Latest Version**, etc., just
like with any other EA-supported source control system — except your model files end up as
ordinary commits in a Git repository that you can push to GitHub, Azure DevOps, or any other Git
remote.

> For a deep dive into how the provider works internally, the tricky parts of hosting a .NET
> library as a native COM-free DLL for EA, and the debugging story behind it, see
> [TECHNICAL.md](TECHNICAL.md).

## How it works, in short

- EA loads MSSCCI providers as a native DLL and calls into a fixed set of exported functions
  (`SccInitialize`, `SccAdd`, `SccCheckin`, `SccGet`, ...).
- MSSCCforGIT is a .NET 10 project published as a **Native AOT** DLL, so it can be loaded directly
  by EA (a native, unmanaged process) without requiring a separate .NET runtime.
- Each exported function translates the EA request into the matching Git action: staging files,
  committing with the comment you typed in EA, and pushing to the `origin` remote — or checking
  out/pulling the latest version for "Get".
- Because EA's centralized-VCS model has no concept of a local-only "staged" state, adding a file
  in EA performs a full commit + push in one step, just like checking in.

## Requirements

- Sparx Systems Enterprise Architect (32-bit).
- [Git for Windows](https://git-scm.com/download/win) installed and available on `PATH`
  (the provider shells out to the `git` command for commit/push, since some LibGit2Sharp
  operations don't behave correctly when running inside a Native AOT binary — see
  [TECHNICAL.md](TECHNICAL.md) for details).
- A local clone of the Git repository that contains (or will contain) your EA model files.
- Git credentials configured so that pushing works from a normal terminal (e.g. via Git Credential
  Manager, an SSH key, or a Personal Access Token). If `git push` works for you manually in the
  repository, it will also work from EA.

## Installation

1. Build/publish the project in Release mode (see below), or download a published release.
   This produces `MSSCCforGIT.dll` under
   `bin\Release\net10.0\win-x86\publish\`.
2. Register the provider with Windows so EA can discover it. A ready-made
   [`RegisterMSSCCforGIT.reg`](RegisterMSSCCforGIT.reg) file is included — edit the
   `SCCServerPath` value inside it to point at the full path of your published
   `MSSCCforGIT.dll`, then double-click the file to import it into the registry.
   > EA is a 32-bit application, so the registry entries must go under the 32-bit
   > (`WOW6432Node`) registry hive, which the provided `.reg` file already does.
3. Start (or restart) Enterprise Architect.

### Building from source

```powershell
dotnet publish MSSCCforGIT.csproj -c Release -r win-x86 --self-contained
```

The build automatically republishes the native AOT DLL after every Release build. Note that EA
must be closed while rebuilding, since it keeps the DLL file locked while running.

## Using it in Enterprise Architect

1. Open (or create) an EA project whose file lives inside a folder that is part of a Git
   repository (a plain local clone is enough — it doesn't need to be inside the same folder as
   the `.eap`/`.qea` file, as long as EA's project folder is within the repository).
2. In EA, go to **Configure → Version Control → Package Control Options**, choose the
   **MSSCCforGIT** provider, and connect your project.
3. Use EA's normal source control actions from the model tree's right-click menu:
   - **Add to Source Control** — stages, commits (using the comment you enter), and pushes the
	 new package file.
   - **Check Out** — no-op / ensures the local working copy exists (Git doesn't use exclusive
	 locks).
   - **Check In** — stages, commits (using your comment), and pushes your changes.
   - **Undo Check Out** — discards local changes and restores the file from `HEAD`.
   - **Get Latest Version** — checks out / pulls the latest committed version of the file.
   - **Show History** — displays the Git commit history for the selected file.
4. Verify your changes: run `git log` in the repository, or check your remote (e.g. GitHub) to
   see the commits pushed by EA.

## Known limitations

- **Show History** is currently read-only: it displays the commit log for a file but does not yet
  let you retrieve/check out an older revision directly from the history dialog. Retrieving an
  older revision must currently be done manually with Git.
- The provider assumes a single Git repository per EA project; nested/multi-repository setups are
  not specifically handled.
- Diagnostic logging is written to `%TEMP%\MSSCCforGIT.log` and is useful for troubleshooting if an
  operation doesn't behave as expected.

## License

See [LICENSE](LICENSE).
