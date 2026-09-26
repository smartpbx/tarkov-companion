using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Platform.Windows.Discovery;

namespace TarkovCompanion.UnitTests.Readiness;

/// <summary>
/// [#712 1-13] Discovery against real folder trees laid out like the machines it has to work on:
/// the Steam release, a launcher install on another drive, Documents redirected into OneDrive,
/// and nothing installed at all. The guesses and the choosing between them are the shipped code;
/// only the facts Windows would report are made up.
/// </summary>
public sealed class EftFolderDiscoveryFixtureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tc-discovery-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task Steam_release_is_found_in_a_library_the_vdf_lists_on_another_drive()
    {
        var steam = Dir("C", "Program Files (x86)", "Steam");
        var library = Path.Combine(_root, "D", "Games", "SteamLibrary");
        var game = Dir("D", "Games", "SteamLibrary", "steamapps", "common", "Escape from Tarkov");
        Dir("D", "Games", "SteamLibrary", "steamapps", "common", "Escape from Tarkov", "Logs", "log_2026.09.26_20-00-00_1.1.5.1.47510");
        Write(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam.Replace("\\", "\\\\") + "\"\n\t}\n" +
            "\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + library.Replace("\\", "\\\\") + "\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"3932890\"\t\t\"1\"\n\t\t}\n\t}\n}\n");

        var found = await Discover(Environment() with { SteamRoots = [steam] });

        Assert.Equal(EftInstallDiscoveryStatus.Ready, found.Status);
        Assert.Equal(game, found.Paths.InstallRoot);
        Assert.Equal(Path.Combine(game, "Logs"), found.Paths.LogRoot);
    }

    [Fact]
    public async Task Launcher_install_on_a_second_drive_is_found_without_a_registry_key()
    {
        var game = Dir("E", "Battlestate Games", "Escape from Tarkov");
        Dir("E", "Battlestate Games", "Escape from Tarkov", "Logs");

        var found = await Discover(Environment() with { FixedDrives = [Path.Combine(_root, "C"), Path.Combine(_root, "E")] });

        Assert.Equal(game, found.Paths.InstallRoot);
        Assert.Equal(Path.Combine(game, "Logs"), found.Paths.LogRoot);
    }

    [Fact]
    public async Task OneDrive_redirected_documents_win_over_an_abandoned_local_screenshots_folder()
    {
        Dir("E", "Battlestate Games", "Escape from Tarkov", "Logs");
        var abandoned = Dir("Users", "p", "Documents", "Escape from Tarkov", "Screenshots");
        var used = Dir("Users", "p", "OneDrive", "Documents", "Escape from Tarkov", "Screenshots");
        Image(abandoned, "2025-01-01[10-00]_old.png", DateTime.UtcNow.AddDays(-200));
        Image(used, "2026-09-26[20-00]_new.png", DateTime.UtcNow.AddMinutes(-5));

        // Windows reports Documents inside OneDrive; the profile's own Documents still exists.
        var found = await Discover(Environment() with
        {
            Documents = Path.Combine(_root, "Users", "p", "OneDrive", "Documents"),
            FixedDrives = [Path.Combine(_root, "E")],
        });

        Assert.Equal(used, found.Paths.ScreenshotRoot);
    }

    [Fact]
    public async Task A_work_OneDrive_the_known_folder_does_not_report_is_still_looked_in()
    {
        var used = Dir("Users", "p", "OneDrive - Contoso", "Documents", "Escape from Tarkov", "Screenshots");

        var found = await Discover(Environment() with { OneDriveRoots = [Path.Combine(_root, "Users", "p", "OneDrive - Contoso")] });

        Assert.Equal(used, found.Paths.ScreenshotRoot);
    }

    [Fact]
    public async Task Nothing_installed_is_missing_with_no_folders_rather_than_a_wrong_guess()
    {
        Dir("C");

        var found = await Discover(Environment());

        Assert.Equal(EftInstallDiscoveryStatus.Missing, found.Status);
        Assert.Null(found.Paths.InstallRoot);
        Assert.Null(found.Paths.LogRoot);
        Assert.Null(found.Paths.ScreenshotRoot);
    }

    [Fact]
    public async Task An_existing_players_named_folders_still_win_over_everything_discovery_finds()
    {
        Dir("E", "Battlestate Games", "Escape from Tarkov", "Logs");
        var named = Dir("Z", "MyLogs");
        var namedShots = Dir("Z", "MyShots");

        var found = await Discover(
            Environment() with { FixedDrives = [Path.Combine(_root, "E")] },
            new FixedOverrides(new EftPathOverrides(namedShots, named)));

        Assert.Equal(named, found.Paths.LogRoot);
        Assert.Equal(namedShots, found.Paths.ScreenshotRoot);
    }

    [Fact]
    public void Steam_library_list_parses_paths_and_unescapes_backslashes()
    {
        var text = "\"libraryfolders\"\n{\n\"0\"\n{\n\"path\" \"C:\\\\Program Files (x86)\\\\Steam\"\n\"label\" \"\"\n}\n\"1\"\n{\n\"path\" \"D:\\\\SteamLibrary\"\n}\n}";

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], SteamLibraryFolders.Parse(text));
        Assert.Empty(SteamLibraryFolders.Parse(null));
        Assert.Empty(SteamLibraryFolders.Parse("not a vdf"));
    }

    private async Task<EftInstallDiscoverySnapshot> Discover(EftDiscoveryEnvironment environment, IEftPathOverrideStore? overrides = null)
    {
        var locator = new WindowsEftPathLocator(new SystemEftPathProbe(() => environment), overrides);
        return await locator.RefreshAsync(CancellationToken.None);
    }

    /// <summary>A machine with a C: drive, a profile, and nothing else found.</summary>
    private EftDiscoveryEnvironment Environment()
    {
        var profile = Path.Combine(_root, "Users", "p");
        return new(
            Documents: Path.Combine(profile, "Documents"),
            Pictures: Path.Combine(profile, "Pictures"),
            UserProfile: profile,
            LocalAppData: Path.Combine(profile, "AppData", "Local"),
            SystemDrive: Path.Combine(_root, "C"),
            ProgramFiles: Path.Combine(_root, "C", "Program Files"),
            FixedDrives: [Path.Combine(_root, "C")],
            OneDriveRoots: [],
            RegisteredInstallRoots: [],
            SteamRoots: [],
            RunningGameRoots: []);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void Image(string folder, string name, DateTime writtenUtc)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        File.SetLastWriteTimeUtc(path, writtenUtc);
    }

    private sealed class FixedOverrides(EftPathOverrides value) : IEftPathOverrideStore
    {
        public Task<EftPathOverrides> GetAsync(CancellationToken cancellationToken) => Task.FromResult(value);

        public Task SaveAsync(EftPathOverrides overrides, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
