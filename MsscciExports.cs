using LibGit2Sharp;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Linq;
using System.Collections.Generic;

namespace MSSCCforGIT;

public static class MsscciExports
{
    // MSSCCI Return Codes (SCC_OK, SCC_E_UNKNOWNERROR, etc.)
    private const int SCC_OK = 0;
    private const int SCC_E_UNKNOWNERROR = -1;
    private const int SCC_E_NONSPECIFICERROR = -2;
    private const int SCC_E_FILENOTCONTROLLED = -19;
    private const int SCC_E_NOTAUTHORIZED = -6;
    private const int SCC_E_INITIALIZATIONFAILED = -5;
    private const int SCC_E_OPNOTPERFORMED = -30;

    // SCC status bit flags (returned via SccQueryInfo)
    private const int SCC_STATUS_INVALID = -1;
    private const int SCC_STATUS_NOTCONTROLLED = 0x0000;
    private const int SCC_STATUS_CONTROLLED = 0x0001;
    private const int SCC_STATUS_CHECKEDOUT = 0x0002;
    private const int SCC_STATUS_OUTBYOTHER = 0x0004;
    private const int SCC_STATUS_OUTMULTIPLE = 0x0008;
    private const int SCC_STATUS_OUTEXCLUSIVE = 0x0010;
    private const int SCC_STATUS_DIFFERENT = 0x0080;
    private const int SCC_STATUS_NOTINPROJECT = 0x0200;

    private static bool s_nativeDllDirectoryInitialized;

