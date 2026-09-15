using TarkovCompanion.RecognitionCorpus;
using Xunit;

namespace TarkovCompanion.RecognitionCorpusTests;

public sealed class CorpusPathTests
{
    [Fact]
    public void RepositoryRootAndItsFilesCannotBePrivateCorpusMaterial()
    {
        Assert.Throws<InvalidOperationException>(() => CorpusPaths.RejectRepositoryPath(CorpusFixtures.RepositoryRoot()));
        Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePrivateInput(CorpusFixtures.ThresholdsPath()));
    }

    [Fact]
    public void OrdinaryPrivateFilesAndLinksThatStayOutsideResolveToTheirCanonicalFile()
    {
        var root = CorpusFixtures.PrivateRoot();
        try
        {
            var directory = Directory.CreateDirectory(Path.Join(root.FullName, "private"));
            var manifest = Path.Join(directory.FullName, "manifest.json");
            File.WriteAllText(manifest, "{}");
            var canonical = CorpusPaths.ResolvePrivateInput(manifest);
            Assert.EndsWith(Path.Join("private", "manifest.json"), canonical, StringComparison.Ordinal);

            var alias = Path.Join(root.FullName, "alias");
            if (CorpusFixtures.TryCreateDirectoryLink(alias, directory.FullName))
            {
                Assert.Equal(canonical, CorpusPaths.ResolvePrivateInput(Path.Join(alias, "manifest.json")));
                Assert.Equal(canonical, CorpusPaths.ResolvePrivateInput(Path.Join(alias, "..", "private", "manifest.json")));
            }
        }
        finally
        {
            CorpusFixtures.DeletePrivateRoot(root);
        }
    }

    [Fact]
    public void IntermediateDirectoryLinkBackIntoTheWorktreeIsRejected()
    {
        var root = CorpusFixtures.PrivateRoot();
        try
        {
            var link = Path.Join(root.FullName, "linked-worktree");
            if (!CorpusFixtures.TryCreateDirectoryLink(link, CorpusFixtures.RepositoryRoot()))
            {
                return;
            }

            var linkedFixture = Path.Join(link, "fixtures", "recognition-corpus", "examples", "synthetic-run-plan.v1.json");
            Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePrivateInput(linkedFixture));
        }
        finally
        {
            CorpusFixtures.DeletePrivateRoot(root);
        }
    }

    /// <summary>
    /// The escape the previous resolver missed: the final link's target is itself reached through
    /// a second, parent-directory link. Asking for the final target resolved only the last hop.
    /// </summary>
    [Fact]
    public void TwoHopParentLinkIntoTheWorktreeIsRejected()
    {
        var root = CorpusFixtures.PrivateRoot();
        try
        {
            var hop2 = Path.Join(root.FullName, "hop2");
            var hop1 = Path.Join(root.FullName, "hop1.json");
            var target = Path.Join(hop2, "examples", "synthetic-run-plan.v1.json");
            if (!CorpusFixtures.TryCreateDirectoryLink(hop2, Path.Join(CorpusFixtures.RepositoryRoot(), "fixtures", "recognition-corpus")) ||
                !CorpusFixtures.TryCreateFileLink(hop1, target))
            {
                return;
            }

            Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePrivateInput(hop1));

            var relative = Path.Join(root.FullName, "relative.json");
            if (CorpusFixtures.TryCreateFileLink(relative, Path.Join("hop2", "examples", "synthetic-run-plan.v1.json")))
            {
                Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePrivateInput(relative));
            }

            // As an output parent the same chain would write private producer material into the checkout.
            Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePrivateOutput(Path.Join(hop2, "examples", "leak.json")));
            Assert.False(File.Exists(Path.Join(CorpusFixtures.RepositoryRoot(), "fixtures", "recognition-corpus", "examples", "leak.json")));
        }
        finally
        {
            CorpusFixtures.DeletePrivateRoot(root);
        }
    }

    /// <summary>
    /// ".." after a link means the link target's parent, not the lexical parent. Collapsing it
    /// before resolution checks one path and reads another.
    /// </summary>
    [Fact]
    public void ParentTraversalAfterALinkIsResolvedPhysically()
    {
        var root = CorpusFixtures.PrivateRoot();
        try
        {
            var outside = Directory.CreateDirectory(Path.Join(root.FullName, "outside"));
            var link = Path.Join(outside.FullName, "examples-link");
            if (!CorpusFixtures.TryCreateDirectoryLink(link, Path.Join(CorpusFixtures.RepositoryRoot(), "fixtures", "recognition-corpus", "examples")))
            {
                return;
            }

            // Lexically this names outside/examples/..., which does not exist; physically it is the
            // checked-in example inside the worktree.
            var physical = Path.Join(link, "..", "examples", "synthetic-run-plan.v1.json");
            Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePrivateInput(physical));

            var relativeTarget = Path.Join(root.FullName, "dot-dot.json");
            if (CorpusFixtures.TryCreateFileLink(relativeTarget, Path.Join("outside", "examples-link", "..", "examples", "synthetic-run-plan.v1.json")))
            {
                Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePrivateInput(relativeTarget));
            }
        }
        finally
        {
            CorpusFixtures.DeletePrivateRoot(root);
        }
    }

    [Fact]
    public void GitStorageIsDeniedForInputsAndEveryOutput()
    {
        var root = CorpusFixtures.PrivateRoot();
        try
        {
            var worktree = Directory.CreateDirectory(Path.Join(root.FullName, "nested"));
            var gitDirectory = Directory.CreateDirectory(Path.Join(worktree.FullName, ".git"));
            var insideGit = Path.Join(gitDirectory.FullName, "private.json");
            File.WriteAllText(insideGit, "{}");
            Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePrivateInput(insideGit));
            Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePrivateOutput(Path.Join(gitDirectory.FullName, "plan.json")));
            Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePublicationOutput(Path.Join(gitDirectory.FullName, "aggregate.json")));

            // A separated or bare Git directory has no ".git" child for an ancestor walk to find.
            var separated = Directory.CreateDirectory(Path.Join(root.FullName, "separated"));
            File.WriteAllText(Path.Join(separated.FullName, "HEAD"), "ref: refs/heads/main\n");
            Directory.CreateDirectory(Path.Join(separated.FullName, "objects"));
            Directory.CreateDirectory(Path.Join(separated.FullName, "refs"));
            var hooks = Directory.CreateDirectory(Path.Join(separated.FullName, "hooks"));
            Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePublicationOutput(Path.Join(hooks.FullName, "pre-commit")));
            var separatedInput = Path.Join(separated.FullName, "objects", "private.json");
            File.WriteAllText(separatedInput, "{}");
            Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePrivateInput(separatedInput));

            var gitLink = Path.Join(root.FullName, "innocent");
            if (CorpusFixtures.TryCreateDirectoryLink(gitLink, gitDirectory.FullName))
            {
                Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePublicationOutput(Path.Join(gitLink, "aggregate.json")));
            }
        }
        finally
        {
            CorpusFixtures.DeletePrivateRoot(root);
        }
    }

    [Fact]
    public void OutputsRefuseLinksDirectoriesAndRepositoryDestinationsButAcceptPrivateFiles()
    {
        var root = CorpusFixtures.PrivateRoot();
        try
        {
            var output = Path.Join(root.FullName, "run-plan.json");
            Assert.EndsWith("run-plan.json", CorpusPaths.ResolvePrivateOutput(output), StringComparison.Ordinal);
            Assert.EndsWith("aggregate.json", CorpusPaths.ResolvePublicationOutput(Path.Join(root.FullName, "aggregate.json")), StringComparison.Ordinal);

            Assert.Throws<InvalidOperationException>(() =>
                CorpusPaths.ResolvePrivateOutput(Path.Join(CorpusFixtures.RepositoryRoot(), "fixtures", "recognition-corpus", "run-plan.json")));
            Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePrivateOutput(root.FullName));
            Assert.Throws<DirectoryNotFoundException>(() => CorpusPaths.ResolvePrivateOutput(Path.Join(root.FullName, "missing", "plan.json")));
            Assert.Throws<ArgumentException>(() => CorpusPaths.ResolvePrivateOutput(Path.Join(root.FullName, "..")));

            File.WriteAllText(Path.Join(root.FullName, "real.json"), "{}");
            var linkedOutput = Path.Join(root.FullName, "linked.json");
            if (CorpusFixtures.TryCreateFileLink(linkedOutput, Path.Join(root.FullName, "real.json")))
            {
                Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePrivateOutput(linkedOutput));
                Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePublicationOutput(linkedOutput));
            }

            var dangling = Path.Join(root.FullName, "dangling.json");
            if (CorpusFixtures.TryCreateFileLink(dangling, Path.Join(root.FullName, "absent.json")))
            {
                Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePublicationOutput(dangling));
            }
        }
        finally
        {
            CorpusFixtures.DeletePrivateRoot(root);
        }
    }

    [Fact]
    public void LinkLoopsAndMissingComponentsFailClosed()
    {
        var root = CorpusFixtures.PrivateRoot();
        try
        {
            Assert.Throws<FileNotFoundException>(() => CorpusPaths.ResolvePrivateInput(Path.Join(root.FullName, "absent", "manifest.json")));

            var first = Path.Join(root.FullName, "loop-a");
            var second = Path.Join(root.FullName, "loop-b");
            if (CorpusFixtures.TryCreateFileLink(first, second) && CorpusFixtures.TryCreateFileLink(second, first))
            {
                Assert.Throws<InvalidOperationException>(() => CorpusPaths.ResolvePrivateInput(Path.Join(root.FullName, "loop-a")));
            }
        }
        finally
        {
            CorpusFixtures.DeletePrivateRoot(root);
        }
    }
}
