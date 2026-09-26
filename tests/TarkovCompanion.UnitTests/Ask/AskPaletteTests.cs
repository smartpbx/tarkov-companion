using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Ask;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Ask;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.UnitTests.Ask;

/// <summary>[#712 2-5] The palette's Ask mode, in the composed app and on its own.</summary>
public sealed class AskPaletteTests
{
    [Fact]
    public async Task The_composed_palette_answers_a_question_and_leaves_a_command_word_alone()
    {
        await WithShellAsync(async shell =>
        {
            Assert.NotNull(shell.Ask);
            shell.PaletteCommand.Execute(null);

            shell.PaletteQuery = "theme";
            Assert.False(shell.Ask!.IsShowing);
            Assert.NotEmpty(shell.FilteredCommandItems);

            // No raid map is open in a fresh app, so the honest answer is that there is none.
            shell.PaletteQuery = "best extract from here";
            Assert.True(shell.Ask.IsShowing);
            await shell.Ask.PendingAnswer!;
            Assert.Equal(AskUnanswered.NoRaid, shell.Ask.Answer!.Unanswered);
            Assert.Empty(shell.Ask.Lines);
            Assert.Equal(AskText.Unanswered(AskUnanswered.NoRaid, string.Empty), shell.Ask.Unanswered);
        });
    }

    [Fact]
    public async Task A_link_closes_the_palette_and_opens_its_page()
    {
        await WithShellAsync(shell =>
        {
            shell.PaletteCommand.Execute(null);
            shell.OpenAskLink(new AskLink(AskLinkKind.PlanHideout, "5d484fba654e7600691aadf7", "Lavatory"));

            Assert.False(shell.IsPaletteOpen);
            Assert.Equal(V2Routes.Hideout, shell.Router.Current.Location.Route);

            shell.OpenAskLink(new AskLink(AskLinkKind.PlanQuest, "Gunsmith Master - Part 5", "Gunsmith Master - Part 5"));
            Assert.Equal(V2Routes.Plan, shell.Router.Current.Location.Route);
            Assert.Equal("Gunsmith Master - Part 5", shell.PlanWorkspace!.SearchText);

            shell.OpenAskLink(new AskLink(AskLinkKind.Raid));
            Assert.Equal(V2Routes.Raid, shell.Router.Current.Location.Route);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_card_says_each_line_and_its_sources_and_a_closest_name_rewrites_the_question()
    {
        using var ask = new AskViewModel(new AskService([AskFixtures.Source()]), debounce: TimeSpan.Zero);
        var replaced = new List<string>();
        ask.QueryReplaced += (_, text) => replaced.Add(text);

        ask.SetQuery("what do I need for Gunsmith 5");
        await ask.PendingAnswer!;

        Assert.Equal("Gunsmith Master - Part 5", ask.Title);
        Assert.Equal(PhraseText.Say(new Phrase(AskLine.QuestGiverLevel, "Mechanic", 40)), ask.Lines[0]);
        Assert.Contains(LocalTime.Moment(AskFixtures.CatalogTime), ask.SourceLine, StringComparison.Ordinal);
        Assert.Single(ask.Links);

        ask.SetQuery("what do I need for Gunsmith 99");
        await ask.PendingAnswer!;
        Assert.True(ask.IsUnanswered);
        Assert.Empty(ask.Lines);
        var suggestion = ask.Suggestions.First(item => item.Name.StartsWith("Gunsmith Master", StringComparison.Ordinal));
        suggestion.AskCommand.Execute(null);
        Assert.Equal($"what do I need for {suggestion.Name}", replaced[^1]);
    }

    [Fact]
    public async Task A_newer_question_replaces_an_older_lookup()
    {
        using var ask = new AskViewModel(new AskService([AskFixtures.Source()]), debounce: TimeSpan.FromMilliseconds(50));

        ask.SetQuery("what do I need for Gunsmith 5");
        var first = ask.PendingAnswer!;
        ask.SetQuery("what do I need for lavatory 2");
        await first;
        await ask.PendingAnswer!;

        Assert.Equal("Lavatory", ask.Title);
    }

    private static async Task WithShellAsync(Func<V2ShellViewModel, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-ask-{Guid.NewGuid():N}");
        try
        {
            await using var services = AppComposition.Build(
                new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                new(DataRoot: root, Offline: true));
            var legacy = services.GetRequiredService<MainWindowViewModel>();
            var shell = services.GetRequiredService<V2ShellViewModel>();
            legacy.PreviewShell = shell;
            await legacy.InitializeAsync();
            await test(shell);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
