using LibGit2Sharp;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
    /// Runs automatically when this assembly is loaded (before any exported Scc* function can be
    /// invoked), ensuring our own directory is added to the native DLL search path so LibGit2Sharp's
    /// git2-*.dll (which sits next to us, not next to the host EA.exe) can be found.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        EnsureNativeDllDirectory();
    }

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
            string comment = lpComment != null ? Marshal.PtrToStringAnsi((IntPtr)lpComment) ?? "EA Checkin" : "EA Checkin";

            for (int i = 0; i < nFiles; i++)
            {
                string filePath = Marshal.PtrToStringAnsi((IntPtr)lpFileNames[i])!;

                // Find local git repo root from the package path
                string? repoPath = DiscoverRepositoryPath(filePath);
                if (repoPath == null) continue;

                using var repo = new Repository(repoPath);

                // 1. Stage file
                Commands.Stage(repo, filePath);

                // 2. Commit
                Signature author = repo.Config.BuildSignature(DateTimeOffset.Now);
                repo.Commit(comment, author, author);

                // 3. Push to default remote
                Remote remote = repo.Network.Remotes["origin"];
                var options = new PushOptions();

                // Note: Authentication must be handled gracefully or retrieved from Git Credential Manager
                repo.Network.Push(remote, @"refs/heads/main", options);
            }

            return SCC_OK;
        }
        catch
        {
            return SCC_E_UNKNOWNERROR;
        }
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
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccAdd", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccAdd(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        sbyte* lpComment,
        ushort* pFlags,
        sbyte* lpCheckinComment,
        int fOptions)
    {
        return AddCore(nFiles, lpFileNames);
    }

    private static unsafe int AddCore(int nFiles, sbyte** lpFileNames)
    {
        return ForEachFileRepo(nFiles, lpFileNames, (repo, filePath) =>
        {
            Commands.Stage(repo, filePath);
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
        return ForEachFileRepo(nFiles, lpFileNames, (repo, filePath) =>
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
            string oldPath = Marshal.PtrToStringAnsi((IntPtr)lpFileName)!;
            string newPath = Marshal.PtrToStringAnsi((IntPtr)lpNewName)!;

            string? repoPath = DiscoverRepositoryPath(oldPath);
            if (repoPath == null) return SCC_E_FILENOTCONTROLLED;

            using var repo = new Repository(repoPath);

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
        return ForEachFileRepo(nFiles, lpFileNames, (repo, filePath) =>
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
        return ForEachFileRepo(nFiles, lpFileNames, (repo, filePath) =>
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
        return GetCore(nFiles, lpFileNames);
    }

    private static unsafe int GetCore(int nFiles, sbyte** lpFileNames)
    {
        try
        {
            for (int i = 0; i < nFiles; i++)
            {
                string filePath = Marshal.PtrToStringAnsi((IntPtr)lpFileNames[i])!;

                string? repoPath = DiscoverRepositoryPath(filePath);
                if (repoPath == null) continue;

                using var repo = new Repository(repoPath);

                Signature signature = repo.Config.BuildSignature(DateTimeOffset.Now);
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
                string filePath = Marshal.PtrToStringAnsi((IntPtr)lpFileNames[i])!;

                string? repoPath = DiscoverRepositoryPath(filePath);
                if (repoPath == null)
                {
                    if (pStatus != null) pStatus[i] = SCC_STATUS_NOTCONTROLLED;
                    continue;
                }

                using var repo = new Repository(repoPath);
                string relativePath = GetRelativePath(repo, filePath);

                FileStatus fileStatus = repo.RetrieveStatus(relativePath);
                int flags = SCC_STATUS_CONTROLLED;

                if (fileStatus.HasFlag(FileStatus.NewInWorkdir) || fileStatus.HasFlag(FileStatus.Nonexistent))
                {
                    flags = SCC_STATUS_NOTCONTROLLED;
                }
                else if (fileStatus.HasFlag(FileStatus.ModifiedInWorkdir) ||
                         fileStatus.HasFlag(FileStatus.NewInIndex) ||
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
    /// Shared helper: resolves the git repository for each file path and invokes an action against it.
    /// </summary>
    private static unsafe int ForEachFileRepo(int nFiles, sbyte** lpFileNames, Action<Repository, string> action)
    {
        try
        {
            for (int i = 0; i < nFiles; i++)
            {
                string filePath = Marshal.PtrToStringAnsi((IntPtr)lpFileNames[i])!;

                string? repoPath = DiscoverRepositoryPath(filePath);
                if (repoPath == null) continue;

                using var repo = new Repository(repoPath);
                action(repo, filePath);
            }

            return SCC_OK;
        }
        catch
        {
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
        ushort* pFlags,
        int fOptions)
    {
        return AddCore(nFiles, lpFileNames);
    }

    [UnmanagedCallersOnly(EntryPoint = "SccAddFilesFromSCC", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccAddFilesFromSCC(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        sbyte* lpComment,
        ushort* pFlags,
        int fOptions)
    {
        return AddCore(nFiles, lpFileNames);
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
        return GetCore(nFiles, lpFileNames);
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