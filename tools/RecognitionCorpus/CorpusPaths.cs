namespace TarkovCompanion.RecognitionCorpus;

/// <summary>
/// Every private corpus path is canonicalized one component at a time before anything is read
/// or written. The earlier resolver collapsed ".." lexically with Path.GetFullPath and then asked
/// the runtime for a link's final target, which resolves a chain of links on the last component
/// but never the parents inside that target: an outside link pointing at "outside/hop/file",
/// where "hop" is itself a directory link into a worktree, passed every ancestor check and read
/// checked-in fixtures as private evidence. Here a link's raw target is spliced back into the
/// pending components, so parents of every target are resolved too, and ".." only ever removes
/// a component that is already known not to be a link.
/// </summary>
public static class CorpusPaths
{
    private const int MaximumLinkTraversals = 40;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>Resolves an existing private input and refuses it inside any repository, worktree, or Git directory.</summary>
    public static string ResolvePrivateInput(string path)
    {
        RejectRepositoryAncestor(Path.GetFullPath(RequirePath(path)));
        var canonical = Canonicalize(path);
        RejectRepositoryAncestor(canonical);
        RequireRegularFile(canonical, "Private recognition corpus input");
        return canonical;
    }

    /// <summary>Resolves an existing public input, such as frozen thresholds or an aggregate, to its canonical file.</summary>
    public static string ResolveExistingInput(string path)
    {
        var canonical = Canonicalize(path);
        RequireRegularFile(canonical, "Recognition corpus input");
        return canonical;
    }

    /// <summary>
    /// Resolves an output for private, truth-free producer material such as a run plan. That
    /// material names samples and context, so it may never land in a repository or worktree.
    /// </summary>
    public static string ResolvePrivateOutput(string path)
    {
        var (lexicalParent, canonicalParent, leaf) = OutputParts(path);
        RejectRepositoryAncestor(lexicalParent);
        RejectRepositoryAncestor(canonicalParent);
        return RequireReplaceableOutput(Path.Join(canonicalParent, leaf));
    }

    /// <summary>
    /// Resolves an output for an already validated privacy-safe aggregate. It may be written into
    /// a checkout for review, but never into Git's own storage and never through a link.
    /// </summary>
    public static string ResolvePublicationOutput(string path)
    {
        var (lexicalParent, canonicalParent, leaf) = OutputParts(path);
        RejectGitStorage(lexicalParent);
        RejectGitStorage(canonicalParent);
        return RequireReplaceableOutput(Path.Join(canonicalParent, leaf));
    }

    public static void RejectRepositoryPath(string path)
    {
        var lexical = Path.GetFullPath(RequirePath(path));
        RejectRepositoryAncestor(lexical);
        if (EntryExists(lexical))
        {
            RejectRepositoryAncestor(Canonicalize(path));
        }
    }

    public static bool SamePath(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Returns the link-free absolute path of an existing entry. Every component, including the
    /// parents of each link target and the final entry, is resolved; a missing component, a link
    /// loop, or a reparse point that is not a symlink or junction fails closed.
    /// </summary>
    public static string Canonicalize(string path)
    {
        var absolute = Absolute(RequirePath(path));
        var current = Path.GetPathRoot(absolute) ?? throw new InvalidOperationException("A recognition corpus path has no filesystem root.");
        var pending = new Stack<string>();
        Push(pending, absolute[current.Length..]);
        var traversals = 0;
        while (pending.Count > 0)
        {
            var component = pending.Pop();
            if (component is "" or ".")
            {
                continue;
            }

            if (component == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var candidate = Path.Join(current, component);
            var attributes = Attributes(candidate) ??
                             throw new FileNotFoundException("A recognition corpus path component does not exist.");
            var target = LinkTarget(candidate, attributes);
            if (target is null)
            {
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidOperationException("A recognition corpus path crosses a reparse point that is not a symlink or junction.");
                }

                current = candidate;
                continue;
            }

            if (++traversals > MaximumLinkTraversals)
            {
                throw new InvalidOperationException("A recognition corpus path traverses too many links or a link loop.");
            }

            if (Path.IsPathFullyQualified(target))
            {
                current = Path.GetPathRoot(target)!;
                Push(pending, target[current.Length..]);
            }
            else if (Path.IsPathRooted(target) && Array.IndexOf(Separators, target[0]) >= 0)
            {
                current = Path.GetPathRoot(current)!;
                Push(pending, target);
            }
            else if (Path.IsPathRooted(target))
            {
                throw new InvalidOperationException("A recognition corpus link has an ambiguous drive-relative target.");
            }
            else
            {
                // A relative target is relative to the directory holding the link, which is the
                // canonical directory already in hand.
                Push(pending, target);
            }
        }

        return current;
    }

    private static string RequirePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A recognition corpus path cannot contain a NUL character.", nameof(path));
        }

