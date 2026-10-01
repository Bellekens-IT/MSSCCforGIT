using LibGit2Sharp;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MSSCCforGIT;

public static partial class MsscciExports
{
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
                LogDiagnostic("GetCore", new Exception($"filePath='{filePath}' repoPath='{repoPath}'"));
                if (repoPath == null) continue;

                using var repo = OpenRepositoryEnsuringSafeDirectory(repoPath);

                PullViaGitCli(repo.Info.WorkingDirectory, repo.Head.FriendlyName);
            }

            return SCC_OK;
        }
        catch (Exception ex)
        {
            LogDiagnostic("GetCore", ex);
            return SCC_E_UNKNOWNERROR;
        }
    }

    /// <summary>
    /// Reports source-control status for each file (controlled/checked out/different/etc.), used
    /// by EA to decide what status icon to show.
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
}
