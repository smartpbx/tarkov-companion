using TarkovCompanion.App.Services.TestChecklist;

namespace TarkovCompanion.UnitTests.TestChecklist;

public sealed class TestChecklistCatalogTests
{
    private static string Item(string id, string area = "Raid", string needs = "[]", string extra = "") =>
        $$"""{ "id": "{{id}}", "area": "{{area}}", "feature": "F {{id}}", "needs": {{needs}}, "steps": ["one", "two"], "expect": "E"{{extra}} }""";

    private static string File(params string[] items) => $$"""{ "version": 1, "items": [{{string.Join(",", items)}}] }""";

    [Fact]
    public void Files_are_read_in_name_order_and_items_keep_their_order_inside_a_file()
    {
        var catalog = TestChecklistCatalog.Parse(
        [
            ("20-intel.json", File(Item("intel.b"), Item("intel.a"))),
            ("00-checklist.json", File(Item("checklist.open"))),
            ("10-raid.json", File(Item("raid.z"))),
        ]);

        Assert.Empty(catalog.Problems);
        Assert.Equal(["checklist.open", "raid.z", "intel.b", "intel.a"], catalog.Items.Select(item => item.Id));
        Assert.Equal(["one", "two"], catalog.Items[0].Steps);
    }

    [Fact]
    public void An_id_repeated_in_a_later_file_is_refused_and_the_first_is_kept()
    {
        var catalog = TestChecklistCatalog.Parse(
        [
            ("10-raid.json", File(Item("raid.now", area: "First"))),
            ("20-intel.json", File(Item("raid.now", area: "Second"), Item("intel.ok"))),
        ]);

        Assert.Equal(["raid.now", "intel.ok"], catalog.Items.Select(item => item.Id));
        Assert.Equal("First", catalog.Items[0].Area);
        var problem = Assert.Single(catalog.Problems);
        Assert.Equal("20-intel.json", problem.File);
        Assert.Contains("duplicate", problem.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "id": "Raid.Now", "area": "A", "feature": "F", "steps": ["s"], "expect": "E" }""", "\"id\"")]
    [InlineData("""{ "id": "raid.now", "feature": "F", "steps": ["s"], "expect": "E" }""", "\"area\"")]
    [InlineData("""{ "id": "raid.now", "area": "A", "feature": "F", "steps": [], "expect": "E" }""", "\"steps\"")]
    [InlineData("""{ "id": "raid.now", "area": "A", "feature": "F", "steps": ["s", 3], "expect": "E" }""", "\"steps\"")]
    [InlineData("""{ "id": "raid.now", "area": "A", "feature": "F", "steps": ["s"] }""", "\"expect\"")]
    [InlineData("""{ "id": "raid.now", "area": "A", "feature": "F", "needs": ["game", "tablte"], "steps": ["s"], "expect": "E" }""", "unknown need 'tablte'")]
    [InlineData("""{ "id": "raid.now", "area": "A", "feature": "F", "steps": ["s"], "expect": "E", "goTo": "raid" }""", "\"goTo\"")]
    [InlineData("""{ "id": "raid.now", "area": "A", "feature": "F", "steps": ["s"], "expect": "E", "flag": "no-such-flag" }""", "unknown flag")]
    [InlineData("""{ "id": "raid.now", "area": "A", "feature": "F", "steps": ["s"], "expect": "E", "refs": "#1" }""", "\"refs\"")]
    public void A_bad_field_skips_that_item_and_names_the_reason(string bad, string reason)
    {
        var catalog = TestChecklistCatalog.Parse([("10-raid.json", File(bad, Item("raid.fine")))]);

        Assert.Equal(["raid.fine"], catalog.Items.Select(item => item.Id));
        Assert.Contains(reason, Assert.Single(catalog.Problems).Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "version": 2, "items": [] }""")]
    [InlineData("""{ "version": 1 }""")]
    [InlineData("[]")]
    public void A_file_that_cannot_be_read_is_one_problem_and_the_other_files_still_load(string json)
    {
        var catalog = TestChecklistCatalog.Parse([("10-raid.json", json), ("20-intel.json", File(Item("intel.ok")))]);

        Assert.Equal(["intel.ok"], catalog.Items.Select(item => item.Id));
        Assert.Equal("10-raid.json", Assert.Single(catalog.Problems).File);
    }

    [Fact]
    public void Optional_fields_and_unknown_fields_are_read_or_ignored()
    {
        var catalog = TestChecklistCatalog.Parse(
        [
            ("10-raid.json", File(Item("raid.now.leave-by", needs: """["game", "in-raid"]""",
                extra: """, "goTo": "#/raid", "flag": "now-panel", "refs": ["#967"], "later": { "x": 1 }"""))),
        ]);

        Assert.Empty(catalog.Problems);
        var item = Assert.Single(catalog.Items);
        Assert.Equal(["game", "in-raid"], item.Needs);
        Assert.Equal("#/raid", item.GoTo);
        Assert.Equal("now-panel", item.Flag);
        Assert.Equal(["#967"], item.Refs);
    }
}
