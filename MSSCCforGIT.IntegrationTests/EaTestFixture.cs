using System.Diagnostics;
using LibGit2Sharp;
using Microsoft.Win32;
using IOFile = System.IO.File;

namespace MSSCCforGIT.IntegrationTests;

/// <summary>
/// Shared fixture for all EA-driven integration tests. Boots real EA.exe via COM automation,
/// opens the <c>Test\TestProject.qea</c> project, and makes sure the registered MSSCCI provider
/// points at a freshly published native AOT build of this repo's provider DLL.
///
/// Because every test drives the same EA instance and the same on-disk git repo, tests must run
/// sequentially (see DisableTestParallelization in the csproj) and share this single fixture
/// instance (registered as an xUnit collection fixture - see <see cref="EaTestCollection"/>).
/// </summary>
public sealed class EaTestFixture : IDisposable
{
    private const string ProviderRegistryKey = @"SOFTWARE\Bellekens\Bellekens GIT MSSCCI";
    private const string InstalledProvidersKey = @"SOFTWARE\SourceCodeControlProvider\InstalledSCCProviders";
    private const string ProviderName = "Bellekens GIT MSSCCI";

    /// <summary>Absolute path to the repo's "Test" folder, used as the git fixture repo.</summary>
    public string TestRepoPath { get; }

    /// <summary>Absolute path to the root of the git repository that contains <see cref="TestRepoPath"/>.</summary>
    public string GitRepoRoot { get; }

    /// <summary>Path of the Test folder relative to <see cref="GitRepoRoot"/>, e.g. "Test".</summary>
    private readonly string _testRepoRelativePath;

    /// <summary>Absolute path to Test\TestProject.qea - the checked-in master copy, never opened directly.</summary>
    public string TestProjectPath { get; }

    /// <summary>
    /// Absolute path to a disposable working copy of <see cref="TestProjectPath"/> that EA is
    /// actually opened against for the lifetime of this fixture, shared across every test in the
    /// run and deleted on <see cref="Dispose"/>. Kept alongside the master copy in
    /// <see cref="TestRepoPath"/> (not a separate directory) because EA resolves a
    /// version-controlled package's backing XML file relative to its own project file's directory.
    /// </summary>
    public string TempProjectPath { get; }

    /// <summary>Absolute path to the published native MSSCCI provider DLL.</summary>
    public string ProviderDllPath { get; }

    /// <summary>The live EA Automation Repository object, open on TestProjectPath for the duration of the fixture.</summary>
    public EA.Repository EaRepository { get; }

    /// <summary>
    /// All EA Automation calls (both the ones made internally by this fixture and the ones tests
    /// make directly against <see cref="EaRepository"/>/its packages) must be routed through this
    /// runner. EA.App/EA.Repository are STA COM servers, and some of their methods (observed with
    /// Package.VersionControlCheckout) rely on the calling thread pumping Windows messages while
    /// the call is in flight - calling them directly from an xUnit thread-pool (MTA) thread can
    /// fail with an opaque COMException (HRESULT 0x8004000D, CO_E_OBJNOTCONNECTED).
    /// </summary>
    public StaThreadRunner StaThread { get; }

    /// <summary>
    /// Drives EA's actual UI for version-control actions that are known to fail via COM Automation
    /// (see <see cref="EaUiAutomation"/> for why). Must only be used from <see cref="StaThread"/>.
    /// </summary>
    public EaUiAutomation EaUi { get; }

    /// <summary>
    /// When set to true, <see cref="Dispose"/> leaves EA running (visible) instead of closing it,
    /// so a human can keep inspecting/interacting with the EA window after a diagnostic test run
    /// finishes. Intended for ad-hoc manual diagnostic tests only - never set this in real tests.
    /// </summary>
    public bool KeepEaOpenOnDispose { get; set; }

    private readonly EA.App _eaApp;
    private readonly string _diagnosticLogPath;
    private readonly string _initialHeadSha;

