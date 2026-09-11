using LibGit2Sharp;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Linq;

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
    /// Caches the repository root path established by SccOpenProject, keyed by the pContext EA
    /// passes back into every subsequent Scc* call. This is needed because some EA calls (e.g.
    /// SccAdd) only supply a bare relative file/package name rather than a full absolute path, so
    /// we must resolve it against the project root that was opened earlier.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, string> s_projectRootsByContext = new();

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

            for (int i = 0; i < nFiles; i++)
            {
                string filePath = ResolveEaPath(ReadMfcCStringOrAnsi((IntPtr)lpFileNames[i]), pContext);

                // Find local git repo root from the package path
                string? repoPath = DiscoverRepositoryPath(filePath);
                LogDiagnostic("SccCheckin", new Exception($"filePath='{filePath}' repoPath='{repoPath}'"));
                if (repoPath == null) continue;

                using var repo = OpenRepositoryEnsuringSafeDirectory(repoPath);
                StageCommitAndPush(repo, filePath, comment);
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
    /// Stages the given file, commits (if there are actually staged changes), and pushes to the
    /// "origin" remote if one is configured. Shared by SccCheckin and SccAdd: MSSCCI assumes a
    /// centralized VCS model (e.g. Visual SourceSafe) where adding a file to source control also
    /// implicitly checks it in, so SccAdd must perform the same commit+push as SccCheckin rather
    /// than just staging locally.
    /// </summary>
    private static void StageCommitAndPush(Repository repo, string filePath, string comment)
    {
        // 0. Reset the index to match HEAD first. Without this, any stray entries left staged by a
        // previous operation that failed after staging but before committing (e.g. the earlier
        // "author is null" or "remote authentication required" failures) would get swept into this
        // commit alongside the intended file, resulting in commits that include unrelated files.
        if (repo.Head.Tip != null)
        {
            repo.Reset(ResetMode.Mixed, repo.Head.Tip);
        }

        // 1. Stage only the target file
        Commands.Stage(repo, filePath);

        // 2. Commit (skip if nothing is actually staged, e.g. file unchanged)
        if (!repo.RetrieveStatus().IsDirty)
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

        return AddCore(nFiles, lpFileNames, pContext, comment);
    }

    /// <summary>
    /// MSSCCI assumes a centralized VCS model (e.g. Visual SourceSafe) where there's no local-only
    /// staging concept: adding a file to source control also implicitly checks it in. Translated to
    /// Git, SccAdd must therefore stage, commit, AND push - not just stage - otherwise the file
    /// never reaches the remote even though EA reports it as successfully added.
    /// </summary>
    private static unsafe int AddCore(int nFiles, sbyte** lpFileNames, IntPtr pContext, string comment)
    {
        return ForEachFileRepo(nFiles, lpFileNames, pContext, (repo, filePath) =>
        {
            StageCommitAndPush(repo, filePath, comment);
        });
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
        try
        {
            string oldPath = ResolveEaPath(ReadMfcCStringOrAnsi((IntPtr)lpFileName), pContext);
            string newPath = ResolveEaPath(ReadMfcCStringOrAnsi((IntPtr)lpNewName), pContext);

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
        try
        {
            for (int i = 0; i < nFiles; i++)
            {
                string filePath = ResolveEaPath(ReadMfcCStringOrAnsi((IntPtr)lpFileNames[i]), pContext);

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
        try
        {
            for (int i = 0; i < nFiles; i++)
            {
                string filePath = ResolveEaPath(ReadMfcCStringOrAnsi((IntPtr)lpFileNames[i]), pContext);

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
                    // Freshly staged (e.g. via SccAdd) but not yet committed. Unless the caller
                    // explicitly requested to keep the file checked out, EA expects just
                    // CONTROLLED here - reporting CHECKEDOUT/DIFFERENT for a plain add causes EA
                    // to reject the status with "Unexpected status after adding file".
                }
                else if (fileStatus.HasFlag(FileStatus.ModifiedInWorkdir) ||
                         fileStatus.HasFlag(FileStatus.ModifiedInIndex) ||
                         fileStatus.HasFlag(FileStatus.DeletedFromWorkdir) ||
                         fileStatus.HasFlag(FileStatus.DeletedFromIndex))
                {
                    flags |= SCC_STATUS_CHECKEDOUT | SCC_STATUS_DIFFERENT;
                }

                if (pStatus != null) pStatus[i] = flags;
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
    /// Reads a plain, straightforward null-terminated ANSI string (e.g. a comment). Unlike file
    /// name pointers, EA passes comment pointers directly (confirmed via diagnostics showing
    /// correctly-decoded comments at offset 0), so the path-scanning heuristic in
    /// ReadMfcCStringOrAnsi must NOT be used here: it can pick up an unrelated path-like string
    /// from adjacent memory instead of the actual (possibly short/blank) comment text.
    /// </summary>
    private static unsafe string ReadPlainAnsiString(IntPtr rawPtr)
    {
        if (rawPtr == IntPtr.Zero) return "";
        return Marshal.PtrToStringAnsi(rawPtr) ?? "";
    }

    /// <summary>
    /// EA (Sparx Enterprise Architect) passes file name pointers as raw allocator/string blocks
    /// rather than plain null-terminated LPCSTR pointers directly at the given address. Diagnostic
    /// dumps showed a variable-length, non-obvious header preceding the actual ANSI path text, and
    /// naive heuristics (fixed offsets, "first match wins") produced truncated/incorrect paths.
    /// Instead, we scan a bounded window of memory after rawPtr for every position that looks like
    /// the start of an absolute path (a drive letter like "C:\" or a UNC prefix "\\"), decode a
    /// null-terminated ANSI string at each candidate position, and pick the candidate that actually
    /// resolves to a real file or directory on disk. If no candidate resolves, we fall back to the
    /// longest fully-printable candidate, then finally to treating rawPtr itself as a plain
    /// null-terminated ANSI string.
    /// </summary>
    private static unsafe string ReadMfcCStringOrAnsi(IntPtr rawPtr)
    {
        if (rawPtr == IntPtr.Zero) return "";

        const int scanWindow = 260; // MAX_PATH-ish, generous enough for the longest EA-supplied paths
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
                    // Track the longest candidate that actually exists on disk (file or containing
                    // directory). A truncated prefix like "C:\Users" can itself be a real, existing
                    // directory, so we must keep scanning for a longer, more complete match before
                    // deciding, rather than returning on the first hit.
                    try
                    {
                        if (File.Exists(candidate) || Directory.Exists(candidate) ||
                            Directory.Exists(Path.GetDirectoryName(candidate)))
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
        try
        {
            for (int i = 0; i < nFiles; i++)
            {
                IntPtr rawPtr = (IntPtr)lpFileNames[i];
                string filePath = ResolveEaPath(ReadMfcCStringOrAnsi(rawPtr), pContext);

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
    /// Shows revision history for one or more files. Not yet implemented.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccHistory", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccHistory(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        int fOptions)
    {
        return SCC_E_OPNOTPERFORMED;
    }

    /// <summary>
    /// Shows provider-specific file properties. Not yet implemented.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccProperties", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe void SccProperties(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpFileName)
    {
        // No provider-specific properties UI to show.
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
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccEndBatch", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccEndBatch(
        IntPtr pContext,
        IntPtr hWnd,
        int nCommand)
    {
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