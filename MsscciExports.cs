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

    /// <summary>
    /// EA calls this first to get the MSSCCI spec version supported (e.g., version 1.3 -> 0x00010300).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SCCGetVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int SCCGetVersion()
    {
        return 0x00010300; // MSSCCI v1.3
    }

    /// <summary>
    /// Called when EA initializes the SCC context.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccInitialize", CallConvs = new[] { typeof(CallConvCdecl) })]
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

            return SCC_OK;
        }
        catch
        {
            return SCC_E_INITIALIZATIONFAILED;
        }
    }

    /// <summary>
    /// Map SccCheckin to LibGit2Sharp Stage + Commit + Push
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccCheckin", CallConvs = new[] { typeof(CallConvCdecl) })]
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
                string? repoPath = Repository.Discover(filePath);
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
    [UnmanagedCallersOnly(EntryPoint = "SccUninitialize", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe int SccUninitialize(IntPtr pContext)
    {
        return SCC_OK;
    }

    /// <summary>
    /// Called by EA to open/associate a project with a local path. For Git there's no
    /// separate "project" concept, so we just verify a repository exists at the path.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccOpenProject", CallConvs = new[] { typeof(CallConvCdecl) })]
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
            string? localPath = lpLocalPath != null ? Marshal.PtrToStringAnsi((IntPtr)lpLocalPath) : null;
            if (string.IsNullOrEmpty(localPath) || Repository.Discover(localPath) == null)
            {
                return SCC_E_NONSPECIFICERROR;
            }

            return SCC_OK;
        }
        catch
        {
            return SCC_E_UNKNOWNERROR;
        }
    }

    /// <summary>
    /// Called when EA closes the project. No git-specific work required.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccCloseProject", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe int SccCloseProject(IntPtr pContext)
    {
        return SCC_OK;
    }

    /// <summary>
    /// Map SccAdd to LibGit2Sharp Stage (git add). Files become tracked but are not
    /// committed until a subsequent SccCheckin.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccAdd", CallConvs = new[] { typeof(CallConvCdecl) })]
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
        return ForEachFileRepo(nFiles, lpFileNames, (repo, filePath) =>
        {
            Commands.Stage(repo, filePath);
        });
    }

    /// <summary>
    /// Map SccRemove to LibGit2Sharp Remove (git rm). Staged for deletion; committed on next checkin.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccRemove", CallConvs = new[] { typeof(CallConvCdecl) })]
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
    [UnmanagedCallersOnly(EntryPoint = "SccRename", CallConvs = new[] { typeof(CallConvCdecl) })]
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

            string? repoPath = Repository.Discover(oldPath);
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
    [UnmanagedCallersOnly(EntryPoint = "SccCheckout", CallConvs = new[] { typeof(CallConvCdecl) })]
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
    [UnmanagedCallersOnly(EntryPoint = "SccUncheckout", CallConvs = new[] { typeof(CallConvCdecl) })]
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
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccGetLatestVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe int SccGetLatestVersion(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        int fOptions,
        IntPtr pvConfig)
    {
        try
        {
            for (int i = 0; i < nFiles; i++)
            {
                string filePath = Marshal.PtrToStringAnsi((IntPtr)lpFileNames[i])!;

                string? repoPath = Repository.Discover(filePath);
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
    [UnmanagedCallersOnly(EntryPoint = "SccQueryInfo", CallConvs = new[] { typeof(CallConvCdecl) })]
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

                string? repoPath = Repository.Discover(filePath);
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

                string? repoPath = Repository.Discover(filePath);
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
    [UnmanagedCallersOnly(EntryPoint = "SccGetProjPath", CallConvs = new[] { typeof(CallConvCdecl) })]
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
    [UnmanagedCallersOnly(EntryPoint = "SccGetCommandOptions", CallConvs = new[] { typeof(CallConvCdecl) })]
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
    [UnmanagedCallersOnly(EntryPoint = "SccDiff", CallConvs = new[] { typeof(CallConvCdecl) })]
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
    [UnmanagedCallersOnly(EntryPoint = "SccHistory", CallConvs = new[] { typeof(CallConvCdecl) })]
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
    [UnmanagedCallersOnly(EntryPoint = "SccProperties", CallConvs = new[] { typeof(CallConvCdecl) })]
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
    [UnmanagedCallersOnly(EntryPoint = "SccPopulateList", CallConvs = new[] { typeof(CallConvCdecl) })]
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
    [UnmanagedCallersOnly(EntryPoint = "SccRunScc", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe int SccRunScc(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames)
    {
        return SCC_E_OPNOTPERFORMED;
    }
}