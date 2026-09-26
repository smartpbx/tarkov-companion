namespace TarkovCompanion.Platform.Windows.Discovery;

/// <summary>
/// [#712 1-13] What the machine says about itself, read once, before any guess is made from it.
/// </summary>
/// <remarks>
/// The folder guesses used to be built inside the code that asked Windows for these facts, so the
/// only way to test a guess was a Windows machine laid out the way the guess expected. Split, the
/// guesses are a pure function of this record and a fixture tree on any machine can stand in for
/// a Steam install, a launcher install on another drive, or a Documents folder OneDrive has moved.
/// </remarks>
/// <param name="Documents">The Documents known folder, wherever it points (OneDrive or not).</param>
/// <param name="Pictures">The Pictures known folder.</param>
/// <param name="UserProfile">The user's profile folder.</param>
/// <param name="LocalAppData">The user's local application data folder.</param>
/// <param name="SystemDrive">The root of the drive Windows is on, "C:\" on most machines.</param>
/// <param name="ProgramFiles">The Program Files folder.</param>
/// <param name="FixedDrives">Every fixed drive's root; the launcher lets a player install to any of them.</param>
/// <param name="OneDriveRoots">Where OneDrive says it keeps the user's folders (personal and work).</param>
/// <param name="RegisteredInstallRoots">Install folders the uninstall keys name (launcher and Steam).</param>
/// <param name="SteamRoots">Steam's own folder, from its registry key.</param>
/// <param name="RunningGameRoots">The folder of a running launcher or anti-cheat process, when there is one.</param>
public sealed record EftDiscoveryEnvironment(
    string? Documents,
    string? Pictures,
    string? UserProfile,
    string? LocalAppData,
    string SystemDrive,
    string? ProgramFiles,
    IReadOnlyList<string> FixedDrives,
    IReadOnlyList<string> OneDriveRoots,
    IReadOnlyList<string> RegisteredInstallRoots,
    IReadOnlyList<string> SteamRoots,
    IReadOnlyList<string> RunningGameRoots)
{
    /// <summary>
    /// Reads a Steam library list (<c>steamapps/libraryfolders.vdf</c>) given the root it lives
    /// under; null when there is no such file. Swapped for a fixture reader in tests.
    /// </summary>
    public Func<string, string?> ReadSteamLibraryList { get; init; } = DefaultReadSteamLibraryList;

    private static string? DefaultReadSteamLibraryList(string steamRoot)
    {
        try
        {
            var path = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// [#712 1-13] Every folder the game's install, logs and screenshots could be in, from an
/// <see cref="EftDiscoveryEnvironment"/>. Pure: nothing here touches the disk except through
/// <see cref="EftDiscoveryEnvironment.ReadSteamLibraryList"/>.
/// </summary>
public static class EftPathCandidateBuilder
{
    /// <summary>The folder names the game's own install uses under a Steam library or a drive root.</summary>
    /// <remarks>
    /// The launcher's default is "Battlestate Games\Escape from Tarkov"; the abbreviated "EFT" is
    /// what older installs used. Steam puts the game under steamapps\common by its install-dir
    /// name, and both spellings are offered because a wrong guess that does not exist costs
    /// nothing.
    /// </remarks>
    private static readonly string[] SteamInstallDirs = ["Escape from Tarkov", "EscapeFromTarkov"];

    public static EftPathCandidates Build(EftDiscoveryEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var programFiles = environment.ProgramFiles;
        var driveRoots = new[] { environment.SystemDrive }
            .Concat(environment.FixedDrives)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var installRoots = environment.RunningGameRoots
            .Concat(environment.RegisteredInstallRoots)
            .Concat(SteamInstallRoots(environment))
            .Concat(new[]
            {
                Path.Combine(environment.SystemDrive, "Battlestate Games", "Escape from Tarkov"),
                programFiles is null ? null : Path.Combine(programFiles, "Battlestate Games", "Escape from Tarkov"),
                Path.Combine(environment.SystemDrive, "Battlestate Games", "EFT"),
                programFiles is null ? null : Path.Combine(programFiles, "Battlestate Games", "EFT"),
            }.OfType<string>())
            // A player who picked another drive in the launcher gets the same folder names on it.
            .Concat(driveRoots.SelectMany(drive => new[]
            {
                Path.Combine(drive, "Battlestate Games", "Escape from Tarkov"),
                Path.Combine(drive, "Battlestate Games", "EFT"),
                Path.Combine(drive, "Games", "Escape from Tarkov"),
                Path.Combine(drive, "Escape from Tarkov"),
            }))
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Every folder a personal Documents or Pictures could be, because the one the API
        // reports is only right when nothing has moved it.
        //
        // OneDrive redirects Documents on the machine this was written against and does not on
        // the machine of the first person to install it, and those two cases produce different
        // paths from the same call. Worse, a machine can have both at once: OneDrive owns the
        // known folder while the game, configured earlier, still writes into the original. So
        // both are offered and the one holding the newest screenshot wins.
        var profile = environment.UserProfile;
        var personalRoots = new[]
            {
                environment.Documents,
                environment.Pictures,
                profile is null ? null : Path.Combine(profile, "Documents"),
                profile is null ? null : Path.Combine(profile, "Pictures"),
                profile is null ? null : Path.Combine(profile, "OneDrive", "Documents"),
                profile is null ? null : Path.Combine(profile, "OneDrive", "Pictures"),
            }
            .Concat(environment.OneDriveRoots.SelectMany(root => new[]
            {
                Path.Combine(root, "Documents"),
                Path.Combine(root, "Pictures"),
            }))
            .OfType<string>()
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var localLow = environment.LocalAppData is { } localAppData && Directory.GetParent(localAppData)?.FullName is { } appData
            ? Path.Combine(appData, "LocalLow")
            : environment.LocalAppData;

        // The game writes its logs inside its own install directory, one folder per launch.
        // Looking only under LocalLow and Documents found nothing on a real installation, so
        // raid tracking never started at all. Install-relative paths come first because that
        // is where the logs actually are.
        var logRoots = installRoots
            .Select(root => Path.Combine(root, "Logs"))
            .Concat(localLow is null ? [] : [Path.Combine(localLow, "Battlestate Games", "EscapeFromTarkov", "Logs")])
            .Concat(personalRoots.Select(root => Path.Combine(root, "Escape from Tarkov", "Logs")))
            .ToArray();

        // The explicit Screenshots folders come before the bare game folders, so that where
        // nothing has an image in it the more specific guess wins rather than the folder that
        // merely contains it.
        var screenshotRoots = installRoots
            .Select(root => Path.Combine(root, "Screenshots"))
            .Concat(personalRoots.Select(root => Path.Combine(root, "Escape from Tarkov", "Screenshots")))
            .Concat(personalRoots.Select(root => Path.Combine(root, "Escape from Tarkov")))
            .ToArray();
        return new(installRoots, logRoots, screenshotRoots);
    }

    /// <summary>
    /// The game's folder under every Steam library: Steam's own folder, each library its
    /// <c>libraryfolders.vdf</c> lists, and a "SteamLibrary" folder at each drive's root (the name
    /// Steam suggests for a new library).
    /// </summary>
    private static IEnumerable<string> SteamInstallRoots(EftDiscoveryEnvironment environment)
    {
        var libraries = environment.SteamRoots
            .Concat(environment.SteamRoots.SelectMany(root => SteamLibraryFolders.Parse(environment.ReadSteamLibraryList(root))))
            .Concat(new[] { environment.SystemDrive }.Concat(environment.FixedDrives)
                .Where(drive => !string.IsNullOrWhiteSpace(drive))
                .Select(drive => Path.Combine(drive, "SteamLibrary")))
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return libraries.SelectMany(library => SteamInstallDirs.Select(name => Path.Combine(library, "steamapps", "common", name)));
    }
}

/// <summary>Reads the library paths out of Steam's <c>libraryfolders.vdf</c>.</summary>
/// <remarks>
/// The file is Valve's KeyValues text: quoted keys and values, braces for nesting, backslashes
/// doubled. Only the <c>"path"</c> values are wanted, so this reads quoted pairs and ignores the
/// structure; a file it cannot make sense of gives no libraries rather than an error.
/// </remarks>
public static class SteamLibraryFolders
{
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var tokens = new List<string>();
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '"')
            {
                continue;
            }

            var builder = new System.Text.StringBuilder();
            index++;
            while (index < text.Length && text[index] != '"')
            {
                if (text[index] == '\\' && index + 1 < text.Length)
                {
                    index++;
                }

                builder.Append(text[index]);
                index++;
            }

            tokens.Add(builder.ToString());
        }

        var paths = new List<string>();
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (string.Equals(tokens[index], "path", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(tokens[index + 1]))
            {
                paths.Add(tokens[index + 1]);
                index++;
            }
        }

        return paths;
    }
}
