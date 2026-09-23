using System.Text.Json;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.UnitTests;

/// <summary>Requirement chains measured from the 2026-09-14 Labs and Reserve catalog rows.</summary>
public sealed class MapExtractRequirementReaderTests
{
    [Fact]
    public void LabsElevatorChainIsPutInActivationOrder()
    {
        using var map = JsonDocument.Parse(Labs);
        var switches = MapExtractRequirementReader.ReadSwitches(map.RootElement);
        var requirements = MapExtractRequirementReader.Read(
            map.RootElement.GetProperty("extracts")[0],
            switches);

        Assert.Equal(
            ["Med Elevator Power Button", "Med Elevator Call Button", "Med Elevator Extract Button"],
            requirements.SwitchChain.Select(item => item.Name));
        Assert.Equal(
            "Needs power: Med Elevator Power Button, then Med Elevator Call Button, then Med Elevator Extract Button",
            MapExtractRequirementReader.Describe(requirements));
    }

    [Fact]
    public void ReserveD2PowerUnlocksTheDoorSwitch()
    {
        using var map = JsonDocument.Parse(Reserve);
        var switches = MapExtractRequirementReader.ReadSwitches(map.RootElement);
        var ordered = MapExtractRequirementReader.Order(
            [switches["fa1f22e776e4724582eb1dbb18ae864a9303cc5a"], switches["9cad024bc31223f02f7296c3cb1834c5f6fa2fe2"]]);

        Assert.Equal(["D-2 Power Switch", "D-2 Door Switch"], ordered.Select(item => item.Name));
    }

    [Fact]
    public void TransferCoOpAndOneUseRemainTyped()
    {
        using var map = JsonDocument.Parse("""
            {"switches":[],"extracts":[{"name":"Test (Co-Op)","oneTime":true,
              "transferItem":{"item":"roubles","count":20000}}]}
            """);
        var requirements = MapExtractRequirementReader.Read(
            map.RootElement.GetProperty("extracts")[0],
            MapExtractRequirementReader.ReadSwitches(map.RootElement),
            id => id == "roubles" ? "Roubles" : null);

        Assert.True(requirements.RequiresPayment);
        Assert.True(requirements.RequiresCoOp);
        Assert.True(requirements.IsOneTime);
        Assert.Equal("Costs 20,000 ₽ · Needs co-op partner · One use", MapExtractRequirementReader.Describe(requirements));
    }

    private const string Labs = """
        {
          "switches": [
            {"id":"014e9122e0fad99bcff6f93161712c3c6e1a2a75","name":"Med Elevator Power Button","switchType":"Open","activatedBy":false,
             "activates":[{"operation":"Unlock","switch":"d1d16f043cea7be35b2729c5e1e1dd648c6dee13"}],"position":{"x":-124.758,"y":-2.31599617,"z":-313.806}},
            {"id":"d1d16f043cea7be35b2729c5e1e1dd648c6dee13","name":"Med Elevator Call Button","switchType":"Open","activatedBy":"014e9122e0fad99bcff6f93161712c3c6e1a2a75",
             "activates":[],"position":{"x":-114.112,"y":-2.84599972,"z":-343.2}},
            {"id":"5077fd224eb1bb588b65e4d3c0b4b5719bdebb66","name":"Med Elevator Extract Button","switchType":"Open","activatedBy":false,
             "activates":[],"position":{"x":-112.802,"y":-2.84599972,"z":-342.762}}
          ],
          "extracts": [{"name":"Hangar Gate","switch":"5077fd224eb1bb588b65e4d3c0b4b5719bdebb66",
            "switches":["5077fd224eb1bb588b65e4d3c0b4b5719bdebb66","d1d16f043cea7be35b2729c5e1e1dd648c6dee13","014e9122e0fad99bcff6f93161712c3c6e1a2a75"]}]
        }
        """;

    private const string Reserve = """
        {"switches":[
          {"id":"9cad024bc31223f02f7296c3cb1834c5f6fa2fe2","name":"D-2 Power Switch","switchType":"Open","activatedBy":false,
           "activates":[{"operation":"Unlock","switch":"fa1f22e776e4724582eb1dbb18ae864a9303cc5a"}],"position":{"x":-117.184174,"y":-12.954,"z":22.6676826}},
          {"id":"fa1f22e776e4724582eb1dbb18ae864a9303cc5a","name":"D-2 Door Switch","switchType":"Close","activatedBy":"9cad024bc31223f02f7296c3cb1834c5f6fa2fe2",
           "activates":[],"position":{"x":-117.449867,"y":-16.9842987,"z":168.546936}}
        ]}
        """;
}
