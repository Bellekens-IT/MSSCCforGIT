using LibGit2Sharp;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MSSCCforGIT;

public static partial class MsscciExports
{
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
                HistoryForm.ShowHistory(hWnd, filePath, relativePath, entries);
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
                PropertiesForm.ShowProperties(hWnd, filePath, filePath, "(not in a Git repository)", "Not controlled", null);
                return SCC_OK;
            }

            using var repo = OpenRepositoryEnsuringSafeDirectory(repoPath);
            string relativePath = GetRelativePath(repo, filePath);
            FileStatus fileStatus = repo.RetrieveStatus(relativePath);

            List<HistoryEntry> lastCommitEntries = GetFileHistoryViaGitCli(repo.Info.WorkingDirectory, relativePath);
            HistoryEntry? lastCommit = lastCommitEntries.Count > 0 ? lastCommitEntries[0] : null;

            PropertiesForm.ShowProperties(hWnd, filePath, relativePath, repoPath, fileStatus.ToString(), lastCommit);

            return SCC_OK;
        }
        catch (Exception ex)
        {
            LogDiagnostic("SccProperties", ex);
            return SCC_E_UNKNOWNERROR;
        }
    }

    /// <summary>
    /// Invokes the provider's own UI for a raw command. EA calls this for "Start version control
    /// explorer" (among other raw-command menu items), so rather than always failing with
    /// SCC_E_OPNOTPERFORMED (which EA showed to the user as "Unknown SCC Error -30"), we treat it
    /// as a request to browse the repository and simply open the repository root in Windows
    /// Explorer - Git doesn't have a bundled repository browser UI of its own to launch here.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccRunScc", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccRunScc(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames)
    {
        try
        {
            string? repoPath = null;

            if (nFiles > 0 && lpFileNames != null)
            {
                string filePath = ResolveEaPath(ReadPlainAnsiString((IntPtr)lpFileNames[0]), pContext);
                repoPath = DiscoverRepositoryPath(filePath);
            }

            repoPath ??= s_lastOpenedProjectRoot;

            LogDiagnostic("SccRunScc", new Exception($"nFiles={nFiles} repoPath='{repoPath}'"));

            if (repoPath == null)
            {
                return SCC_E_FILENOTCONTROLLED;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{repoPath}\"",
                UseShellExecute = true,
            });

            return SCC_OK;
        }
        catch (Exception ex)
        {
            LogDiagnostic("SccRunScc", ex);
            return SCC_E_UNKNOWNERROR;
        }
    }
}