    /// <summary>
    /// Debounced-commit state: EA never actually calls SccBeginBatch/SccEndBatch around a
    /// multi-file "checkin branch" operation (confirmed empirically - zero occurrences logged even
    /// with diagnostics added to both), it simply calls SccCheckin repeatedly in quick succession,
    /// once per selected file. To still produce a single combined commit+push for such a batch, we
    /// accumulate staged files per repo and (re)schedule a delayed flush on every SccCheckin call;
    /// if another SccCheckin arrives before the delay elapses, the timer is reset and that file
    /// joins the same pending commit. EA also calls SccQueryInfo immediately after every single
    /// SccCheckin (even mid-batch) purely to refresh each file's status icon, so that specific call
    /// only extends the debounce window rather than flushing - it is not a sign EA has moved on.
    /// Any OTHER Scc* call (SccAdd, SccCheckout, SccGet, SccRemove, SccRename, SccUncheckout,
    /// SccHistory) arriving while a batch is pending means EA has finished this checkin operation
    /// and started doing something else, so the pending batch is flushed immediately at that point
    /// rather than waiting for the debounce window to elapse.
    /// </summary>
    private static readonly object s_pendingCheckinLock = new();
    private static readonly Dictionary<string, List<string>> s_pendingFilesByRepo = new(StringComparer.OrdinalIgnoreCase);
    private static string? s_pendingComment;
    private static System.Threading.Timer? s_pendingCheckinTimer;
    private static readonly TimeSpan PendingCheckinDebounce = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Resets the pending-checkin debounce timer if a batch is currently accumulating. Called only
    /// from SccQueryInfo, since EA issuing that call right after SccCheckin is a routine per-file
    /// status refresh rather than a sign EA has moved on to a different operation.
    /// </summary>
    private static void ExtendPendingCheckinTimerIfActive()
    {
        lock (s_pendingCheckinLock)
        {
            if (s_pendingFilesByRepo.Count == 0 || s_pendingCheckinTimer == null) return;

            s_pendingCheckinTimer.Change(PendingCheckinDebounce, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Immediately flushes (commits+pushes) any pending checkin batch, if one is accumulating. Called
    /// from every Scc* entry point other than SccCheckin/SccQueryInfo, since EA calling any of those
    /// while a batch is pending means it has finished the checkin operation and moved on to
    /// something else - so the pending batch should not wait out the debounce window any longer.
    /// Safe to call unconditionally; it is a no-op when no batch is pending.
    /// </summary>
    private static void FlushPendingCheckinsNowIfActive()
    {
        Dictionary<string, List<string>>? filesByRepo = null;
        string? comment = null;

        lock (s_pendingCheckinLock)
        {
            if (s_pendingFilesByRepo.Count == 0) return;

            filesByRepo = new Dictionary<string, List<string>>(s_pendingFilesByRepo, StringComparer.OrdinalIgnoreCase);
            comment = s_pendingComment;

            s_pendingFilesByRepo.Clear();
            s_pendingComment = null;
            s_pendingCheckinTimer?.Dispose();
            s_pendingCheckinTimer = null;
        }

        CommitAndPushFiles(filesByRepo, comment ?? "EA Checkin");
    }

    /// <summary>
    /// Caches the repository root path established by SccOpenProject, keyed by the pContext EA
    /// passes back into every subsequent Scc* call. This is needed because some EA calls (e.g.
    /// SccAdd) only supply a bare relative file/package name rather than a full absolute path, so
    /// we must resolve it against the project root that was opened earlier.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, string> s_projectRootsByContext = new();

    /// <summary>
    /// Tracks files added via SccAdd with EA's "Keep checked out" option ticked. Git has no
    /// exclusive/local-only staging concept like the centralized VCS model MSSCCI assumes, so we
    /// still commit+push the file immediately like a normal add (otherwise it would never reach the
    /// remote and other users/EA instances wouldn't see it as added at all) - but we remember it
    /// here so SccQueryInfo keeps reporting it as checked out afterward, matching what the user
    /// asked for and letting them continue editing without needing to check it out again. Cleared
    /// once the file is genuinely checked in again via SccCheckin.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> s_keptCheckedOutFiles =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Remembers the most recently resolved full file path from SccQueryInfo, along with when it
    /// was resolved. EA reliably calls SccQueryInfo for a file right before SccAdd for that same
    /// file (confirmed via diagnostics), so when SccAdd's own memory-pointer scan fails to recover
    /// a usable full path (observed to happen intermittently - EA's buffer layout varies across
    /// calls and the real path isn't always within the scanned window), this cached path from the
    /// immediately preceding SccQueryInfo call is a much more reliable fallback than continuing to
    /// guess from raw memory.
    /// </summary>
    private static string? s_lastQueryInfoResolvedPath;
    private static DateTime s_lastQueryInfoResolvedAt;

    /// <summary>
    /// Fallback project root used when pContext isn't a reliable/stable key (observed to vary or
    /// be zero across some EA calls). Since this provider only supports one open project at a
    /// time in practice, tracking the most recently opened root as a fallback is sufficient.
    /// </summary>
    private static string? s_lastOpenedProjectRoot;

    /// <summary>
    /// Runs automatically when this assembly is loaded (before any exported Scc* function can be
    /// invoked), ensuring our own directory is added to the native DLL search path so LibGit2Sharp's
    /// git2-*.dll (which sits next to us, not next to the host EA.exe) can be found.
    /// </summary>
#pragma warning disable CA2255 // Used intentionally in this native-hosted library to set up the
                               // DLL search path before EA calls into any exported Scc* function.
    [ModuleInitializer]
    internal static void Initialize()
    {
        EnsureNativeDllDirectory();
    }
#pragma warning restore CA2255

    /// <summary>
    /// When EA (or any host process) loads this provider DLL, the default DLL search path only
    /// includes the host EXE's directory - not ours. Since LibGit2Sharp's native git2-*.dll sits
    /// alongside our own DLL (not EA.exe), P/Invokes into it fail with DllNotFoundException unless
    /// we explicitly add our own directory to the search path first.
    /// </summary>
    private static unsafe void EnsureNativeDllDirectory()
    {
        if (s_nativeDllDirectoryInitialized) return;

        nint codeAddress = (nint)(delegate*<void>)&EnsureNativeDllDirectoryMarker;

        if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, codeAddress, out IntPtr hModule) && hModule != IntPtr.Zero)
        {
            Span<char> buffer = stackalloc char[1024];
            int len;
            fixed (char* pBuffer = buffer)
            {
                len = GetModuleFileNameW(hModule, pBuffer, buffer.Length);
            }

            if (len > 0)
            {
                string modulePath = new string(buffer.Slice(0, len));
                string? directory = Path.GetDirectoryName(modulePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    AddDllDirectory(directory);
                    SetDllDirectory(directory);
                }
            }
        }

        s_nativeDllDirectoryInitialized = true;
    }

    /// <summary>
    /// Dummy marker method whose JITted/AOT-compiled code address is used purely to identify
    /// which native module (our DLL) this code lives in via GetModuleHandleEx.
    /// </summary>
    private static void EnsureNativeDllDirectoryMarker() { }

    private const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetModuleHandleExW(uint dwFlags, nint lpModuleName, out IntPtr phModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static unsafe extern int GetModuleFileNameW(IntPtr hModule, char* lpFilename, int nSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AddDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    /// <summary>
    /// EA calls this first to get the MSSCCI spec version supported (e.g., version 1.3 -> 0x00010300).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccGetVersion", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int SccGetVersion()
    {
        return 0x00010300; // MSSCCI v1.3
    }

    /// <summary>
    /// Called when EA initializes the SCC context.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccInitialize", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccInitialize(
        IntPtr* ppContext,
        IntPtr hWnd,
        sbyte* lpCallerName,
        sbyte* lpSccName,
        int* lpSccCaps,
        sbyte* lpAuxPathLabel,
        int* pnCheckoutCommentLen,
        int* pnCommentLen)
    {
        try
        {
            // Write provider name into the buffer EA provides
            string providerName = "Git Native MSSCCI";
            Marshal.Copy(System.Text.Encoding.ASCII.GetBytes(providerName + '\0'), 0, (IntPtr)lpSccName, providerName.Length + 1);

            // Set flags capabilities (e.g., supports checkouts, comments)
            if (lpSccCaps != null)
            {
                *lpSccCaps = 0x00000001; // SCC_CAP_CHECKOUT
            }

            // EA expects these to report the max buffer lengths it should allocate for
            // checkout/checkin comment text; leaving them unset can confuse the caller.
            if (pnCheckoutCommentLen != null) *pnCheckoutCommentLen = 2048;
            if (pnCommentLen != null) *pnCommentLen = 2048;

            // EA stores this context handle and passes it back on every subsequent call.
            // We don't need per-session state, but it must be a non-null, stable value.
            if (ppContext != null) *ppContext = (IntPtr)1;

            return SCC_OK;
        }
        catch (Exception ex)
        {
            LogDiagnostic("SccInitialize", ex);
            return SCC_E_INITIALIZATIONFAILED;
        }
    }

    /// <summary>
    /// Temporary diagnostic helper: writes exception details to a log file next to the DLL so we
    /// can see the real cause of failures that would otherwise be swallowed as generic SCC error codes.
    /// </summary>
    private static void LogDiagnostic(string context, Exception ex)
    {
        try
        {
            string logPath = Path.Combine(Path.GetTempPath(), "MSSCCforGIT.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:O}] {context}: {ex}\n\n");
        }
        catch
        {
            // Best-effort logging only.
        }
    }

    /// <summary>
    /// Map SccCheckin to LibGit2Sharp Stage + Commit + Push
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccCheckin", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccCheckin(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        sbyte* lpComment,
        int fOptions,
        IntPtr pvConfig)
    {
        try
        {
            string comment = lpComment != null ? ReadPlainAnsiString((IntPtr)lpComment) : "EA Checkin";
            if (string.IsNullOrWhiteSpace(comment)) comment = "EA Checkin";

            LogDiagnostic("SccCheckin.Entry", new Exception($"nFiles={nFiles} comment='{comment}' fOptions={fOptions}"));

            // Group files by repo first so that a multi-file checkin (e.g. "checkin branch",
            // which selects every changed package under a branch at once) results in a single
            // commit + push per repo, rather than one commit+push per individual file. EA doesn't
            // distinguish between these at the SCC level - it just calls SccCheckin once with all
            // selected files - so combining them here matches the "one commit for this checkin"
            // expectation a user has when checking in a whole branch.
            var filesByRepo = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < nFiles; i++)
            {
                string filePath = ResolveEaPath(ReadPlainAnsiString((IntPtr)lpFileNames[i]), pContext);

                string? repoPath = DiscoverRepositoryPath(filePath);
                LogDiagnostic("SccCheckin", new Exception($"filePath='{filePath}' repoPath='{repoPath}'"));
                if (repoPath == null) continue;

                if (!filesByRepo.TryGetValue(repoPath, out List<string>? files))
                {
                    files = new List<string>();
                    filesByRepo[repoPath] = files;
                }
                files.Add(filePath);

                // A real SccCheckin clears any "keep checked out" flag left over from SccAdd.
                s_keptCheckedOutFiles.TryRemove(filePath, out _);
            }

            // EA calls SccCheckin once per individual file even for a multi-file "checkin branch"
            // operation (confirmed via diagnostics - SccBeginBatch/SccEndBatch are never called),
            // so to get a single combined commit+push for the whole branch we stage this call's
            // files immediately, but debounce the actual commit+push: schedule it to run shortly
            // after this call, and if another SccCheckin arrives before that timer fires, its files
            // are added to the same pending set and the timer is reset. Only once no further
            // SccCheckin arrives within the debounce window do we actually commit+push everything
            // accumulated so far.
            //
            // EA only has a single comment field per checkin operation, so a different comment
            // arriving means this call belongs to a different checkin (either an individual file
            // checked in on its own, or a separate batch) rather than a continuation of the
            // currently pending one. In that case, flush the pending batch immediately - under its
            // original comment - before starting a fresh batch under the new comment.
            Dictionary<string, List<string>>? filesToFlushNow = null;
            string? commentToFlushNow = null;

            lock (s_pendingCheckinLock)
            {
                if (s_pendingComment != null && !string.Equals(s_pendingComment, comment, StringComparison.Ordinal))
                {
                    filesToFlushNow = new Dictionary<string, List<string>>(s_pendingFilesByRepo, StringComparer.OrdinalIgnoreCase);
                    commentToFlushNow = s_pendingComment;

                    s_pendingFilesByRepo.Clear();
                    s_pendingComment = null;
                    s_pendingCheckinTimer?.Dispose();
                    s_pendingCheckinTimer = null;
                }

                s_pendingComment ??= comment;

                foreach (var (repoPath, filePaths) in filesByRepo)
                {
                    if (!s_pendingFilesByRepo.TryGetValue(repoPath, out List<string>? existing))
                    {
                        existing = new List<string>();
                        s_pendingFilesByRepo[repoPath] = existing;
                    }
                    existing.AddRange(filePaths);
                }

                s_pendingCheckinTimer?.Dispose();
                s_pendingCheckinTimer = new System.Threading.Timer(
                    FlushPendingCheckins,
                    null,
                    PendingCheckinDebounce,
                    System.Threading.Timeout.InfiniteTimeSpan);
            }

            if (filesToFlushNow != null && commentToFlushNow != null)
            {
                CommitAndPushFiles(filesToFlushNow, commentToFlushNow);
            }

            return SCC_OK;
        }
        catch (Exception ex)
        {
            LogDiagnostic("SccCheckin", ex);
            return SCC_E_UNKNOWNERROR;
        }
    }

