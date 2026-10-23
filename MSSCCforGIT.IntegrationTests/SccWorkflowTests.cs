using EA;
using LibGit2Sharp;
using IOFile = System.IO.File;

namespace MSSCCforGIT.IntegrationTests;

/// <summary>
/// End-to-end tests that drive real EA (via its COM Automation API) against the fixture project
/// at Test\TestProject.qea, exercising the Git-backed MSSCCI provider through EA's
/// VersionControl* Package automation methods (which call straight into the provider's
/// SccAdd/SccCheckin/SccCheckout/SccGet/SccRemove exports, the same way EA's own UI does).
///
/// Each test resets the fixture Test repo to its original state first, so tests can run
/// independently and be re-run repeatedly without accumulating leftover commits/files.
/// </summary>
[Collection(EaTestCollection.Name)]
public sealed class SccWorkflowTests
{
    /// <summary>
    /// GUID of the package in Test\TestProject.qea that is already under version control, used
    /// as a fixed starting point instead of looking packages up by name.
    /// </summary>
    private const string TestCheckoutPackageGuid = "{0E0C7598-7B9F-42fc-BEBE-9D4AAEE28578}";
    private const string TestAddPackageGuid = "{9003124F-0084-4eb0-AFF3-3AC6F1ABF31B}";
    private const string TestCheckinPackageGuid = "{EF6AA3B5-99DB-4062-8BD7-2194EDE9C31C}";
    private const string TestGetStatusPackageGuid = "{22632E2C-B117-4a3f-8918-B7DDD5132C84}";

    private readonly EaTestFixture _fixture;

    public SccWorkflowTests(EaTestFixture fixture)
    {
        _fixture = fixture;
        // EA re-touches the controlled package's XMI (timestamps/export markers) merely by
        // opening the project, which can re-dirty Test/TestPackage.xml in git. Reset the EA
        // project first, then reset the git working tree, so each test starts with a
        // guaranteed-clean git state regardless of what EA did on open.
        _fixture.ResetEaProject();
        _fixture.ResetTestRepo();
        _fixture.ClearDiagnosticLog();
    }

    [Fact]
    public void Checkout_VersionControlledPackage_InvokesProviderAndMarksFileWritable()
    {
        var package = _fixture.StaThread.Invoke(() => _fixture.EaRepository.GetPackageByGuid(TestCheckoutPackageGuid));
        Assert.True(_fixture.StaThread.Invoke(() => package.IsVersionControlled), "Fixture package must already be under version control.");

        _fixture.StaThread.Invoke(() => package.VersionControlCheckout("Integration test checkout"));

        var packageFlags = _fixture.StaThread.Invoke(() =>
            _fixture.EaRepository.SQLQuery($"SELECT PackageFlags FROM t_package WHERE ea_guid = '{TestCheckoutPackageGuid}'"));
        Assert.Contains("CheckedOutTo", packageFlags);

        // If the package isn't actually checked out according to EA, saving a change to it will
        // throw/fail, since EA enforces that a controlled package must be checked out before it
        // can be modified.
        _fixture.StaThread.Invoke(() =>
        {
            package.Notes = $"Integration test note {Guid.NewGuid()}";
            package.Update();
        });
    }


    [Fact]
    public void Checkin_ModifiedVersionControlledPackage_CommitsChangeToGit()
    {
        using var repoBefore = new LibGit2Sharp.Repository(_fixture.GitRepoRoot);
        var commitCountBefore = repoBefore.Commits.Count();

        _fixture.StaThread.Invoke(() =>
        {
            // The fixture package is already checked out (see Test/TestProject.qea), matching
            // the common real-world scenario of checking in an already-checked-out package, so
            // there's no need (and no ability) to check it out again here first.
            var package = _fixture.EaRepository.GetPackageByGuid(TestCheckinPackageGuid);
            package.Notes = $"Integration test note {Guid.NewGuid()}";
            package.Update();
            package.VersionControlCheckin("Integration test checkin");
        });

        // SccCheckin debounces the actual commit+push for a few seconds (see
        // MsscciExports.CheckinBatching.cs) so it can combine rapid-fire multi-file checkins into
        // a single commit, so the real commit may not exist yet the instant VersionControlCheckin
        // returns - poll until it shows up (or we give up well past the debounce window).
        int commitCountAfter;
        using (var pollingRepo = new LibGit2Sharp.Repository(_fixture.GitRepoRoot))
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            do
            {
                commitCountAfter = pollingRepo.Commits.Count();
                if (commitCountAfter > commitCountBefore) break;
                Thread.Sleep(250);
            } while (DateTime.UtcNow < deadline);
        }

        using var repoAfter = new LibGit2Sharp.Repository(_fixture.GitRepoRoot);
        commitCountAfter = repoAfter.Commits.Count();

