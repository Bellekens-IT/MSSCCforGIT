using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MSSCCforGIT;

public static partial class MsscciExports
{
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
}