    /// <summary>
    /// Timer callback: commits and pushes everything accumulated in s_pendingFilesByRepo as a
    /// single commit per repo, then clears the pending state. Runs on a thread pool thread once
    /// the debounce window in SccCheckin has elapsed with no further SccCheckin calls.
    /// </summary>
    private static void FlushPendingCheckins(object? state)
    {
        Dictionary<string, List<string>> filesByRepo;
        string comment;

        lock (s_pendingCheckinLock)
        {
            if (s_pendingFilesByRepo.Count == 0) return;

            filesByRepo = new Dictionary<string, List<string>>(s_pendingFilesByRepo, StringComparer.OrdinalIgnoreCase);
            comment = string.IsNullOrWhiteSpace(s_pendingComment) ? "EA Checkin" : s_pendingComment;

            s_pendingFilesByRepo.Clear();
            s_pendingComment = null;
            s_pendingCheckinTimer?.Dispose();
            s_pendingCheckinTimer = null;
        }

        CommitAndPushFiles(filesByRepo, comment);
    }

    /// <summary>
    /// Commits and pushes the given per-repo file sets under the given comment. Used both by the
    /// debounce-timer flush and by SccCheckin itself when it detects a comment change and needs to
    /// flush the previous batch immediately before starting a new one.
    /// </summary>
    private static void CommitAndPushFiles(Dictionary<string, List<string>> filesByRepo, string comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) comment = "EA Checkin";

