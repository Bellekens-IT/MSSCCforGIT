using System.Linq;
using System.Runtime.InteropServices;

namespace MSSCCforGIT;

public static partial class MsscciExports
{
    /// <summary>
    /// Caches the repository root path established by SccOpenProject, keyed by the pContext EA
    /// passes back into every subsequent Scc* call. This is needed because some EA calls (e.g.
    /// SccAdd) only supply a bare relative file/package name rather than a full absolute path, so
    /// we must resolve it against the project root that was opened earlier.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, string> s_projectRootsByContext = new();

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
}