        return path;
    }

    private static string Absolute(string path)
    {
        if (Path.IsPathFullyQualified(path))
        {
            return path;
        }

        if (Path.IsPathRooted(path))
        {
            throw new ArgumentException("A recognition corpus path must be fully qualified or relative to the current directory.", nameof(path));
        }

        return Path.Join(Directory.GetCurrentDirectory(), path);
    }

    private static void Push(Stack<string> pending, string relative)
    {
        var components = relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (var index = components.Length - 1; index >= 0; index--)
        {
            pending.Push(components[index]);
        }
    }

    private static FileAttributes? Attributes(string path)
    {
        try
        {
            // GetAttributes does not follow a final link: a symlink or junction reports
            // ReparsePoint, and a dangling link still exists as a link.
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string? LinkTarget(string path, FileAttributes attributes)
    {
        // Windows marks every symlink and junction as a reparse point, and opening each ordinary
        // component to ask would need access the caller may not have. Unix readlink is cheap and
        // does not depend on the runtime having mapped a symlink to ReparsePoint.
        if (OperatingSystem.IsWindows() && !attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }

        FileSystemInfo entry = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(path) : new FileInfo(path);
        return string.IsNullOrEmpty(entry.LinkTarget) ? null : entry.LinkTarget;
    }

    private static bool EntryExists(string path) => Attributes(path) is not null;

    private static void RequireRegularFile(string canonical, string description)
    {
        var attributes = Attributes(canonical);
        if (attributes is null || attributes.Value.HasFlag(FileAttributes.Directory))
        {
            throw new FileNotFoundException($"{description} is not an existing regular file.");
        }
    }

    private static (string LexicalParent, string CanonicalParent, string Leaf) OutputParts(string path)
    {
        var absolute = Absolute(RequirePath(path));
        var root = Path.GetPathRoot(absolute) ?? throw new InvalidOperationException("A recognition corpus output has no filesystem root.");
        var separator = absolute.LastIndexOfAny(Separators);
        var leaf = separator < 0 ? absolute : absolute[(separator + 1)..];
        if (leaf is "" or "." or ".." || separator < root.Length - 1)
        {
            throw new ArgumentException("A recognition corpus output must name a file inside an existing directory.", nameof(path));
        }

        var parent = separator < root.Length ? root : absolute[..separator];
        if (Attributes(parent) is not { } parentAttributes || !parentAttributes.HasFlag(FileAttributes.Directory))
        {
            throw new DirectoryNotFoundException("A recognition corpus output directory does not exist.");
        }

        return (Path.GetFullPath(parent), Canonicalize(parent), leaf);
    }

    private static string RequireReplaceableOutput(string output)
    {
        // A replaced link would be harmless to an atomic rename, but a link where an output is
        // expected is a sign that somebody is steering the write; refuse rather than guess.
        if (Attributes(output) is { } attributes &&
            (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) ||
             LinkTarget(output, attributes) is not null))
        {
            throw new InvalidOperationException("A recognition corpus output cannot replace a directory, symlink, or reparse point.");
        }

        return output;
    }

    /// <summary>
    /// A repository is recognized by a ".git" entry in the path or any ancestor, and a separated
    /// or bare Git directory by its own HEAD, objects, and refs layout, since such a directory
    /// has no ".git" child for the ancestor walk to find.
    /// </summary>
    private static void RejectRepositoryAncestor(string path)
    {
        RejectGitStorage(path);
        for (var directory = ContainingDirectory(path); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (EntryExists(Path.Join(directory, ".git")))
            {
                throw new InvalidOperationException("Recognition corpus roots and private manifests must be outside every repository or worktree.");
            }
        }
    }

    private static void RejectGitStorage(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        foreach (var component in path[root.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            // Win32 drops trailing dots and spaces, so ".git." and ".git " open ".git".
            var name = OperatingSystem.IsWindows() ? component.TrimEnd('.', ' ') : component;
            if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Recognition corpus material cannot be read from or written into Git storage.");
            }
        }

        for (var directory = ContainingDirectory(path); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Join(directory, "HEAD")) && Directory.Exists(Path.Join(directory, "objects")) &&
                Directory.Exists(Path.Join(directory, "refs")))
            {
                throw new InvalidOperationException("Recognition corpus material cannot be read from or written into Git storage.");
            }
        }
    }

    /// <summary>
    /// The walk starts at a directory: probing "file/.git" is merely absent on Unix but can be an
    /// I/O error rather than not-found on Windows.
    /// </summary>
    private static string? ContainingDirectory(string path) =>
        Attributes(path) is { } attributes && attributes.HasFlag(FileAttributes.Directory) ? path : Path.GetDirectoryName(path);
}
