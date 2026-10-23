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
    private const string TestPackageGuid = "{0E0C7598-7B9F-42fc-BEBE-9D4AAEE28578}";

    private readonly EaTestFixture _fixture;

    public SccWorkflowTests(EaTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetTestRepo();
        _fixture.ResetEaProject();
        _fixture.ClearDiagnosticLog();
    }

    [Fact]
    public void Checkout_VersionControlledPackage_InvokesProviderAndMarksFileWritable()
    {
        var package = _fixture.StaThread.Invoke(() => FindPackageByGuid(_fixture.EaRepository, TestPackageGuid));
        Assert.True(_fixture.StaThread.Invoke(() => package.IsVersionControlled), "Fixture package must already be under version control.");

        _fixture.StaThread.Invoke(() => package.VersionControlCheckout("Integration test checkout"));
        try
        {
            var filePath = Path.Combine(_fixture.TestRepoPath, $"{package.Name}.xml");
            Assert.True(IOFile.Exists(filePath), $"Expected package file at '{filePath}'.");

            var log = _fixture.ReadDiagnosticLog();
            Assert.Contains("SccCheckout", log);
        }
        finally
        {
            // Release the lock EA placed on the package so later tests in this shared EA
            // instance aren't blocked by a leftover checkout from this test.
            _fixture.StaThread.Invoke(() => package.VersionControlCheckin("Release checkout lock from integration test"));
        }
    }

    [Fact]
    public void Checkin_ModifiedVersionControlledPackage_CommitsChangeToGit()
    {
        var package = _fixture.StaThread.Invoke(() => FindPackageByGuid(_fixture.EaRepository, TestPackageGuid));

        using var repoBefore = new LibGit2Sharp.Repository(_fixture.GitRepoRoot);
        var commitCountBefore = repoBefore.Commits.Count();

        _fixture.StaThread.Invoke(() =>
        {
            package.VersionControlCheckout("Integration test checkout");
            package.Notes = $"Integration test note {Guid.NewGuid()}";
            package.Update();
            package.VersionControlCheckin("Integration test checkin");
        });

        using var repoAfter = new LibGit2Sharp.Repository(_fixture.GitRepoRoot);
        var commitCountAfter = repoAfter.Commits.Count();

        Assert.True(commitCountAfter > commitCountBefore, "Expected a new commit to be created by SccCheckin.");
        Assert.Equal("Integration test checkin", repoAfter.Commits.First().MessageShort);

        var log = _fixture.ReadDiagnosticLog();
        Assert.Contains("SccCheckin", log);
    }

    [Fact]
    public void GetLatest_AfterExternalCommit_UpdatesWorkingCopyFromGit()
    {
        var package = _fixture.StaThread.Invoke(() => FindPackageByGuid(_fixture.EaRepository, TestPackageGuid));
        var filePath = Path.Combine(_fixture.TestRepoPath, $"{package.Name}.xml");

        var originalContent = IOFile.ReadAllText(filePath);
        var marker = $"GetLatestMarker_{Guid.NewGuid():N}";
        Assert.DoesNotContain(marker, originalContent);

        // Append the marker as an XML comment so the change is well-formed regardless of the
        // package's actual content, instead of relying on specific text (e.g. "Proposed") that
        // may not exist in every fixture package.
        var externalContent = originalContent + $"<!-- {marker} -->";
        CommitExternalChange(filePath, externalContent, "External commit for GetLatest test");

        // Restore the working copy to its pre-external-change state so VersionControlGetLatest
        // actually has something to pull down, rather than the file already matching HEAD.
        IOFile.WriteAllText(filePath, originalContent);

        _fixture.StaThread.Invoke(() => package.VersionControlGetLatest(false));

        var localContentAfterGet = IOFile.ReadAllText(filePath);
        Assert.Contains(marker, localContentAfterGet);

        var log = _fixture.ReadDiagnosticLog();
        Assert.Contains("GetCore", log);
    }

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

        // EA enforces the "parent package is locked by Version Control" rule for any structural
        // change (including adding a new child) under a package that is already under version
        // control, so the parent must be checked out first.
        var parent = _fixture.StaThread.Invoke(() => FindPackageByGuid(_fixture.EaRepository, TestPackageGuid));
        _fixture.StaThread.Invoke(() => parent.VersionControlCheckout("Integration test checkout for Add"));
        try
        {
            _fixture.StaThread.Invoke(() =>
            {
                Assert.True(parent.VersionControlGetStatus() >= 0, "Expected parent package status to be queryable before adding a child.");

                var newPackage = (Package)parent.Packages.AddNew(newPackageName, "Package");
                newPackage.Update();
                parent.Packages.Refresh();

                newPackage.VersionControlAdd("Bellekens GIT", $"{newPackageName}.xml", "Integration test add", false);
            });
        }
        finally
        {
            // Release the parent checkout lock so later tests sharing this fixture's EA instance
            // aren't blocked by a leftover checkout from this test.
            _fixture.StaThread.Invoke(() => parent.VersionControlCheckin("Release checkout lock from integration test"));
        }

        using var repoAfter = new LibGit2Sharp.Repository(_fixture.GitRepoRoot);
        var testSubtree = (LibGit2Sharp.Tree)repoAfter.Commits.First()["Test"].Target;
        var trackedFiles = testSubtree.Select(e => e.Name).ToList();
        Assert.Contains(trackedFiles, name => name.Contains(newPackageName, StringComparison.OrdinalIgnoreCase));

        var log = _fixture.ReadDiagnosticLog();
        Assert.Contains("SccAdd", log);
    }

    [Fact]
    public void GetStatus_ControlledPackage_ReturnsControlledStatus()
    {
        var package = _fixture.StaThread.Invoke(() => FindPackageByGuid(_fixture.EaRepository, TestPackageGuid));

        var status = _fixture.StaThread.Invoke(() => package.VersionControlGetStatus());

        // EA's VCS status constants: 0 = not controlled; any other value (e.g. controlled/
        // checked-in) indicates the provider recognised and returned a status for the package.
        // Note: VersionControlGetStatus() reads EA's own cached package status rather than making
        // a live round-trip through the provider's SccQueryInfo export, so there's no provider log
        // entry to assert on here (unlike the other VersionControl* tests, which do invoke
        // provider exports directly).
        Assert.True(status != 0, $"Expected a non-zero (controlled) VersionControlGetStatus() for the test package, got {status}.");
    }

    [Fact]
    public void Remove_VersionControlledPackage_InvokesProvider()
    {
        var newPackageName = $"IntegrationTestPackage_{Guid.NewGuid():N}";
        var parent = _fixture.StaThread.Invoke(() => FindPackageByGuid(_fixture.EaRepository, TestPackageGuid));

        // Both adding and removing a child package under a controlled parent are structural
        // changes, so the parent must be checked out first or EA throws "Cannot create new
        // Package. The parent package is locked by Version Control."
        _fixture.StaThread.Invoke(() => parent.VersionControlCheckout("Integration test checkout for Remove"));
        var newPackage = _fixture.StaThread.Invoke(() =>
        {
            var child = (Package)parent.Packages.AddNew(newPackageName, "Package");
            child.Update();
            parent.Packages.Refresh();
            child.VersionControlAdd("Bellekens GIT", $"{newPackageName}.xml", "Integration test add before remove", false);
            return child;
        });

        var filePath = Path.Combine(_fixture.TestRepoPath, $"{newPackageName}.xml");
        Assert.True(IOFile.Exists(filePath), $"Expected package file at '{filePath}' after VersionControlAdd.");

        try
        {
            _fixture.ClearDiagnosticLog();

            _fixture.StaThread.Invoke(() => newPackage.VersionControlRemove());

            // SccRemove's provider implementation funnels through the shared ForEachFileRepo
            // helper (unlike e.g. SccAdd, which logs its own "SccAdd.Entry" first), so that's what
            // shows up in the diagnostic log here.
            var log = _fixture.ReadDiagnosticLog();
            Assert.Contains("ForEachFileRepo", log);
            Assert.False(IOFile.Exists(filePath), $"Expected package file at '{filePath}' to be removed after VersionControlRemove.");
        }
        finally
        {
            // Release the parent checkout lock so later tests sharing this fixture's EA instance
            // aren't blocked by a leftover checkout from this test.
            _fixture.StaThread.Invoke(() => parent.VersionControlCheckin("Release checkout lock from integration test"));
        }
    }

    [Fact]
    public void PutLatest_ModifiedCheckedOutPackage_InvokesProvider()
    {
        var package = _fixture.StaThread.Invoke(() => FindPackageByGuid(_fixture.EaRepository, TestPackageGuid));

        _fixture.StaThread.Invoke(() =>
        {
            package.VersionControlCheckout("Integration test checkout for PutLatest");
            package.Notes = $"Integration test note {Guid.NewGuid()}";
            package.Update();
        });

        _fixture.ClearDiagnosticLog();
        try
        {
            _fixture.StaThread.Invoke(() => package.VersionControlPutLatest("Integration test put latest"));

            var log = _fixture.ReadDiagnosticLog();
            Assert.True(log.Contains("SccCheckin") || log.Contains("SccAdd"),
                $"Expected VersionControlPutLatest to invoke the provider's checkin/add exports. Log:\n{log}");
        }
        finally
        {
            // VersionControlPutLatest is expected to release the checkout as part of committing,
            // but release it again defensively so a failed/partial PutLatest doesn't leave the
            // package locked for later tests sharing this fixture's EA instance.
            _fixture.StaThread.Invoke(() => package.VersionControlCheckin("Release checkout lock from integration test"));
        }
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

    /// <summary>
    /// Looks up a package directly by its fixed GUID via EA's Repository.GetPackageByGuid,
    /// instead of walking the package tree by name (which breaks whenever the fixture project's
    /// package names change).
    /// </summary>
    private static Package FindPackageByGuid(EA.Repository eaRepository, string guid)
    {
        var package = (Package)eaRepository.GetPackageByGuid(guid);
        if (package is null)
        {
            throw new InvalidOperationException($"Package '{guid}' was not found in the test project.");
        }

        return package;
    }
}
