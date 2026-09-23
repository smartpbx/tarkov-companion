using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Infrastructure.Persistence;
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
    public async Task CheckedOverridesReplaceRealCatalogLinksAndReachReviewedLabsExtract()
    {
        var features = await ReadFeaturesAsync("the-lab", LabsOverrideMap);

        var main = Assert.Single(features, feature => feature.Name == "Main Elevator");
        Assert.Equal(
            ["Main Elevator Power Button", "Main Elevator Call Button", "Main Elevator Extract Button"],
            main.ExtractRequirements!.SwitchChain.Select(item => item.Name));

        var ventilation = Assert.Single(features, feature => feature.Name == "Ventilation Shaft");
        Assert.Null(ventilation.ExtractRequirements);

        var medical = Assert.Single(features, feature => feature.Name == "Medical Block Elevator");
        Assert.Equal(
            ["Med Elevator Power Button", "Med Elevator Call Button", "Med Elevator Extract Button"],
            medical.ExtractRequirements!.SwitchChain.Select(item => item.Name));
    }

    [Fact]
    public async Task CheckedOverridesReachRealAndReviewedReserveExtractsWithoutDroppingCatalogRequirements()
    {
        var features = await ReadFeaturesAsync("reserve", ReserveOverrideMap, seedMinefieldMap: true);

        var hermetic = Assert.Single(features, feature => feature.Name == "Bunker Hermetic Door");
        Assert.Equal(
            ["Bunker Hermetic Door Power Switch"],
            hermetic.ExtractRequirements!.SwitchChain.Select(item => item.Name));

        var d2 = Assert.Single(features, feature => feature.Name == "D-2");
        Assert.Equal(
            ["D-2 Power Switch", "D-2 Door Switch"],
            d2.ExtractRequirements!.SwitchChain.Select(item => item.Name));

        var woods = Assert.Single(features, feature => feature.Name == "Exit to Woods");
        Assert.Equal("Minefield map (Reserve)", woods.ExtractRequirements!.Transfer!.ItemName);
        Assert.Contains("Needs key: Minefield map (Reserve)", woods.Detail, StringComparison.Ordinal);

        var coOp = Assert.Single(features, feature => feature.Name == "Scav Lands (Co-Op)");
        Assert.True(coOp.ExtractRequirements!.RequiresCoOp);
        Assert.Contains("Needs co-op partner", coOp.Detail, StringComparison.Ordinal);
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

    private const string LabsOverrideMap = """
        {
          "id":"lab","name":"The Lab","normalizedName":"the-lab",
          "switches":[
            {"id":"911ef57b76415c1d42fdba3b5c8fdb587dd229a7","name":"Main Elevator Power Button","switchType":"Open","activatedBy":false,
             "activates":[{"operation":"Unlock","switch":"72848c177c54e2913e7d0259f23fba929d14ebf5"}],"position":{"x":-271.4,"y":-2.4,"z":-366.1}},
            {"id":"72848c177c54e2913e7d0259f23fba929d14ebf5","name":"Main Elevator Call Button","switchType":"Open","activatedBy":"911ef57b76415c1d42fdba3b5c8fdb587dd229a7",
             "activates":[],"position":{"x":-281.0,"y":-2.8,"z":-335.5}},
            {"id":"89170f110a27deb607f63b2382f28c25ba75e861","name":"Main Elevator Extract Button","switchType":"Open","activatedBy":false,
             "activates":[],"position":{"x":-282.4,"y":-2.9,"z":-335.9}},
            {"id":"014e9122e0fad99bcff6f93161712c3c6e1a2a75","name":"Med Elevator Power Button","switchType":"Open","activatedBy":false,
             "activates":[{"operation":"Unlock","switch":"d1d16f043cea7be35b2729c5e1e1dd648c6dee13"}],"position":{"x":-124.8,"y":-2.3,"z":-313.8}},
            {"id":"d1d16f043cea7be35b2729c5e1e1dd648c6dee13","name":"Med Elevator Call Button","switchType":"Open","activatedBy":"014e9122e0fad99bcff6f93161712c3c6e1a2a75",
             "activates":[],"position":{"x":-114.1,"y":-2.8,"z":-343.2}},
            {"id":"5077fd224eb1bb588b65e4d3c0b4b5719bdebb66","name":"Med Elevator Extract Button","switchType":"Open","activatedBy":false,
             "activates":[],"position":{"x":-112.8,"y":-2.8,"z":-342.8}}
          ],
          "extracts":[
            {"id":"main","name":"Main Elevator","faction":"shared","position":{"x":-282.4,"y":-2.9,"z":-335.9},
             "switch":"5077fd224eb1bb588b65e4d3c0b4b5719bdebb66","switches":["5077fd224eb1bb588b65e4d3c0b4b5719bdebb66","d1d16f043cea7be35b2729c5e1e1dd648c6dee13","014e9122e0fad99bcff6f93161712c3c6e1a2a75"]},
            {"id":"vent","name":"Ventilation Shaft","faction":"pmc","position":{"x":-130.0,"y":-6.8,"z":-250.0},
             "switch":"5077fd224eb1bb588b65e4d3c0b4b5719bdebb66","switches":["5077fd224eb1bb588b65e4d3c0b4b5719bdebb66","d1d16f043cea7be35b2729c5e1e1dd648c6dee13","014e9122e0fad99bcff6f93161712c3c6e1a2a75"]}
          ]
        }
        """;

    private const string ReserveOverrideMap = """
        {
          "id":"reserve","name":"Reserve","normalizedName":"reserve",
          "switches":[
            {"id":"2c4a9ff1d031afefbd0965b189f58e3834cdb367","name":"Bunker Hermetic Door Power Switch","switchType":"Open","activatedBy":false,
             "activates":[],"position":{"x":-60.8,"y":-5.6,"z":78.2}},
            {"id":"9cad024bc31223f02f7296c3cb1834c5f6fa2fe2","name":"D-2 Power Switch","switchType":"Open","activatedBy":false,
             "activates":[{"operation":"Unlock","switch":"fa1f22e776e4724582eb1dbb18ae864a9303cc5a"}],"position":{"x":-117.2,"y":-13.0,"z":22.7}},
            {"id":"fa1f22e776e4724582eb1dbb18ae864a9303cc5a","name":"D-2 Door Switch","switchType":"Close","activatedBy":"9cad024bc31223f02f7296c3cb1834c5f6fa2fe2",
             "activates":[],"position":{"x":-117.4,"y":-17.0,"z":168.5}}
          ],
          "extracts":[
            {"id":"hermetic","name":"Bunker Hermetic Door","faction":"shared","position":{"x":-110.0,"y":0.0,"z":80.0},"switch":false,"switches":[]},
            {"id":"woods","name":"Exit to Woods","faction":"pmc","position":{"x":10.0,"y":0.0,"z":10.0},"switch":false,"switches":[],
             "transferItem":{"item":"675aaa003107dac10006332f","count":1}},
            {"id":"coop","name":"Scav Lands (Co-Op)","faction":"shared","position":{"x":20.0,"y":0.0,"z":20.0},"switch":false,"switches":[]}
          ]
        }
        """;

    private static async Task<IReadOnlyList<MapFeature>> ReadFeaturesAsync(
        string mapName,
        string payload,
        bool seedMinefieldMap = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"extract-switch-overrides-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO maps(id, name, normalized_name, pmc_raid_duration_seconds, scav_raid_duration_seconds, source_json)
                    VALUES ($mapId, $mapName, $mapName, 2400, 2100, $payload);
                    """ + (seedMinefieldMap
                        ? """
                          INSERT INTO items(
                              id, name, short_name, normalized_name, category_type, width, height, slots,
                              source_updated_utc, normalized_short_name)
                          VALUES (
                              '675aaa003107dac10006332f', 'Minefield map (Reserve)', 'Mines',
                              'minefield map reserve', 'Unknown', 1, 1, 1,
                              '2025-11-14T10:03:18+00:00', 'mines');
                          """
                        : string.Empty);
                command.Parameters.AddWithValue("$mapId", mapName + "-id");
                command.Parameters.AddWithValue("$mapName", mapName);
                command.Parameters.AddWithValue("$payload", payload);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }

            return await new SqliteMapFeatureCatalog(factory)
                .GetAsync(mapName, CancellationToken.None);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-shm");
            File.Delete(path + "-wal");
        }
    }
}