    public EaTestFixture()
    {
        var solutionDir = FindSolutionDirectory();
        TestRepoPath = Path.Combine(solutionDir, "Test");
        TestProjectPath = Path.Combine(TestRepoPath, "TestProject.qea");
        // Always the same fixed name (rather than a per-run GUID-suffixed name) so repeated test
        // runs don't keep piling up distinct entries in EA's "recent projects" list.
        TempProjectPath = Path.Combine(TestRepoPath, "TestProject.Testcopy.qea");
        ProviderDllPath = Path.Combine(solutionDir, "bin", "Release", "net10.0-windows", "win-x86", "publish", "MSSCCforGIT.dll");
        _diagnosticLogPath = Path.Combine(Path.GetTempPath(), "MSSCCforGIT.log");

        if (!IOFile.Exists(TestProjectPath))
        {
            throw new FileNotFoundException($"Test EA project not found at '{TestProjectPath}'.", TestProjectPath);
        }

        // The "Test" folder lives inside the same git repo as the rest of the solution (it's not
        // its own repo), so all git operations must be scoped to it via pathspecs rather than
        // treating TestRepoPath as a repo root.
        var discoveredGitDir = LibGit2Sharp.Repository.Discover(TestRepoPath)
            ?? throw new InvalidOperationException($"Could not discover a git repository containing '{TestRepoPath}'.");
        using (var repo = new LibGit2Sharp.Repository(discoveredGitDir))
        {
            GitRepoRoot = repo.Info.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _initialHeadSha = repo.Head.Tip.Sha;
        }

        _testRepoRelativePath = Path.GetRelativePath(GitRepoRoot, TestRepoPath).Replace('\\', '/');

        EnsureProviderPublished(solutionDir);
        EnsureProviderRegistered();
        ResetTestRepo();
        ClearDiagnosticLog();

        // Tests open a disposable copy of TestProject.qea rather than the checked-in master copy,
        // so EA's own project-file bookkeeping (recent files, lock files, SQLite WAL/journal
        // sidecars) never touches the file tracked in source control and every run starts from an
        // identical, pristine model.
        IOFile.Copy(TestProjectPath, TempProjectPath, overwrite: true);

        // EA.App/EA.Repository must be created on the same STA thread that will later invoke
        // methods on them (COM apartment affinity is determined by the creating thread), so the
        // whole startup sequence below runs via StaThread rather than directly on the constructor's
        // (thread-pool, MTA) thread.
        StaThread = new StaThreadRunner();

        // EA.App is a single-instance COM server: if an EA.exe process is already running (e.g. a
        // stray instance left over from a previous crashed/killed test run, possibly with a stale
        // modal dialog open that would silently swallow this run's UI Automation clicks), creating
        // a new EA.App() COM object reuses that existing process instead of launching a fresh one.
        // Force-close any such leftover instances first so every test run starts from a known-clean
        // EA.exe with no stray dialogs.
        KillExistingEaProcesses();

        // Capture the set of EA.exe processes already running before we launch our own, so we can
        // reliably identify which EA.exe process belongs to this test run (there's no direct way
        // to get a process ID from the EA.App COM object) for UI Automation to attach to.
        var preExistingEaPids = new HashSet<int>(Process.GetProcessesByName("EA").Select(p => p.Id));

        try
        {
            (_eaApp, EaRepository) = StaThread.Invoke(() =>
            {
                var eaApp = new EA.App();
                var repository = (EA.Repository)eaApp.Repository;

                // EA refuses to become visible until a file is open ("EA cannot be made visible unless
                // a file is first open" COMException), so OpenFile must happen before Visible = true -
                // there's no way to shorten the invisible window further than this.
                if (!repository.OpenFile(TempProjectPath))
                {
                    throw new InvalidOperationException($"EA failed to open project '{TempProjectPath}': {repository.GetLastError()}");
                }

                eaApp.Visible = true;

                // Without this, some VersionControl* automation calls (e.g. VersionControlCheckout) can
                // block on or be aborted by EA's own confirmation dialogs, which surface to COM callers as
                // an opaque COMException with no message/HRESULT info since there's no UI thread pumping
                // messages for the dialog in this automation scenario.
                repository.SuppressEADialogs = true;

                return (eaApp, repository);
            });

            var eaProcessId = FindNewEaProcessId(preExistingEaPids);
            EaUi = StaThread.Invoke(() => new EaUiAutomation(eaProcessId));
        }
        catch
        {
            // If anything in startup fails after EA.exe was launched, xUnit will never call
            // Dispose() on this fixture (the constructor itself threw), so the orphaned EA.exe
            // process must be cleaned up here instead, or it leaks indefinitely.
            KillExistingEaProcesses();
            StaThread.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Force-closes any EA.exe processes already running before this fixture starts its own, so
    /// <c>new EA.App()</c> always launches a fresh instance rather than silently attaching to a
    /// leftover process (which can have stray modal dialogs open from a previous interrupted test
    /// run, swallowing this run's UI Automation input).
    /// </summary>
    private static void KillExistingEaProcesses()
    {
        foreach (var process in Process.GetProcessesByName("EA"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
                // Best-effort - if it's already gone or can't be killed, proceed anyway; the
                // PID-diffing logic below will fail loudly if this leaves ambiguity.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Finds the EA.exe process backing this fixture's EA.App instance. EA.App is a single-instance
    /// COM server: if an EA.exe process is already running, creating a new EA.App() COM object can
    /// reuse that existing process rather than launching a new one, so simply diffing "new" PIDs
    /// isn't reliable. Instead, prefer a PID that appeared after activation; if none appeared,
    /// fall back to whichever EA.exe process already existed (since that must be the one EA.App
    /// attached to).
    /// </summary>
    private static int FindNewEaProcessId(HashSet<int> preExistingEaPids)
    {
        List<Process> newCandidates = new();
        for (int attempt = 0; attempt < 30; attempt++)
        {
            newCandidates = Process.GetProcessesByName("EA").Where(p => !preExistingEaPids.Contains(p.Id)).ToList();
            if (newCandidates.Count == 1)
            {
                return newCandidates[0].Id;
            }

            Thread.Sleep(200);
        }

        var allCurrent = Process.GetProcessesByName("EA").Select(p => p.Id).ToList();
        if (allCurrent.Count == 1)
        {
            return allCurrent[0];
        }

        throw new InvalidOperationException(
            $"Could not identify the EA.exe process started by this test fixture. " +
            $"Pre-existing PIDs: [{string.Join(", ", preExistingEaPids)}]. " +
            $"New candidates: [{string.Join(", ", newCandidates.Select(p => p.Id))}]. " +
            $"All EA.exe PIDs now: [{string.Join(", ", allCurrent)}].");
    }

    /// <summary>Reads the current diagnostic log contents written by the provider's LogDiagnostic helper.</summary>
    public string ReadDiagnosticLog()
    {
        return IOFile.Exists(_diagnosticLogPath) ? IOFile.ReadAllText(_diagnosticLogPath) : string.Empty;
    }

    /// <summary>
    /// Closes the temp project in EA, discards it, re-copies a pristine temp copy from the
    /// checked-in master <see cref="TestProjectPath"/>, and reopens it. <see cref="ResetTestRepo"/>
    /// alone only resets the git working tree - it cannot undo state EA itself tracks inside the
    /// .qea file (e.g. a package's checked-out/locked bit), so without this, a checkout/checkin
    /// performed by one test would leak into the next test sharing this fixture's single EA
    /// instance and temp project copy.
    /// </summary>
    public void ResetEaProject()
    {
        StaThread.Invoke(() =>
        {
            EaRepository.CloseFile();
            IOFile.Copy(TestProjectPath, TempProjectPath, overwrite: true);
            if (!EaRepository.OpenFile(TempProjectPath))
            {
                throw new InvalidOperationException($"EA failed to reopen project '{TempProjectPath}': {EaRepository.GetLastError()}");
            }

            EaRepository.SuppressEADialogs = true;
        });
    }

    public void ClearDiagnosticLog()
    {
        if (IOFile.Exists(_diagnosticLogPath))
        {
            IOFile.Delete(_diagnosticLogPath);
        }
    }

    /// <summary>
    /// Discards any local commits/working-tree changes made by a previous test run under the Test
    /// folder, and removes untracked files there (e.g. newly-added packages) so tests can re-add
    /// files with the same names. Scoped to the Test subfolder so the rest of the repo (including
    /// this test project's own source files) is left untouched.
    /// </summary>
    public void ResetTestRepo()
    {
        using var repo = new LibGit2Sharp.Repository(GitRepoRoot);

        var initialCommit = repo.Lookup<Commit>(_initialHeadSha)
            ?? throw new InvalidOperationException($"Could not find original commit '{_initialHeadSha}'.");

        // Move the current branch back to the commit recorded at fixture startup. Tests commit
        // directly to this branch (e.g. SccCheckin, VersionControlAdd, and the external-commit
        // helper used by the GetLatest test all create real commits), so just restoring the Test/
        // working-tree content with CheckoutPaths isn't enough on its own - without also moving
        // the branch tip back, those test commits would permanently accumulate on HEAD across runs
        // and contaminate every later test/run with content that was only ever meant to be
        // transient. This only touches the ref (a Test-scoped git repo for this fixture), not any
        // file content outside Test/.
        if (repo.Head.Tip.Sha != initialCommit.Sha)
        {
            repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), initialCommit.Sha);
        }

        // Revert any tracked changes under Test/ back to that commit.
        repo.CheckoutPaths(initialCommit.Sha, new[] { _testRepoRelativePath }, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });

        // Remove any files added under Test/ that didn't exist at that commit. TestProject.qea
        // and its transient SQLite-journal sidecar files are intentionally untracked/excluded:
        // the .qea itself isn't meant to be versioned, and the journal/shm/wal files may still be
        // locked by EA while the project is open.
        var options = new StatusOptions { IncludeUntracked = true, PathSpec = new[] { _testRepoRelativePath } };
        foreach (var entry in repo.RetrieveStatus(options))
        {
            if (entry.State is FileStatus.NewInWorkdir && !IsEaProjectOrSidecarFile(entry.FilePath))
            {
                var fullPath = Path.Combine(GitRepoRoot, entry.FilePath);
                if (IOFile.Exists(fullPath))
                {
                    IOFile.Delete(fullPath);
                }
            }
        }
    }

    private bool IsEaProjectOrSidecarFile(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return fileName.Equals("TestProject.qea", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("TestProject.qea-", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith(Path.GetFileName(TempProjectPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string FindSolutionDirectory()
    {
        var dir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(dir);
        while (current is not null)
        {
            if (IOFile.Exists(Path.Combine(current.FullName, "MSSCCforGIT.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException($"Could not locate MSSCCforGIT.slnx above '{dir}'.");
    }

    private static void EnsureProviderPublished(string solutionDir)
    {
        var projectPath = Path.Combine(solutionDir, "MSSCCforGIT.csproj");
        var dllPath = Path.Combine(solutionDir, "bin", "Release", "net10.0-windows", "win-x86", "publish", "MSSCCforGIT.dll");

        var needsPublish = !IOFile.Exists(dllPath) || IsAnySourceNewerThan(solutionDir, IOFile.GetLastWriteTimeUtc(dllPath));
        if (!needsPublish)
        {
            return;
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            ArgumentList =
            {
                "publish",
                projectPath,
                "-c", "Release",
                "-r", "win-x86",
                "--self-contained",
                "-p:_IsPublishing=true",
            },
            WorkingDirectory = solutionDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start 'dotnet publish'.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !IOFile.Exists(dllPath))
        {
            throw new InvalidOperationException(
                $"Publishing the native provider failed (exit code {process.ExitCode}).\nstdout: {stdout}\nstderr: {stderr}");
        }
    }

    private static bool IsAnySourceNewerThan(string solutionDir, DateTime timestampUtc)
    {
        foreach (var file in Directory.EnumerateFiles(solutionDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            if (IOFile.GetLastWriteTimeUtc(file) > timestampUtc)
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureProviderRegistered()
    {
        using var installedProviders = Registry.LocalMachine.CreateSubKey(InstalledProvidersKey, writable: true)
            ?? throw new InvalidOperationException($"Could not open/create '{InstalledProvidersKey}'.");
        installedProviders.SetValue(ProviderName, ProviderRegistryKey);

        using var providerKey = Registry.LocalMachine.CreateSubKey(ProviderRegistryKey, writable: true)
            ?? throw new InvalidOperationException($"Could not open/create '{ProviderRegistryKey}'.");
        providerKey.SetValue("SCCServerName", ProviderName);
        providerKey.SetValue("SCCServerPath", ProviderDllPath);
    }

    public void Dispose()
    {
        if (KeepEaOpenOnDispose)
        {
            StaThread.Dispose();
            return;
        }

        try
        {
            StaThread.Invoke(() =>
            {
                try
                {
                    EaUi?.Dispose();
                }
                catch
                {
                    // Best-effort cleanup only.
                }

                try
                {
                    EaRepository?.CloseFile();
                }
                catch
                {
                    // Best-effort cleanup only.
                }

                try
                {
                    EaRepository?.Exit();
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            });
        }
        catch
        {
            // Best-effort cleanup only.
        }

        // EA.Repository.Exit() returns before the EA.exe process has actually torn down, so it can
        // still briefly hold open handles on files under Test\ (e.g. a just-added package's XML
        // file). Without waiting here, the git checkout in ResetTestRepo() below can race that
        // teardown and fail with "could not rmdir ... used by another process".
        KillExistingEaProcesses();

        ResetTestRepo();
        StaThread.Dispose();
        DeleteTempProjectFiles();
    }

    /// <summary>
    /// Deletes the disposable working copy of TestProject.qea (and any sidecar files EA created
    /// next to it, e.g. SQLite WAL/journal/lock files) once EA has fully closed it. Keeps the
    /// checked-in master copy at <see cref="TestProjectPath"/> untouched.
    /// </summary>
    private void DeleteTempProjectFiles()
    {
        var tempProjectDirectory = Path.GetDirectoryName(TempProjectPath)!;
        var tempProjectFileNamePrefix = Path.GetFileName(TempProjectPath);
        foreach (var file in Directory.EnumerateFiles(tempProjectDirectory, tempProjectFileNamePrefix + "*"))
        {
            try
            {
                IOFile.Delete(file);
            }
            catch
            {
                // Best-effort cleanup only - EA may still briefly hold a handle open right after exit.
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class EaTestCollection : ICollectionFixture<EaTestFixture>
{
    public const string Name = "EA automation collection";
}
