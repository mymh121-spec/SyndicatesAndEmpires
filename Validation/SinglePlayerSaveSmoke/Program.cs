using System.Text.Json;
using Game.Application.Session;
using Game.Application.Turn;
using Game.Application.World;
using Game.Domain.Military;
using Game.Domain.World;

var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
foreach (int seed in new[] { 42, 12345, -7 })
{
    var (world, map) = CreateWorld(seed);
    var clock = new RealtimeSimulationClock();
    var save = new SinglePlayerSave
    {
        size = MapSizePreset.Standard, resources = MapResourceAbundance.Standard,
        water = MapWaterLevel.Standard, seed = seed, neutralCastles = 8, wrap = true,
        configuration = "smoke", selection = new SinglePlayerInput()
    };
    string player = world.Units.First(unit => unit.OwnerFactionId == "player").Id;
    GridCoordinate target = map.Mines.First(mine =>
        world.CanIssueMove("player", player, mine.Coordinate, out _, out _)).Coordinate;
    var move = new SinglePlayerRecord
    {
        action = SinglePlayerAction.Move,
        input = new SinglePlayerInput { unitId = player, x = target.X, y = target.Y, hasCell = true }
    };
    save.records.Add(move);
    Apply(world, move);
    for (int i = 0; i < 2500; i++)
    {
        clock.SetSpeed(i < 1250 ? 1 : 4);
        // Exercise unequal frames, remainder accumulation and dropped time.
        var advance = clock.Advance(i % 3 == 0 ? 0.016 : i % 3 == 1 ? 0.23 : 1.61);
        int steps = advance.FixedStepCount * clock.SpeedMultiplier;
        save.AddAdvance(steps, advance.CompletedGameDays, clock.TotalGameHours);
        if (steps > 0) Apply(world, new SinglePlayerRecord
        {
            action = SinglePlayerAction.Advance, mapSteps = steps,
            completedDays = advance.CompletedGameDays, gameHours = clock.TotalGameHours
        });
    }
    save.clock = clock.CaptureState();
    save.turn = clock.CurrentDayNumber;
    save.Validate();
    string encoded = JsonSerializer.Serialize(save, jsonOptions);
    SinglePlayerSave decoded = JsonSerializer.Deserialize<SinglePlayerSave>(encoded, jsonOptions);
    decoded.Validate();
    var (restored, _) = CreateWorld(seed);
    foreach (SinglePlayerRecord record in decoded.records) Apply(restored, record);
    Check(JsonSerializer.Serialize(world) == JsonSerializer.Serialize(restored),
        "Map units, paths, captures, castles, supply, commanders and AI must round-trip");
    var restoredClock = new RealtimeSimulationClock();
    restoredClock.RestoreState(decoded.clock);
    for (int i = 0; i < 100; i++)
    {
        var a = clock.Advance(0.19);
        var b = restoredClock.Advance(0.19);
        Check(a.FixedStepCount == b.FixedStepCount && a.CompletedGameDays == b.CompletedGameDays,
            "Sub-step clock remainder must survive save/load");
        world.AdvanceFixedSteps(a.FixedStepCount * clock.SpeedMultiplier);
        restored.AdvanceFixedSteps(b.FixedStepCount * restoredClock.SpeedMultiplier);
    }
    Check(JsonSerializer.Serialize(world) == JsonSerializer.Serialize(restored), "Future must stay deterministic");
    Check(save.records.Count < 625, "Clock compression must bound replay size");
    Console.WriteLine($"PASS seed={seed}, day={save.turn}, records={save.records.Count}, bytes={encoded.Length}");
    decoded.version++;
    MustReject(decoded, "future schema");
    decoded.version--;
    decoded.records[0].action = (SinglePlayerAction)999;
    MustReject(decoded, "unknown action");
    decoded.records[0].action = SinglePlayerAction.Move;
    decoded.clock.totalGameHours = double.NaN;
    MustReject(decoded, "invalid time");
}

static (RealtimeMapGameplayService, GridMapLayout) CreateWorld(int seed)
{
    var map = new GridMapLayoutGenerator().Generate(80, 48, 160, seed,
        new GridCoordinate(4, 24),
        new[] { new GridCoordinate(44, 23), new GridCoordinate(30, 32), new GridCoordinate(57, 16) },
        true, neutralCastleCount: 8);
    var world = new RealtimeMapGameplayService(map, "player", new[] { "ai_1", "ai_2", "ai_3" });
    Check(world.TryCreateUnit("player", UnitArchetype.Swordsman, out var unit, out _), "create player");
    world.TryHireCommander("player", RealtimeMapGameplayService.ProtagonistCommanderId, unit.Id, out _);
    return (world, map);
}
static void Apply(RealtimeMapGameplayService world, SinglePlayerRecord record)
{
    if (record.action == SinglePlayerAction.Move)
        Check(world.TryIssueMove("player", record.input.unitId,
            new GridCoordinate(record.input.x, record.input.y), out _), "recorded movement");
    else
    {
        world.AdvanceFixedSteps(record.mapSteps);
        for (int i = 0; i < record.completedDays; i++) world.AdvanceEconomicDay(out _);
    }
}
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void MustReject(SinglePlayerSave save, string message)
{
    try { save.Validate(); }
    catch (ArgumentException) { return; }
    catch (InvalidOperationException) { return; }
    throw new Exception("Accepted " + message);
}