        foreach (var (repoPath, filePaths) in filesByRepo)
        {
            try
            {
                using var repo = OpenRepositoryEnsuringSafeDirectory(repoPath);
                StageCommitAndPush(repo, filePaths, comment);
            }
            catch (Exception ex)
            {
                LogDiagnostic("CommitAndPushFiles", ex);
            }
        }
    }


    /// <summary>
    /// Stages the given files, commits them all together as a single commit (if any of them have
    /// actual staged changes), and pushes to the "origin" remote if one is configured.
    /// </summary>
    private static void StageCommitAndPush(Repository repo, IReadOnlyList<string> filePaths, string comment)
    {
        // 0. Reset the index to match HEAD first. Without this, any stray entries left staged by a
        // previous operation that failed after staging but before committing (e.g. the earlier
        // "author is null" or "remote authentication required" failures) would get swept into this
        // commit alongside the intended files, resulting in commits that include unrelated files.
        if (repo.Head.Tip != null)
        {
            repo.Reset(ResetMode.Mixed, repo.Head.Tip);
        }

        // 1. Stage only the target files
        bool hasStagedChange = false;
        foreach (string filePath in filePaths)
        {
            Commands.Stage(repo, filePath);

            // Checking the whole-repo IsDirty flag here would be wrong: this repo's working
            // directory can easily have unrelated dirty files (e.g. other in-progress edits, other
            // untracked test files) that make IsDirty always true regardless of whether any of
            // these specific files changed, which was causing "git commit" to fail with "no changes
            // added to commit" whenever EA checked in files that themselves had nothing new to commit.
            string relativePath = GetRelativePath(repo, filePath);
            FileStatus fileStatus = repo.RetrieveStatus(relativePath);
            if (fileStatus.HasFlag(FileStatus.NewInIndex) ||
                fileStatus.HasFlag(FileStatus.ModifiedInIndex) ||
                fileStatus.HasFlag(FileStatus.DeletedFromIndex) ||
                fileStatus.HasFlag(FileStatus.RenamedInIndex) ||
                fileStatus.HasFlag(FileStatus.TypeChangeInIndex))
            {
                hasStagedChange = true;
            }
        }

        // 2. Commit (skip if none of the files actually had staged changes, e.g. all unchanged)
        if (!hasStagedChange)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(comment))
        {
            comment = "EA commit";
        }

        // Commit via the "git" CLI rather than LibGit2Sharp's Repository.Commit. Diagnostics
        // showed the comment string is intact right up until the LibGit2Sharp call (PreCommit log),
        // but the resulting commit object always ends up with an empty message (confirmed via
        // "git cat-file -p" on the raw commit). This points to LibGit2Sharp's commit-message
        // marshaling (it uses a custom ICustomMarshaler internally) not working correctly under
        // NativeAOT, which doesn't support reflection-based custom marshalers. The git CLI, which
        // we already use for push for a similar interop reason, sidesteps this entirely.
        CommitViaGitCli(repo.Info.WorkingDirectory, comment);

        // 3. Push to default remote, if one is configured. A brand-new local-only
        // repository won't have a remote, so pushing is skipped rather than failing.
        Remote? remote = repo.Network.Remotes["origin"];
        if (remote != null)
        {
            PushViaGitCli(repo.Info.WorkingDirectory);
        }
    }

    /// <summary>
    /// Single-file convenience overload of StageCommitAndPush, used by SccAdd where each call
    /// naturally targets a single file and its own commit (add == an implicit individual checkin).
    /// </summary>
    private static void StageCommitAndPush(Repository repo, string filePath, string comment)
    {
        StageCommitAndPush(repo, new[] { filePath }, comment);
    }

    /// <summary>
    /// Commits currently-staged changes using the system "git" CLI rather than LibGit2Sharp's
    /// Repository.Commit, since the latter's commit-message marshaling does not survive under
    /// NativeAOT (see StageCommitAndPush for details).
    /// </summary>
    private static void CommitViaGitCli(string workingDirectory, string comment)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            ArgumentList = { "commit", "-m", comment },
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start 'git commit' process.");
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);

        LogDiagnostic("CommitViaGitCli", new Exception($"exitCode={process.ExitCode} stdout='{stdout}' stderr='{stderr}'"));

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'git commit' failed with exit code {process.ExitCode}: {stderr}");
        }
    }

    /// <summary>
    /// Pushes the current branch to "origin" using the system "git" CLI rather than
    /// LibGit2Sharp's Network.Push. LibGit2Sharp requires an explicit CredentialsHandler and does
    /// not transparently use Windows Git Credential Manager, so it fails with "remote
    /// authentication required but no callback set" even when "git push" from a normal terminal
    /// works fine (because the git CLI does use the configured credential helper).
    /// </summary>
    private static void PushViaGitCli(string workingDirectory)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            ArgumentList = { "push" },
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start 'git push' process.");
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);

        LogDiagnostic("PushViaGitCli", new Exception($"exitCode={process.ExitCode} stdout='{stdout}' stderr='{stderr}'"));

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'git push' failed with exit code {process.ExitCode}: {stderr}");
        }
    }

    /// <summary>
    /// Builds a fallback commit signature when Git's user.name/user.email aren't configured (e.g.
    /// not set in the global/system config visible to EA's process), since
    /// Repository.Config.BuildSignature returns null in that case and Repository.Commit throws
    /// ArgumentNullException rather than defaulting to something usable.
    /// </summary>
    private static Signature GetFallbackSignature(Repository repo)
    {
        string name = repo.Config.Get<string>("user.name")?.Value
            ?? Environment.UserName
            ?? "EA User";
        string email = repo.Config.Get<string>("user.email")?.Value
            ?? $"{Environment.UserName}@{Environment.MachineName}".ToLowerInvariant();

        return new Signature(name, email, DateTimeOffset.Now);
    }

    /// <summary>
    /// Called when EA shuts down the SCC context.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccUninitialize", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccUninitialize(IntPtr pContext)
    {
        return SCC_OK;
    }

    /// <summary>
    /// Called by EA to open/associate a project with a local path. For Git there's no
    /// separate "project" concept, so we just verify a repository exists at the path.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccOpenProject", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccOpenProject(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpUser,
        sbyte* lpProjName,
        sbyte* lpLocalPath,
        sbyte* lpAuxProjPath,
        sbyte* lpComment,
        sbyte* lpTextOut,
        int nTextOutLen,
        int fOption)
    {
        try
        {
            string? projName = lpProjName != null ? Marshal.PtrToStringAnsi((IntPtr)lpProjName) : null;
            string? localPath = lpLocalPath != null ? Marshal.PtrToStringAnsi((IntPtr)lpLocalPath) : null;

            // Prefer the local path if given; some EA versions only populate lpProjName with the
            // path instead. Accept whichever one resolves to a git repository.
            string? candidatePath = !string.IsNullOrEmpty(localPath) ? localPath : projName;
            if (!string.IsNullOrEmpty(candidatePath))
            {
                // Normalize to a full path with trailing directory separator; libgit2's discovery
                // API can behave inconsistently with relative paths or missing trailing slashes.
                candidatePath = Path.GetFullPath(candidatePath);
                if (!candidatePath.EndsWith(Path.DirectorySeparatorChar))
                {
                    candidatePath += Path.DirectorySeparatorChar;
                }
            }

            string? discovered = null;
            try
            {
                discovered = candidatePath != null ? DiscoverRepositoryPath(candidatePath) : null;
            }
            catch (Exception discoverEx)
            {
                LogDiagnostic("SccOpenProject.Discover", discoverEx);
            }

            LogDiagnostic("SccOpenProject", new Exception(
                $"projName='{projName}' localPath='{localPath}' candidatePath='{candidatePath}' discoveredLen={discovered?.Length.ToString() ?? "null"} discovered='{discovered}'"));

            if (string.IsNullOrEmpty(candidatePath) || string.IsNullOrEmpty(discovered))
            {
                return SCC_E_NONSPECIFICERROR;
            }

            s_projectRootsByContext[pContext] = candidatePath!;
            s_lastOpenedProjectRoot = candidatePath;

            return SCC_OK;
        }
        catch (Exception ex)
        {
            LogDiagnostic("SccOpenProject", ex);
            return SCC_E_UNKNOWNERROR;
        }
    }

    /// <summary>
    /// Called when EA closes the project. No git-specific work required.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccCloseProject", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccCloseProject(IntPtr pContext)
    {
        return SCC_OK;
    }

    /// <summary>
    /// Map SccAdd to LibGit2Sharp Stage (git add). Files become tracked but are not
    /// committed until a subsequent SccCheckin.
    /// Real MSSCCI signature: SCCRTN SccAdd(LPVOID, HWND, LONG nFiles, LPCSTR* lpFileNames,
    /// LPCSTR lpComment, LONG* pFlags, LONG fOptions). Our previous signature had an extra
    /// bogus "lpCheckinComment" parameter and a too-narrow pFlags type, which desynchronized
    /// the stdcall stack layout and caused us to read garbage pointers for lpFileNames.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccAdd", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccAdd(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        sbyte* lpComment,
        int* pFlags,
        int fOptions)
    {
        string comment = lpComment != null ? ReadPlainAnsiString((IntPtr)lpComment) : "EA Add";
        if (string.IsNullOrWhiteSpace(comment)) comment = "EA Add";

        try
        {
            for (int i = 0; i < nFiles; i++)
            {
                // SccAdd's own lpFileNames pointer has proven unreliable to read directly (EA's
                // buffer layout for it varies across calls and doesn't consistently contain the
                // real path), so we no longer rely on it for path resolution - only the path cached
                // from the preceding, reliable SccQueryInfo call is used (see AddCore). This log
                // line just records what SccAdd itself supplied, for diagnostic comparison.
                IntPtr rawPtr = (IntPtr)lpFileNames[i];
                int pFlagsValue = pFlags != null ? pFlags[i] : 0;
                LogDiagnostic("SccAdd.Entry", new Exception($"nFiles={nFiles} index={i} rawPtr=0x{rawPtr:X} comment='{comment}' fOptions=0x{fOptions:X} pFlags[i]=0x{pFlagsValue:X}"));
            }
        }
        catch (Exception ex)
        {
            LogDiagnostic("SccAdd.Entry", ex);
        }

        return AddCore(nFiles, lpFileNames, pContext, comment, pFlags);
    }

    /// <summary>
    /// MSSCCI assumes a centralized VCS model (e.g. Visual SourceSafe) where there's no local-only
    /// staging concept: adding a file to source control also implicitly checks it in. Translated to
    /// Git, SccAdd must therefore stage, commit, AND push - not just stage - otherwise the file
    /// never reaches the remote even though EA reports it as successfully added. This applies even
    /// when EA's "Keep checked out" checkbox is ticked: Git has no real exclusive-lock/local-only
    /// concept to honor, so the file is still committed+pushed like a normal add, but it's recorded
    /// in s_keptCheckedOutFiles so SccQueryInfo continues reporting it as checked out afterward.
    /// Confirmed via diagnostics that fOptions is always 0 regardless of this checkbox, but
    /// pFlags[i] (nominally documented as per-file type flags e.g. text/binary) carries bit 0x1000
    /// when it's ticked (0x0 otherwise) - so we key off that bit per file instead.
    /// Note: pFlags is otherwise an EA-supplied INPUT array and must not be written to - an earlier
    /// attempt to write success/error codes into it corrupted adjacent EA-owned memory used for the
    /// file name buffers on subsequent calls, so writing to it has been removed; here it is only read.
    /// </summary>
    private static unsafe int AddCore(int nFiles, sbyte** lpFileNames, IntPtr pContext, string comment, int* pFlags = null)
    {
        // A new SccAdd arriving means EA has moved on from any pending checkin batch; flush now.
        FlushPendingCheckinsNowIfActive();

        const int SCC_FILE_KEEPCHECKEDOUT = 0x1000;

        try
        {
            for (int i = 0; i < nFiles; i++)
            {
                // EA reliably calls SccQueryInfo for a file immediately before calling SccAdd for
                // that same file (confirmed via diagnostics), and that call's path resolution has
                // proven completely reliable. SccAdd's own file-name pointer, by contrast, has
                // proven unreliable to scan directly (EA's buffer layout for it varies across calls
                // and doesn't always contain the real path), so we no longer attempt that at all and
                // rely solely on the path cached from the preceding SccQueryInfo call.
                if (s_lastQueryInfoResolvedPath == null ||
                    DateTime.UtcNow - s_lastQueryInfoResolvedAt >= TimeSpan.FromSeconds(5))
                {
                    LogDiagnostic("AddCore", new Exception($"index={i} skipped: no recent SccQueryInfo-resolved path available"));
                    continue;
                }

                string filePath = s_lastQueryInfoResolvedPath;

                bool keepCheckedOut = pFlags != null && (pFlags[i] & SCC_FILE_KEEPCHECKEDOUT) != 0;

                string? repoPath = DiscoverRepositoryPath(filePath);
                LogDiagnostic("AddCore", new Exception($"filePath='{filePath}' repoPath='{repoPath}' keepCheckedOut={keepCheckedOut}"));
                if (repoPath == null)
                {
                    continue;
                }

                using var repo = OpenRepositoryEnsuringSafeDirectory(repoPath);

                StageCommitAndPush(repo, filePath, comment);

                if (keepCheckedOut)
                {
                    s_keptCheckedOutFiles[filePath] = 0;
                }
                else
                {
                    s_keptCheckedOutFiles.TryRemove(filePath, out _);
                }
            }

            return SCC_OK;
        }
        catch (Exception ex)
        {
            LogDiagnostic("AddCore", ex);
            return SCC_E_UNKNOWNERROR;
        }
    }

    /// <summary>
    /// Map SccRemove to LibGit2Sharp Remove (git rm). Staged for deletion; committed on next checkin.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccRemove", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccRemove(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        sbyte* lpComment,
        int fOptions)
    {
        return ForEachFileRepo(nFiles, lpFileNames, pContext, (repo, filePath) =>
        {
            Commands.Remove(repo, filePath, removeFromWorkingDirectory: true);
        });
    }

    /// <summary>
    /// Map SccRename to a filesystem move plus LibGit2Sharp Stage of old and new paths (git mv semantics).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccRename", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccRename(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpFileName,
        sbyte* lpNewName)
    {
        FlushPendingCheckinsNowIfActive();

        try
        {
            string oldPath = ResolveEaPath(ReadPlainAnsiString((IntPtr)lpFileName), pContext);
            string newPath = ResolveEaPath(ReadPlainAnsiString((IntPtr)lpNewName), pContext);

            string? repoPath = DiscoverRepositoryPath(oldPath);
            if (repoPath == null) return SCC_E_FILENOTCONTROLLED;

            using var repo = OpenRepositoryEnsuringSafeDirectory(repoPath);

            if (File.Exists(oldPath) && !File.Exists(newPath))
            {
                File.Move(oldPath, newPath);
            }

            Commands.Stage(repo, oldPath);
            Commands.Stage(repo, newPath);

            return SCC_OK;
        }
        catch
        {
            return SCC_E_UNKNOWNERROR;
        }
    }

    /// <summary>
    /// Git has no exclusive locking model, so SccCheckout simply ensures the file exists
    /// in the working directory and is up to date; no lock is taken.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccCheckout", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccCheckout(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        sbyte* lpComment,
        int fOptions,
        IntPtr pvConfig)
    {
        LogDiagnostic("SccCheckout.Entry", new Exception($"nFiles={nFiles} fOptions=0x{fOptions:X}"));

        return ForEachFileRepo(nFiles, lpFileNames, pContext, (repo, filePath) =>
        {
            // No-op for Git: file is already writable/checked out on disk.
        });
    }

    /// <summary>
    /// Map SccUncheckout to discarding local changes (git checkout -- file), reverting to HEAD.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccUncheckout", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccUncheckout(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        int fOptions)
    {
        return ForEachFileRepo(nFiles, lpFileNames, pContext, (repo, filePath) =>
        {
            string relativePath = GetRelativePath(repo, filePath);
            repo.CheckoutPaths("HEAD", new[] { relativePath }, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
        });
    }

    /// <summary>
    /// Map SccGetLatestVersion to a pull (fetch + merge/fast-forward) from the default remote.
    /// The MSSCCI export name for this operation is "SccGet" (not "SccGetLatestVersion").
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccGet", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccGet(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        int fOptions,
        IntPtr pvConfig)
    {
        return GetCore(nFiles, lpFileNames, pContext);
    }

    private static unsafe int GetCore(int nFiles, sbyte** lpFileNames, IntPtr pContext)
    {
        FlushPendingCheckinsNowIfActive();

        try
        {
            for (int i = 0; i < nFiles; i++)
            {
                string filePath = ResolveEaPath(ReadPlainAnsiString((IntPtr)lpFileNames[i]), pContext);

                string? repoPath = DiscoverRepositoryPath(filePath);
                if (repoPath == null) continue;

                using var repo = OpenRepositoryEnsuringSafeDirectory(repoPath);

                Signature signature = repo.Config.BuildSignature(DateTimeOffset.Now) ?? GetFallbackSignature(repo);
                Commands.Pull(repo, signature, new PullOptions());
            }

            return SCC_OK;
        }
        catch
        {
            return SCC_E_UNKNOWNERROR;
        }
    }

    /// <summary>
    /// Map SccQueryInfo to LibGit2Sharp file status, translated into SCC_STATUS_* flags.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccQueryInfo", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccQueryInfo(
        IntPtr pContext,
        int nFiles,
        sbyte** lpFileNames,
        int* pStatus)
    {
        // EA calls SccQueryInfo right after every SccCheckin to refresh each file's status icon,
        // so its arrival is a reliable sign the current checkin/batch operation is still ongoing;
        // extend the pending-checkin debounce window if one is active.
        ExtendPendingCheckinTimerIfActive();

        try
        {
            for (int i = 0; i < nFiles; i++)
            {
                IntPtr rawPtr = (IntPtr)lpFileNames[i];
                string filePath = ResolveEaPath(ReadFileNamePointer(rawPtr), pContext);

                // Remember this resolved path as a fallback for the SccAdd call EA typically issues
                // immediately afterward for the same file, in case that call's own pointer scan
                // fails to recover a usable path from EA's memory buffer.
                if (Path.IsPathRooted(filePath))
                {
                    s_lastQueryInfoResolvedPath = filePath;
                    s_lastQueryInfoResolvedAt = DateTime.UtcNow;
                }

                string? repoPath = DiscoverRepositoryPath(filePath);

                if (repoPath == null)
                {
                    if (pStatus != null) pStatus[i] = SCC_STATUS_NOTCONTROLLED;
                    continue;
                }

                using var repo = OpenRepositoryEnsuringSafeDirectory(repoPath);
                string relativePath = GetRelativePath(repo, filePath);

                FileStatus fileStatus = repo.RetrieveStatus(relativePath);
                int flags = SCC_STATUS_CONTROLLED;

                if (fileStatus.HasFlag(FileStatus.NewInWorkdir) || fileStatus.HasFlag(FileStatus.Nonexistent))
                {
                    flags = SCC_STATUS_NOTCONTROLLED;
                }
                else if (fileStatus.HasFlag(FileStatus.NewInIndex))
                {
                    // Staged but not yet committed.
                    flags |= SCC_STATUS_CHECKEDOUT | SCC_STATUS_DIFFERENT;
                }
                else if (fileStatus.HasFlag(FileStatus.ModifiedInWorkdir) ||
                         fileStatus.HasFlag(FileStatus.ModifiedInIndex) ||
                         fileStatus.HasFlag(FileStatus.DeletedFromWorkdir) ||
                         fileStatus.HasFlag(FileStatus.DeletedFromIndex))
                {
                    flags |= SCC_STATUS_CHECKEDOUT | SCC_STATUS_DIFFERENT;
                }
                else if (s_keptCheckedOutFiles.ContainsKey(filePath))
                {
                    // File is clean/committed (SccAdd always commits+pushes immediately now, even
                    // with "Keep checked out" ticked, since Git has no real local-only staging
                    // concept to honor), but the user asked to keep it checked out, so report it as
                    // such rather than plain CONTROLLED until a real SccCheckin clears this flag.
                    flags |= SCC_STATUS_CHECKEDOUT;
                }

                if (pStatus != null) pStatus[i] = flags;
                LogDiagnostic("SccQueryInfo", new Exception($"relativePath='{relativePath}' fileStatus={fileStatus} flags={flags}"));
            }

            return SCC_OK;
        }
        catch
        {
            return SCC_E_UNKNOWNERROR;
        }
    }

    /// <summary>
    /// Resolves a file name/path that EA passed for a given pContext into a full absolute path.
    /// If filePath is already absolute AND resolves to a real file/directory, it's returned as-is.
    /// Otherwise (including truncated/corrupted-prefix paths like "ocuments\GitHub\Proj\file.xml",
    /// which can occur because EA's in-memory string layout sometimes clips the leading portion of
    /// the real path), we extract just the trailing file name and combine it with the project root
    /// that was recorded for this pContext by SccOpenProject (or, if that's not available, the most
    /// recently opened project root).
    /// </summary>
    private static string ResolveEaPath(string filePath, IntPtr pContext)
    {
        if (string.IsNullOrEmpty(filePath)) return filePath;

        try
        {
            if (Path.IsPathRooted(filePath) && (File.Exists(filePath) || Directory.Exists(filePath)))
            {
                return filePath;
            }

            string? root = s_projectRootsByContext.TryGetValue(pContext, out var cachedRoot)
                ? cachedRoot
                : s_lastOpenedProjectRoot;

            if (!string.IsNullOrEmpty(root))
            {
                // Take just the file name portion (the last path segment) since the leading
                // directory portion may be corrupted/truncated; the trailing segment closest to
                // the actual file name has consistently decoded correctly in testing.
                string fileName = filePath.Contains('\\') ? filePath[(filePath.LastIndexOf('\\') + 1)..] : filePath;
                if (!string.IsNullOrEmpty(fileName))
                {
                    string combined = Path.GetFullPath(Path.Combine(root, fileName));
                    if (File.Exists(combined) || Directory.Exists(Path.GetDirectoryName(combined)))
                    {
                        return combined;
                    }
                }
            }

            if (Path.IsPathRooted(filePath))
            {
                return filePath;
            }
        }
        catch
        {
            // Fall through and return the original filePath unresolved.
        }

        return filePath;
    }

    /// <summary>
    /// Reads a plain, null-terminated ANSI string at the given pointer. Used for comment pointers
    /// (lpComment), which diagnostics confirmed EA always passes directly at the given address with
    /// no wrapping/offset needed - unlike file name pointers (see ReadFileNamePointer below), which
    /// do require a scanning fallback.
    /// </summary>
    private static unsafe string ReadPlainAnsiString(IntPtr rawPtr)
    {
        if (rawPtr == IntPtr.Zero) return "";
        return Marshal.PtrToStringAnsi(rawPtr) ?? "";
    }

    /// <summary>
    /// Reads a file name pointer as passed by EA. EA's lpFileNames buffers are frequently
    /// truncated/corrupted at the exact address given (e.g. only 'C:\Users' or similar garbage),
    /// but diagnostic testing showed a complete or more complete copy of the real path often exists
    /// a little further along in the same allocation/memory window. A plain direct read at the
    /// given pointer alone therefore isn't reliable, unlike for comment pointers - it only "happens"
    /// to work when the string at offset 0 isn't corrupted for that particular call, which isn't
    /// consistent across calls.
    /// Instead, we scan a bounded window of memory after rawPtr for every position that looks like
    /// the start of an absolute path (a drive letter like "C:\" or a UNC prefix "\\"), decode a
    /// null-terminated ANSI string at each candidate position, and pick the candidate that actually
    /// resolves to a real file or directory on disk. If no candidate resolves, we fall back to the
    /// longest "path-like" fragment found (contains a separator and a dot), then the longest fully
    /// printable candidate, then finally to treating rawPtr itself as a plain null-terminated ANSI
    /// string.
    /// </summary>
    private static unsafe string ReadFileNamePointer(IntPtr rawPtr)
    {
        if (rawPtr == IntPtr.Zero) return "";

        // 260 (MAX_PATH-ish) was originally enough to find the real path in EA's buffer, but
        // diagnostics later showed calls where only a short garbage prefix ("C:\Users.") exists
        // within that window and the real path fragment must sit further away in memory - so the
        // window is widened considerably to make recovery more reliable across EA's memory layout
        // variations without meaningfully increasing scan cost.
        const int scanWindow = 4096;
        try
        {
            byte[] window = new byte[scanWindow];
            Marshal.Copy(rawPtr, window, 0, scanWindow);

            string? bestExistingCandidate = null;
            string? bestPrintableCandidate = null;
            string? bestPathLikeCandidate = null;
            for (int offset = 0; offset < scanWindow - 1; offset++)
            {
                // Only consider offsets that start a run of printable ASCII (i.e. the previous
                // byte is not itself printable), so we find each distinct embedded string once,
                // not every suffix of it.
                bool isStringStart = offset == 0 || window[offset - 1] < 32 || window[offset - 1] >= 127;
                if (!isStringStart || window[offset] < 32 || window[offset] >= 127) continue;

                string candidate = Marshal.PtrToStringAnsi(rawPtr + offset) ?? "";
                if (candidate.Length == 0 || !candidate.All(c => c >= 32 && c < 127)) continue;

                bool looksLikeDriveLetter = candidate.Length >= 3 && char.IsLetter(candidate[0]) && candidate[1] == ':' && candidate[2] == '\\';
                bool looksLikeUncPrefix = candidate.Length >= 2 && candidate[0] == '\\' && candidate[1] == '\\';
                bool looksPathLike = candidate.Contains('\\') && candidate.Contains('.');

                if (looksLikeDriveLetter || looksLikeUncPrefix)
                {
                    // Track the longest candidate that actually exists on disk as a file or
                    // directory in its own right. We deliberately do NOT fall back to "does the
                    // parent directory exist", since for a short garbage/truncated candidate like
                    // "C:\Users(" that check is nearly always true (GetDirectoryName collapses it
                    // to "C:\", which obviously exists) and would wrongly let this candidate win
                    // over a longer, more complete candidate found elsewhere in the scan window.
                    try
                    {
                        if (File.Exists(candidate) || Directory.Exists(candidate))
                        {
                            if (bestExistingCandidate == null || candidate.Length > bestExistingCandidate.Length)
                            {
                                bestExistingCandidate = candidate;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore invalid-path exceptions from GetDirectoryName and keep scanning.
                    }
                }

                // Track the longest "path-like" fragment (contains a separator and a dot), even if
                // it doesn't start with a recognizable drive/UNC prefix and isn't itself a valid
                // full path - EA's memory layout sometimes clips the leading portion of the real
                // path (e.g. "ocuments\GitHub\Project\file.xml" missing "C:\Users\name\D"). The
                // trailing file name extracted from this is still reliable even when the prefix isn't.
                if (looksPathLike && (bestPathLikeCandidate == null || candidate.Length > bestPathLikeCandidate.Length))
                {
                    bestPathLikeCandidate = candidate;
                }

                if (bestPrintableCandidate == null || candidate.Length > bestPrintableCandidate.Length)
                {
                    bestPrintableCandidate = candidate;
                }
            }

            if (bestExistingCandidate != null)
            {
                return bestExistingCandidate;
            }

            if (bestPathLikeCandidate != null)
            {
                return bestPathLikeCandidate;
            }

            if (bestPrintableCandidate != null)
            {
                return bestPrintableCandidate;
            }
        }
        catch
        {
            // Fall through to plain ANSI interpretation below.
        }

        return Marshal.PtrToStringAnsi(rawPtr) ?? "";
    }

    /// <summary>
    /// Shared helper: resolves the git repository for each file path and invokes an action against it.
    /// </summary>
    private static unsafe int ForEachFileRepo(int nFiles, sbyte** lpFileNames, IntPtr pContext, Action<Repository, string> action)
    {
        // Called by SccRemove/SccCheckout/SccUncheckout; any of these arriving means EA has moved
        // on from any pending checkin batch, so flush it immediately.
        FlushPendingCheckinsNowIfActive();

        try
        {
            for (int i = 0; i < nFiles; i++)
            {
                IntPtr rawPtr = (IntPtr)lpFileNames[i];
                string filePath = ResolveEaPath(ReadPlainAnsiString(rawPtr), pContext);

                string? repoPath = DiscoverRepositoryPath(filePath);
                LogDiagnostic("ForEachFileRepo", new Exception($"filePath='{filePath}' repoPath='{repoPath}'"));
                if (repoPath == null) continue;

                using var repo = OpenRepositoryEnsuringSafeDirectory(repoPath);
                action(repo, filePath);
            }

            return SCC_OK;
        }
        catch (Exception ex)
        {
            LogDiagnostic("ForEachFileRepo", ex);
            return SCC_E_UNKNOWNERROR;
        }
    }

    /// <summary>
    /// Converts an absolute file path into a path relative to the repository working directory,
    /// as required by most LibGit2Sharp APIs.
    /// </summary>
    private static string GetRelativePath(Repository repo, string filePath)
    {
        string fullWorkDir = Path.GetFullPath(repo.Info.WorkingDirectory);
        string fullFile = Path.GetFullPath(filePath);
        return Path.GetRelativePath(fullWorkDir, fullFile).Replace('\\', '/');
    }

    /// <summary>
    /// Walks up from the given file or directory path looking for a ".git" entry, returning the
    /// repository root directory if found. LibGit2Sharp's Repository.Discover relies on marshaling
    /// a native git_buf out-parameter that does not work correctly under NativeAOT (it always
    /// returns null/empty even when a repository exists), so we implement discovery manually here.
    /// The Repository constructor itself works fine under NativeAOT and is used once the root
    /// directory has been found.
    /// </summary>
    private static string? DiscoverRepositoryPath(string path)
    {
        try
        {
            string? current = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            current = current != null ? Path.GetFullPath(current) : null;

            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(Path.Combine(current, ".git")) || File.Exists(Path.Combine(current, ".git")))
                {
                    return current;
                }

                string? parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break; // Reached the filesystem root.
                }

                current = parent;
            }
        }
        catch
        {
            // Fall through to return null on any path-resolution error.
        }

        return null;
    }

    /// <summary>
    /// Opens a Repository, automatically adding the path to Git's global "safe.directory" list
    /// and retrying if libgit2 refuses to open it with "repository path ... is not owned by
    /// current user". This ownership check can trigger under EA because the provider DLL may run
    /// with a different effective user/profile context than the one that originally cloned the repo.
    /// </summary>
    private static Repository OpenRepositoryEnsuringSafeDirectory(string repoPath)
    {
        try
        {
            return new Repository(repoPath);
        }
        catch (LibGit2SharpException ex) when (ex.Message.Contains("not owned by current user", StringComparison.OrdinalIgnoreCase))
        {
            LogDiagnostic("OpenRepositoryEnsuringSafeDirectory", new Exception($"Adding safe.directory for '{repoPath}' after ownership error: {ex.Message}"));
            AddSafeDirectory(repoPath);
            return new Repository(repoPath);
        }
    }

    /// <summary>
    /// Adds the given path to the current user's global Git "safe.directory" list via "git config
    /// --global --add safe.directory", which libgit2's ownership validation also honors.
    /// </summary>
    private static void AddSafeDirectory(string repoPath)
    {
        try
        {
            string normalizedPath = repoPath.Replace('\\', '/');
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                ArgumentList = { "config", "--global", "--add", "safe.directory", normalizedPath },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            LogDiagnostic("AddSafeDirectory", ex);
        }
    }

    // -------------------------------------------------------------------------------------------
    // The following entry points are part of the mandatory MSSCCI export table. EA resolves every
    // one of them via GetProcAddress when the provider DLL is loaded ("InitProcPointers"); if any
    // is missing, EA refuses to initialize the provider at all. Where the underlying feature has
    // no direct git-native counterpart wired up yet, these are implemented as safe stubs.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves the local project path for a given project name. Since Git has no separate
    /// "project" indirection, we just echo back the requested local path unchanged.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccGetProjPath", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccGetProjPath(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpProjName,
        sbyte* lpLocalPath,
        int nLocalPathLen,
        int* pbNew,
        int fFlags)
    {
        if (pbNew != null) *pbNew = 0; // Not a new project.
        return SCC_OK;
    }

    /// <summary>
    /// Reports which command-line style options this provider supports for a given command.
    /// We don't expose any provider-specific options dialog, so return 0 (no extra options).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccGetCommandOptions", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccGetCommandOptions(
        IntPtr pContext,
        IntPtr hWnd,
        int nCommand)
    {
        return SCC_OK;
    }

    /// <summary>
    /// Shows a diff for a file. Not yet implemented against LibGit2Sharp; report as not performed
    /// so EA can surface a clear message rather than silently doing nothing.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccDiff", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccDiff(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpFileName,
        int fOptions)
    {
        return SCC_E_OPNOTPERFORMED;
    }

    /// <summary>
    /// Shows revision history for one or more files. There's no dedicated MSSCCI history UI
    /// callback for us to populate, so we shell out to "git log" for each file's relative path and
    /// display the resulting entries in a WinForms ListView dialog owned by EA's window.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccHistory", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccHistory(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        int fOptions)
    {
        FlushPendingCheckinsNowIfActive();

        try
        {
            for (int i = 0; i < nFiles; i++)
            {
                string filePath = ResolveEaPath(ReadPlainAnsiString((IntPtr)lpFileNames[i]), pContext);

                string? repoPath = DiscoverRepositoryPath(filePath);
                LogDiagnostic("SccHistory", new Exception($"filePath='{filePath}' repoPath='{repoPath}'"));
                if (repoPath == null) continue;

                using var repo = OpenRepositoryEnsuringSafeDirectory(repoPath);
                string relativePath = GetRelativePath(repo, filePath);

                List<HistoryEntry> entries = GetFileHistoryViaGitCli(repo.Info.WorkingDirectory, relativePath);
                MsscciDialogs.ShowHistory(hWnd, relativePath, entries);
            }

            return SCC_OK;
        }
        catch (Exception ex)
        {
            LogDiagnostic("SccHistory", ex);
            return SCC_E_UNKNOWNERROR;
        }
    }

    // Field separator unlikely to appear in commit metadata, used to split "git log" output into
    // discrete columns for the History/Properties dialogs.
    private const string GitLogFieldSeparator = "\u001f";

    /// <summary>
    /// Runs "git log" for a single file (relative to the repo working directory) and parses its
    /// stdout into structured entries, using the same git-CLI approach as commit/push to sidestep
    /// LibGit2Sharp interop issues under NativeAOT.
    /// </summary>
    private static List<HistoryEntry> GetFileHistoryViaGitCli(string workingDirectory, string relativePath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            ArgumentList =
            {
                "log", "--follow", "--date=short",
                $"--pretty=format:%h{GitLogFieldSeparator}%ad{GitLogFieldSeparator}%an{GitLogFieldSeparator}%s",
                "--", relativePath,
            },
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null)
        {
            LogDiagnostic("GetFileHistoryViaGitCli", new Exception("failed to start 'git log'"));
            return new List<HistoryEntry>();
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);

        LogDiagnostic("GetFileHistoryViaGitCli", new Exception($"exitCode={process.ExitCode} stdout='{stdout}' stderr='{stderr}'"));

        var entries = new List<HistoryEntry>();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return entries;
        }

        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(GitLogFieldSeparator, 4);
            if (parts.Length == 4)
            {
                entries.Add(new HistoryEntry(parts[0], parts[1], parts[2], parts[3]));
            }
        }

        return entries;
    }

    /// <summary>
    /// Shows provider-specific file properties: the file's Git-relative path, current status, and
    /// last commit details (hash/author/date/message), via the same git-CLI approach used for
    /// commit/push/history to sidestep LibGit2Sharp interop issues under NativeAOT.
    /// </summary>
    /// <remarks>
    /// Although the MSSCCI header declares SccProperties as returning void, EA's dispatch layer
    /// evidently reads back a return code from this call like it does for the other Scc* entry
    /// points, and reports whatever garbage was left in the return register as an "Unknown SCC
    /// Error" (e.g. 767471944) since we were never explicitly setting one. Returning an explicit
    /// SCC_OK avoids that spurious error dialog.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "SccProperties", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccProperties(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpFileName)
    {
        try
        {
            string filePath = ResolveEaPath(ReadPlainAnsiString((IntPtr)lpFileName), pContext);

            string? repoPath = DiscoverRepositoryPath(filePath);
            LogDiagnostic("SccProperties", new Exception($"filePath='{filePath}' repoPath='{repoPath}'"));
            if (repoPath == null)
            {
                MsscciDialogs.ShowProperties(hWnd, filePath, filePath, "(not in a Git repository)", "Not controlled", null);
                return SCC_OK;
            }

            using var repo = OpenRepositoryEnsuringSafeDirectory(repoPath);
            string relativePath = GetRelativePath(repo, filePath);
            FileStatus fileStatus = repo.RetrieveStatus(relativePath);

            List<HistoryEntry> lastCommitEntries = GetFileHistoryViaGitCli(repo.Info.WorkingDirectory, relativePath);
            HistoryEntry? lastCommit = lastCommitEntries.Count > 0 ? lastCommitEntries[0] : null;

            MsscciDialogs.ShowProperties(hWnd, filePath, relativePath, repoPath, fileStatus.ToString(), lastCommit);

            return SCC_OK;
        }
        catch (Exception ex)
        {
            LogDiagnostic("SccProperties", ex);
            return SCC_E_UNKNOWNERROR;
        }
    }

    /// <summary>
    /// Populates an EA project list (e.g., for Add/Remove file pickers) with files under source
    /// control. Not yet implemented; returns no additional files.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccPopulateList", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccPopulateList(
        IntPtr pContext,
        int nCommand,
        int nFiles,
        sbyte** lpFileNames,
        sbyte** lpFileNamesNew,
        int fOptions,
        IntPtr* ppFileList,
        ushort* pdwFlags)
    {
        if (ppFileList != null) *ppFileList = IntPtr.Zero;
        return SCC_OK;
    }

    /// <summary>
    /// Invokes the provider's own UI for a raw command. We have no such UI.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccRunScc", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccRunScc(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames)
    {
        return SCC_E_OPNOTPERFORMED;
    }

    /// <summary>
    /// Lets EA query which optional notification events this provider wants delivered
    /// (e.g., file added/renamed/deleted outside of SCC operations). We don't subscribe to any.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccGetEvents", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccGetEvents(IntPtr pContext)
    {
        return SCC_OK;
    }

    /// <summary>
    /// Sets a provider-specific option (e.g., a UI preference). We have no configurable options.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccSetOption", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccSetOption(
        IntPtr pContext,
        int nOption,
        nint dwVal)
    {
        return SCC_OK;
    }

    // -------------------------------------------------------------------------------------------
    // Extended MSSCCI exports. EA's provider loader resolves the FULL extended export table
    // (not just the base v1.1 set) via GetProcAddress before accepting a provider, matching what
    // other working Git MSSCCI providers (e.g. pbsGitMSSCCI) export. These are implemented as
    // safe no-ops / "not performed" stubs where no direct git-native behavior applies yet.
    // -------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "SccAddFromScc", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccAddFromScc(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        sbyte* lpComment,
        int* pFlags,
        int fOptions)
    {
        string comment = lpComment != null ? ReadPlainAnsiString((IntPtr)lpComment) : "EA Add";
        if (string.IsNullOrWhiteSpace(comment)) comment = "EA Add";
        return AddCore(nFiles, lpFileNames, pContext, comment);
    }

    [UnmanagedCallersOnly(EntryPoint = "SccAddFilesFromSCC", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccAddFilesFromSCC(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        sbyte* lpComment,
        int* pFlags,
        int fOptions)
    {
        string comment = lpComment != null ? ReadPlainAnsiString((IntPtr)lpComment) : "EA Add";
        if (string.IsNullOrWhiteSpace(comment)) comment = "EA Add";
        return AddCore(nFiles, lpFileNames, pContext, comment);
    }

    [UnmanagedCallersOnly(EntryPoint = "SccBackgroundGet", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccBackgroundGet(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        int fOptions,
        IntPtr pvConfig)
    {
        return GetCore(nFiles, lpFileNames, pContext);
    }

    [UnmanagedCallersOnly(EntryPoint = "SccBeginBatch", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccBeginBatch(
        IntPtr pContext,
        IntPtr hWnd,
        int nCommand,
        int nFiles,
        sbyte** lpFileNames)
    {
        // Diagnostics confirmed EA never actually invokes this during a "checkin branch"; batching
        // is instead handled via the debounce mechanism in SccCheckin/FlushPendingCheckins. Logging
        // is kept here in case a future EA version (or a different caller) does use it.
        LogDiagnostic("SccBeginBatch", new Exception($"nCommand={nCommand} nFiles={nFiles}"));
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccEndBatch", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccEndBatch(
        IntPtr pContext,
        IntPtr hWnd,
        int nCommand)
    {
        LogDiagnostic("SccEndBatch", new Exception($"nCommand={nCommand}"));
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccCreateSubProject", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccCreateSubProject(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpProjPath,
        sbyte* lpNewSubProjName)
    {
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccDirDiff", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccDirDiff(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpDirName,
        int fOptions)
    {
        return SCC_E_OPNOTPERFORMED;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccDirQueryInfo", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccDirQueryInfo(
        IntPtr pContext,
        sbyte* lpDirName,
        int* pStatus)
    {
        if (pStatus != null) *pStatus = SCC_STATUS_CONTROLLED;
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccEnumChangedFiles", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccEnumChangedFiles(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpProjName,
        sbyte* lpLocalPath,
        int fOptions,
        IntPtr* ppFileList)
    {
        if (ppFileList != null) *ppFileList = IntPtr.Zero;
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccGetExtendedCapabilities", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccGetExtendedCapabilities(
        IntPtr pContext,
        int nID)
    {
        // No optional extended capabilities are supported.
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccGetParentProjectPath", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccGetParentProjectPath(
        IntPtr pContext,
        sbyte* lpProjName,
        sbyte* lpLocalPath,
        sbyte* lpParentProjName,
        int nParentProjNameLen)
    {
        if (lpParentProjName != null && nParentProjNameLen > 0)
        {
            *lpParentProjName = 0; // Empty string: no parent project.
        }
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccGetUserOption", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccGetUserOption(
        IntPtr pContext,
        IntPtr hWnd,
        int nOption)
    {
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccIsMultiCheckoutEnabled", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccIsMultiCheckoutEnabled(IntPtr pContext)
    {
        // Git has no exclusive checkout locking, so multi-checkout is always effectively enabled.
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccPopulateDirList", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccPopulateDirList(
        IntPtr pContext,
        sbyte* lpDirName,
        int fOptions,
        IntPtr* ppFileList)
    {
        if (ppFileList != null) *ppFileList = IntPtr.Zero;
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccQueryChanges", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccQueryChanges(
        IntPtr pContext,
        sbyte* lpProjName,
        int nFiles,
        sbyte** lpFileNames,
        int fOptions,
        IntPtr* ppFileList)
    {
        if (ppFileList != null) *ppFileList = IntPtr.Zero;
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccRemoveDir", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccRemoveDir(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpDirName,
        sbyte* lpComment,
        int fOptions)
    {
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccWillCreateSccFile", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccWillCreateSccFile(IntPtr pContext)
    {
        // Git doesn't create any provider-specific "scc" marker files.
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "DllRegisterServer", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int DllRegisterServer()
    {
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "DllUnregisterServer", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int DllUnregisterServer()
    {
        return SCC_OK;
    }
}