        Assert.True(commitCountAfter > commitCountBefore, "Expected a new commit to be created by SccCheckin.");
        // EA prepends its own "Check-in: <timestamp> " prefix to whatever comment is passed to
        // VersionControlCheckin before handing it to the provider, so the commit message contains
        // rather than exactly equals the comment passed by the test.
        Assert.Contains("Integration test checkin", repoAfter.Commits.First().MessageShort);
    }

    //[Fact]
    //public void GetLatest_AfterExternalCommit_UpdatesWorkingCopyFromGit()
    //{
    //    var package = _fixture.StaThread.Invoke(() => _fixture.EaRepository.GetPackageByGuid(TestCheckoutPackageGuid));
    //    var filePath = Path.Combine(_fixture.TestRepoPath, $"{package.Name}.xml");

    //    var originalContent = IOFile.ReadAllText(filePath);
    //    var marker = $"GetLatestMarker_{Guid.NewGuid():N}";
    //    Assert.DoesNotContain(marker, originalContent);

    //    // Append the marker as an XML comment so the change is well-formed regardless of the
    //    // package's actual content, instead of relying on specific text (e.g. "Proposed") that
    //    // may not exist in every fixture package.
    //    var externalContent = originalContent + $"<!-- {marker} -->";
    //    CommitExternalChange(filePath, externalContent, "External commit for GetLatest test");

    //    // Restore the working copy to its pre-external-change state so VersionControlGetLatest
    //    // actually has something to pull down, rather than the file already matching HEAD.
    //    IOFile.WriteAllText(filePath, originalContent);

    //    _fixture.StaThread.Invoke(() => package.VersionControlGetLatest(false));

    //    var localContentAfterGet = IOFile.ReadAllText(filePath);
    //    Assert.Contains(marker, localContentAfterGet);

    //    var log = _fixture.ReadDiagnosticLog();
    //    Assert.Contains("GetCore", log);
    //}

    [Fact]
    public void Add_NewPackage_StagesCommitsAndTracksInGit()
    {
        using (var repo = new LibGit2Sharp.Repository(_fixture.GitRepoRoot))
        {
            // Pre-existing untracked files at this point are expected to be just EA's own
            // disposable temp project copy and its SQLite sidecar files (named
            // "TestProject.<guid>.qea[-journal/-wal/-shm]"), never anything related to the
            // package this test is about to add.
            var options = new StatusOptions { IncludeUntracked = true, PathSpec = new[] { "Test" } };
            Assert.DoesNotContain(repo.RetrieveStatus(options), e =>
                e.State == FileStatus.NewInWorkdir && !Path.GetFileName(e.FilePath).StartsWith("TestProject.", StringComparison.OrdinalIgnoreCase));
        }

        var newPackageName = $"IntegrationTestPackage_{Guid.NewGuid():N}";
     
        using var repoBefore = new LibGit2Sharp.Repository(_fixture.GitRepoRoot);
        var commitCountBefore = repoBefore.Commits.Count();

        
        _fixture.StaThread.Invoke(() =>
        {
            var parent = _fixture.EaRepository.GetPackageByGuid(TestAddPackageGuid);
            var newPackage = parent.Packages.AddNew(newPackageName, "");
            newPackage.Update();
            newPackage.VersionControlAdd("Bellekens IT", $"{newPackageName}.xml", "Integration test add", false);
        });

        // VersionControlAdd funnels through SccAdd, which commits+pushes immediately (unlike
        // SccCheckin, SccAdd does not debounce), but poll briefly anyway to avoid any race with
        // the push completing.
        int commitCountAfter;
        using (var pollingRepo = new LibGit2Sharp.Repository(_fixture.GitRepoRoot))
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            do
            {
                commitCountAfter = pollingRepo.Commits.Count();
                if (commitCountAfter > commitCountBefore) break;
                Thread.Sleep(250);
            } while (DateTime.UtcNow < deadline);
        }

        using var repoAfter = new LibGit2Sharp.Repository(_fixture.GitRepoRoot);
        var testSubtree = (LibGit2Sharp.Tree)repoAfter.Commits.First()["Test"].Target;
        var trackedFiles = testSubtree.Select(e => e.Name).ToList();
        Assert.Contains(trackedFiles, name => name.Contains(newPackageName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetStatus_ControlledPackage_ReturnsControlledStatus()
    {
        var status = _fixture.StaThread.Invoke(() =>
        {
            var package = _fixture.EaRepository.GetPackageByGuid(TestGetStatusPackageGuid);
            return package.VersionControlGetStatus();
        });

        // EA's VCS status constants: 0 = not controlled; any other value (e.g. controlled/
        // checked-in) indicates the provider recognised and returned a status for the package.
        // Note: VersionControlGetStatus() reads EA's own cached package status rather than making
        // a live round-trip through the provider's SccQueryInfo export, so there's no provider log
        // entry to assert on here (unlike the other VersionControl* tests, which do invoke
        // provider exports directly).
        Assert.True(status != 0, $"Expected a non-zero (controlled) VersionControlGetStatus() for the test package, got {status}.");
    }




    /// <summary>
    /// Writes new content directly to a tracked file and commits it from outside EA, simulating
    /// a teammate's push, so that a subsequent VersionControlGetLatest has something to pull.
    /// </summary>
    private static void CommitExternalChange(string filePath, string newContent, string message)
    {
        IOFile.WriteAllText(filePath, newContent);

        var repoPath = LibGit2Sharp.Repository.Discover(filePath);
        using var repo = new LibGit2Sharp.Repository(repoPath);
        Commands.Stage(repo, filePath);
        var signature = new Signature("Integration Test", "integration-test@example.com", DateTimeOffset.Now);
        repo.Commit(message, signature, signature);
    }

}
