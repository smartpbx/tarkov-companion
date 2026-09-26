using TarkovCompanion.Application.Services.Ask;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.Ask;

/// <summary>
/// [#712 2-5] The Ask box against a real synced catalog: the item search, the hideout tables and the
/// ammunition and key facts as the 2026-09-14 sync stored them.
/// </summary>
/// <remarks>
/// Runs where the seed database is (<c>TARKOV_SEED_CATALOG</c>, by default the dev host's copy) and
/// reports itself skipped elsewhere, like the real-screenshot measurements. CI has no catalog; the
/// fixture tests beside this one carry the same names.
/// </remarks>
public sealed class AskSeedCatalogTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Answers_real_questions_from_the_real_catalog()
    {
        var seed = Environment.GetEnvironmentVariable("TARKOV_SEED_CATALOG") ?? "/root/orca/seed/catalog-2026-09-14.db";
        if (!File.Exists(seed))
        {
            output.WriteLine("[ask-seed] skipped: no seed catalog.");
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-ask-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "catalog.db");
            File.Copy(seed, path);
            var factory = new SqliteConnectionFactory(new(path));
            var items = new SqliteItemRepository(factory);
            var facts = new SqliteItemFactCatalog(factory);
            var source = new RulesAnswerSource(
                items,
                new ItemIntelService(items, new NoNeeds(), facts),
                requirements: new SqliteRequirementCatalog(factory),
                prerequisites: new SqliteHideoutPrerequisiteCatalog(factory),
                facts: facts);

            async Task<AskAnswer> Ask(string question)
            {
                var answer = await source.AnswerAsync(question, CancellationToken.None);
                output.WriteLine($"{question} -> {answer.Heading} | {answer.Unanswered} | {string.Join(" / ", answer.Lines.Select(line => $"{line.Code}({string.Join(", ", line.Arguments)})"))} | closest: {string.Join(", ", answer.Closest)}");
                return answer;
            }

            var key = await Ask("where is Dorms 314 key used");
            Assert.Equal("Dorm room 314 marked key", key.Heading);
            Assert.Contains(key.Lines, line => line.Code is AskLine.KeyOpens or AskLine.KeyOpensOnMap);

            var ledx = await Ask("is LEDX needed for anything");
            Assert.Equal("LEDX Skin Transilluminator", ledx.Heading);

            var lavatory = await Ask("what do I need for lavatory 2");
            Assert.Equal("Lavatory", lavatory.Heading);
            Assert.Contains(lavatory.Lines, line => Equals(line.Code, AskLine.HideoutItem));

            var ammo = await Ask("best 5.45 for class 4");
            var picks = ammo.Lines.Where(line => Equals(line.Code, AskLine.AmmoPick)).ToArray();
            Assert.NotEmpty(picks);
            Assert.All(picks, line => Assert.StartsWith("5.45x39mm", (string)line.Arguments[0]!, StringComparison.Ordinal));
            Assert.All(picks, line => Assert.True((int)line.Arguments[1]! >= 40));

            var which = await Ask("best 7.62 for class 5");
            Assert.Equal(AskUnanswered.Ambiguous, which.Unanswered);
            Assert.Contains(which.Closest, name => name.StartsWith("7.62x39", StringComparison.Ordinal));

            var nothing = await Ask("where is the zzq flux capacitor used");
            Assert.False(nothing.IsAnswered);
            Assert.Empty(nothing.Lines);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class NoNeeds : IQuestProgressService
    {
        public Task<ItemNeedSummary> GetItemNeedsAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(new ItemNeedSummary(0, 0, 0));
    }
}
