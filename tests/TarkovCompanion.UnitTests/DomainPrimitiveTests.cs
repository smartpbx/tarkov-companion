using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.UnitTests;

public sealed class DomainPrimitiveTests
{
    [Fact]
    public void Dimensions_CalculateSlots()
    {
        var dimensions = new ItemDimensions(2, 3);

        Assert.Equal(6, dimensions.Slots);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void Dimensions_RejectNonPositiveValues(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ItemDimensions(width, height));

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Confidence_RejectsOutOfRangeValues(double value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Confidence(value));
}
