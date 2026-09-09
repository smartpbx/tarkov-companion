using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.WindowsSmokeTests;

public sealed class PlatformContractTests
{
    [Fact]
    public void SimulatorWindowRequiresExplicitIdentity()
    {
        var descriptor = new WindowDescriptor(
            42,
            "TarkovCompanion.EftSimulator",
            "Tarkov Companion EFT Simulator",
            new PixelRect(0, 0, 1920, 1080),
            false,
            true);

        Assert.True(descriptor.IsSimulator);
        Assert.NotEqual("EscapeFromTarkov", descriptor.ProcessName);
    }
}
