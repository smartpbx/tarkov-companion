using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>
/// [#902 P9] Findability: the palette finds every setting and lands on its row, What's new can be
/// opened again, the Capture shortcut has a switch, and an item on any page opens its Intel.
/// </summary>
/// <remarks>
/// A separate partial so the hot constructor gains nothing: every member here is built on first
/// use. The views reach the commands through the shell they sit in, which is also why a Keep, Stash
/// or Loot row needs no callback threaded through its own view model to open Intel.
/// </remarks>
public sealed partial class V2ShellViewModel
{
    /// <summary>
    /// Commands that act on the address, the pin list and the preview's navigation, which are drawn
    /// only in developer mode. Their keys still work; the palette just stops offering them.
    /// </summary>
    private static readonly HashSet<string> DeveloperOnlyCommandIds = new(StringComparer.Ordinal)
    {
        "copy-address",
        "pin",
        "reset-preview",
    };

    private IReadOnlyList<V2ShellCommandViewModel>? _settingItems;
    private V2ShellCommandViewModel? _whatsNewItem;
    private ICommand? _toggleCaptureShortcutCommand;
    private ICommand? _openWhatsNewCommand;
    private ICommand? _openItemIntelCommand;

    /// <summary>The palette's rows for <see cref="V2SettingsIndex.All"/>.</summary>
    public IReadOnlyList<V2ShellCommandViewModel> SettingItems => _settingItems ??=
    [
        .. V2SettingsIndex.All.Select(entry => new V2ShellCommandViewModel(
            new V2ShellCommand($"setting.{entry.Id}", entry.LabelKey, V2ShellCommandKind.Navigate, null, entry.Route),
            new DelegateCommand(() => OpenSetting(entry)))
        {
            Where = SettingWhere(entry),
            Keywords = entry.Keywords,
        }),
    ];

    /// <summary>The Capture dialog's and Setup's switch for Alt+Shift+C, the palette command's state.</summary>
    public ICommand ToggleCaptureShortcutCommand => _toggleCaptureShortcutCommand ??= new DelegateCommand(ToggleCaptureShortcut);

    public string CaptureShortcutSwitchLabel => ShellText.CaptureShortcutSwitch;

    /// <summary>Opens this build's change list at any time, from About or the palette.</summary>
    public ICommand OpenWhatsNewCommand => _openWhatsNewCommand ??= new DelegateCommand(OpenWhatsNew);

    public bool HasWhatsNew => ReleaseExperience is not null;

    public string OpenWhatsNewLabel => ShellText.WhatsNewInThisBuild;

    /// <summary>Opens an item's Intel from a row on any page; the parameter is the item id.</summary>
    public ICommand OpenItemIntelCommand => _openItemIntelCommand ??= new ParameterCommand<string>(OpenItemIntel);

    public string OpenItemIntelLabel => ShellText.OpenInIntel;

    /// <summary>Whether the palette offers a command: developer-only ones need developer mode.</summary>
    internal bool OffersInPalette(V2ShellCommand command) =>
        IsDeveloperMode || !DeveloperOnlyCommandIds.Contains(command.Id);

    /// <summary>
    /// What the palette lists for a query: the commands, What's new, and once something is typed,
    /// every setting whose name, home or other words match it.
    /// </summary>
    private IReadOnlyList<V2ShellCommandViewModel> FilterPalette(string query)
    {
        var offered = CommandItems
            .Zip(Commands)
            .Where(pair => OffersInPalette(pair.Second))
            .Select(pair => pair.First);
        if (WhatsNewItem is { } whatsNew)
        {
            offered = offered.Append(whatsNew);
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return [.. offered];
        }

        return
        [
            .. offered.Where(command => command.MatchesQuery(query)),
            .. SettingItems.Where(setting => setting.MatchesQuery(query)),
        ];
    }

    private V2ShellCommandViewModel? WhatsNewItem => ReleaseExperience is null
        ? null
        : _whatsNewItem ??= new V2ShellCommandViewModel(
            new V2ShellCommand("whats-new", "V2.Shell.WhatsNew", V2ShellCommandKind.Navigate, null),
            OpenWhatsNewCommand)
        {
            Keywords = ["changelog", "release notes", "changes"],
        };

    private static string SettingWhere(V2SettingEntry entry)
    {
        if (entry.Section is { } section)
        {
            return ShellText.SettingWhere(
                V2ShellText.Get(V2ShellVariants.A.Setup.LabelKey),
                V2ShellText.Get($"V2.Setup.Section.{section}"));
        }

        return V2ShellText.Get(V2RouteRegistry.Default[entry.Route].HeadingKey);
    }

    /// <summary>
    /// Goes to a setting's page, opens its Setup section, and moves focus onto its row, which
    /// scrolls it into view. Focus is the landing because it is also what a screen reader follows.
    /// </summary>
    internal void OpenSetting(V2SettingEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var invoker = _dialogInvoker ?? V2ShellFocusTargets.Palette;
        if (HasOpenDialog)
        {
            CloseDialog(restoreInvoker: false);
        }

        var opened = Router.Navigate(entry.Route, invoker);
        Act(opened);
        if (!opened.Succeeded)
        {
            return;
        }

        if (entry.Section is { } section)
        {
            SetupWorkspace?.Select(section);
        }

        if (entry.Route == V2Routes.Keep)
        {
            _keep?.LootRules?.Open();
        }

        FocusRequested?.Invoke(this, new(entry.Target, V2FocusReason.Setting));
    }

    private void OpenWhatsNew()
    {
        if (HasOpenDialog)
        {
            CloseDialog(restoreInvoker: false);
        }

        ReleaseExperience?.Show();
    }

    private void OpenItemIntel(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        OpenSuggestedItem(itemId, V2ShellRouter.PageHeadingTarget);
    }

    /// <summary>Whether the top bar's map picker belongs on this page: only where a map is shown.</summary>
    /// <remarks>
    /// [#902 P9] It sat on every page and changed only the Raid map, so on Intel or Setup a pick
    /// did nothing visible and then surprised the player on the next visit to Raid.
    /// </remarks>
    private bool PageShowsAMap => Router.CurrentDestination is { } destination &&
        (destination == V2Routes.Raid || destination == V2Routes.Team || destination == V2Routes.Plan);
}
