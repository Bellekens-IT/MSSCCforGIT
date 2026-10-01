using LibGit2Sharp;

namespace MSSCCforGIT;

public static partial class MsscciExports
{
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
    /// Pulls (fetch + merge) the given branch from "origin" using the system "git" CLI rather than
    /// LibGit2Sharp's Commands.Pull. Explicitly names the remote and branch ("git pull origin
    /// &lt;branch&gt;") instead of relying on upstream tracking configuration (branch.&lt;name&gt;.remote /
    /// branch.&lt;name&gt;.merge), since - unlike "git push", which can infer the target via
    /// push.default=simple - a pull/merge needs an explicit source and would otherwise fail with
    /// "There is no tracking information for the current branch" on branches that were never
    /// explicitly configured for tracking (even though push to the same remote works fine). Also
    /// picks up the configured credential helper (e.g. Git Credential Manager) the same way
    /// PushViaGitCli does.
    /// </summary>
    private static void PullViaGitCli(string workingDirectory, string branchName)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            ArgumentList = { "pull", "origin", branchName },
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start 'git pull' process.");
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);

        LogDiagnostic("PullViaGitCli", new Exception($"exitCode={process.ExitCode} stdout='{stdout}' stderr='{stderr}'"));

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'git pull' failed with exit code {process.ExitCode}: {stderr}");
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
}
