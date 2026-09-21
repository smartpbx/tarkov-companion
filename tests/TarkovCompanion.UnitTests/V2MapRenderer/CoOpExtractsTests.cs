using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// Issue 573: "co-op extracts shouldnt highlight as options on the map, i pretty much never use
/// them" — a co-op extract is told apart by its own name, the same one every list already shows.
/// </summary>
public sealed class CoOpExtractsTests
{
    [Theory]
    [InlineData("Side Tunnel (Co-Op)", true)]
    [InlineData("Emercom Checkpoint (Co-op)", true)]
    [InlineData("Friendship Bridge (CO-OP)", true)]
    [InlineData("Cliff Descent", false)]
    [InlineData("Pier Boat", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void An_extract_is_co_op_only_when_its_own_name_says_so(string? name, bool expected) =>
        Assert.Equal(expected, CoOpExtracts.IsCoOp(name));

    [Theory]
    [InlineData("Hidden", CoOpExtractVisibility.Hidden)]
    [InlineData("hidden", CoOpExtractVisibility.Hidden)]
    [InlineData("Dim", CoOpExtractVisibility.Dim)]
    [InlineData("Normal", CoOpExtractVisibility.Normal)]
    [InlineData(null, CoOpExtractVisibility.Dim)]
    [InlineData("", CoOpExtractVisibility.Dim)]
    [InlineData("not-a-value", CoOpExtractVisibility.Dim)]
    public void An_unreadable_or_missing_stored_value_falls_back_to_dim_the_default(string? stored, CoOpExtractVisibility expected) =>
        Assert.Equal(expected, CoOpExtracts.ParseVisibility(stored));

    [Theory]
    [InlineData(CoOpExtractVisibility.Hidden, false)]
    [InlineData(CoOpExtractVisibility.Dim, false)]
    [InlineData(CoOpExtractVisibility.Normal, true)]
    public void A_co_op_extract_is_offered_only_at_normal(CoOpExtractVisibility visibility, bool expected) =>
        Assert.Equal(expected, CoOpExtracts.IsOffered("Side Tunnel (Co-Op)", visibility));

    [Theory]
    [InlineData(CoOpExtractVisibility.Hidden)]
    [InlineData(CoOpExtractVisibility.Dim)]
    [InlineData(CoOpExtractVisibility.Normal)]
    public void An_ordinary_extract_is_always_offered_whatever_the_co_op_setting(CoOpExtractVisibility visibility) =>
        Assert.True(CoOpExtracts.IsOffered("Cliff Descent", visibility));
}
