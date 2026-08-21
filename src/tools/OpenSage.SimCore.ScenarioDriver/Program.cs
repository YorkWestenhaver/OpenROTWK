// Harness scenario driver (api-freeze-v1 §6, build-order step 6 glue).
//
// Drives one scripted scenario end-to-end through the REAL SimCore pipeline:
// an injection schedule (bfme2-harness/injection-schedule/v1, produced by the harness's
// schedule generator from a replay) is fed through OrderIngest.SubmitScheduled - the same
// pipe remote peers use (§4.3: replays are the same pipe) - SimLoop ticks the frozen phase
// sequence, and SyncChecker.ComputeDeepCheckpoint streams every field record plus the
// checkpoint vector to a DeepCrcWriter dump (opensage-deepdump v2).
//
// The scripted scenario ("scripted-v1") stands in for the not-yet-ported module layer: a
// deterministic, integer-only toy sim whose state is order-SENSITIVE (every order mutates
// CRC'd state) so a Target-A self-diff over it is meaningful. It follows the SimCore rules
// by hand - Fix64 arithmetic, the logic RNG stream, no float, no unordered iteration - even
// though a tool assembly is outside the analyzer's full-mode scope.
//
// Exit codes: 0 ok, 2 usage/format error.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Rng;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

internal static class Program
{
    private static int Main(string[] args)
    {
        string? schedulePath = null;
        string? outPath = null;
        uint? untilFrame = null;
        uint checkpointInterval = 10;
        uint seed = 0xB00u;
        var scenarioName = "scripted-v1";
        string? mapPath = null;
        var iniPaths = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--schedule": schedulePath = args[++i]; break;
                case "--out": outPath = args[++i]; break;
                case "--until-frame": untilFrame = uint.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--checkpoint-interval": checkpointInterval = uint.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed": seed = ParseUInt(args[++i]); break;
                case "--scenario": scenarioName = args[++i]; break;
                case "--map": mapPath = args[++i]; break;
                case "--ini": iniPaths.Add(args[++i]); break;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    return 2;
            }
        }

        if (outPath is null || (schedulePath is null && mapPath is null))
        {
            Console.Error.WriteLine(
                "usage: scenariodriver --schedule <injection-schedule.json> --out <dump> " +
                "[--until-frame N] [--checkpoint-interval K] [--seed S] " +
                "[--scenario NAME] [--map <file.map> [--ini <file.ini>]...]");
            return 2;
        }

        List<InjectedOrder> orders;
        try
        {
            orders = schedulePath is null ? new List<InjectedOrder>() : LoadSchedule(schedulePath);
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidDataException)
        {
            Console.Error.WriteLine($"schedule error: {e.Message}");
            return 2;
        }

        var lastOrderFrame = 0u;
        foreach (var o in orders)
        {
            if (o.Frame > lastOrderFrame)
            {
                lastOrderFrame = o.Frame;
            }
        }
        var stopAfter = untilFrame ?? (mapPath is not null ? 300u : lastOrderFrame + 10);

        IDriverScenario scenario;
        switch (scenarioName)
        {
            case "scripted-v1":
                scenario = new ScriptedScenario(seed);
                break;
            case "autoheal-v1":
                scenario = new AutoHealScenario(seed);
                break;
            case "delayeddeath-v1":
                scenario = new DelayedDeathScenario(seed);
                break;
            case "die-batch-v1":
                scenario = new DieBatchScenario(seed);
                break;
            case "spcd-v1":
                scenario = new SpecialPowerCompletionDieScenario(seed);
                break;
            case "map-v1":
                if (mapPath is null)
                {
                    Console.Error.WriteLine("map-v1 requires --map <file.map>");
                    return 2;
                }
                try
                {
                    scenario = new MapScenario(seed, mapPath, iniPaths);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException)
                {
                    Console.Error.WriteLine($"map error: {e.Message}");
                    return 2;
                }
                break;
            default:
                Console.Error.WriteLine($"unknown scenario: {scenarioName}");
                return 2;
        }
        var loop = new SimLoop(scenario)
        {
            CrcCheckpointIntervalInFrames = SyncChecker.EffectiveInterval(checkpointInterval),
        };

        foreach (var o in orders)
        {
            loop.Orders.SubmitScheduled(o.Order, new LogicFrame(o.Frame), o.SubmissionIndex);
        }

        var mapScenario = scenario as MapScenario;
        using (var stream = new StreamWriter(outPath, append: false, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            scenario.AttachWriter(new DeepCrcWriter(stream, leaveOpen: true));
            while (loop.CurrentFrame.Value <= stopAfter && mapScenario is not { MapExitRequested: true })
            {
                loop.Advance();
            }
        }

        Console.WriteLine(
            $"{scenarioName}: frames=0..{stopAfter} orders={orders.Count} dispatched={scenario.Dispatched} " +
            $"checkpoints={scenario.Checkpoints} objects={scenario.ObjectCount} " +
            $"finalCombined={scenario.FinalCombined:X8} seed=0x{seed:X8} interval={loop.CrcCheckpointIntervalInFrames}");
        if (mapScenario is not null)
        {
            var exitFrame = mapScenario.MapExitFrame;
            Console.WriteLine(
                $"map-v1: MapExitFrame={(exitFrame is null ? "none" : exitFrame.Value.ToString(CultureInfo.InvariantCulture))} " +
                $"mapObjectsSpawned={mapScenario.MapObjectsSpawned} mapObjectsSkipped={mapScenario.MapObjectsSkipped}");
        }
        return 0;
    }

    private static uint ParseUInt(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(s, CultureInfo.InvariantCulture);

    // -----------------------------------------------------------------------
    // Schedule loading: bfme2-harness/injection-schedule/v1 -> SimOrders
    // -----------------------------------------------------------------------

    private readonly record struct InjectedOrder(uint Frame, int SubmissionIndex, SimOrder Order);

    private static List<InjectedOrder> LoadSchedule(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var schema = root.GetProperty("schema").GetString();
        if (schema != "bfme2-harness/injection-schedule/v1")
        {
            throw new InvalidDataException($"unexpected schedule schema {schema}");
        }

        var result = new List<InjectedOrder>();
        // Per-(frame, player) submission counters. The schedule is already sorted
        // (frame, player, seq) - harness schedule.py - which matches the engine's
        // (playerIndex, submissionIndex) dispatch order by construction.
        var counters = new Dictionary<(uint, int), int>();

        foreach (var frameGroup in root.GetProperty("frames").EnumerateArray())
        {
            var frame = frameGroup.GetProperty("frame").GetUInt32();
            foreach (var o in frameGroup.GetProperty("orders").EnumerateArray())
            {
                var player = o.GetProperty("player").GetInt32();
                var code = o.GetProperty("code").GetInt32();
                var order = new SimOrder((GameMessageType)code, player);
                foreach (var a in o.GetProperty("args").EnumerateArray())
                {
                    order.AddArgument(DecodeArg(a));
                }

                counters.TryGetValue((frame, player), out var sub);
                counters[(frame, player)] = sub + 1;
                result.Add(new InjectedOrder(frame, sub, order));
            }
        }
        return result;
    }

    private static SimOrderArg DecodeArg(JsonElement a)
    {
        var tag = a.GetProperty("tag").GetInt32();
        switch (tag)
        {
            case 0:
                return SimOrderArg.FromInteger(a.GetProperty("value").GetInt32());
            case 1:
                return SimOrderArg.FromWireFloat(WireBits(a));
            case 2:
                return SimOrderArg.FromBoolean(a.GetProperty("value").GetInt64() != 0);
            case 3:
                return SimOrderArg.FromObjectId(a.GetProperty("value").GetUInt32());
            case 4:
                return SimOrderArg.FromUnsigned(a.GetProperty("value").GetUInt32());
            case 6:
                {
                    var c = a.GetProperty("components");
                    return SimOrderArg.FromWirePosition(
                        WireBits(c[0]), WireBits(c[1]), WireBits(c[2]));
                }
            case 7:
                {
                    var c = a.GetProperty("components");
                    return SimOrderArg.FromScreenPosition(c[0].GetProperty("value").GetInt32(),
                                                          c[1].GetProperty("value").GetInt32());
                }
            case 8:
                {
                    var c = a.GetProperty("components");
                    return SimOrderArg.FromScreenRectangle(
                        c[0].GetProperty("value").GetInt32(), c[1].GetProperty("value").GetInt32(),
                        c[2].GetProperty("value").GetInt32(), c[3].GetProperty("value").GetInt32());
                }
            case 9:
            case 10:
                return SimOrderArg.FromUnsigned(a.GetProperty("value").GetUInt32());
            default:
                throw new InvalidDataException($"unhandled argument tag {tag}");
        }
    }

    private static uint WireBits(JsonElement e)
    {
        var s = e.GetProperty("wireBits").GetString()
                ?? throw new InvalidDataException("missing wireBits");
        if (!s.StartsWith("0x", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"wireBits not 0x-prefixed: {s}");
        }
        return uint.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}

// ---------------------------------------------------------------------------
// Driver scenario surface: ISimSystems plus the dump/summary plumbing Main uses.
// ---------------------------------------------------------------------------

internal interface IDriverScenario : ISimSystems
{
    void AttachWriter(DeepCrcWriter writer);
    int Dispatched { get; }
    int Checkpoints { get; }
    int ObjectCount { get; }
    uint FinalCombined { get; }
}

// ---------------------------------------------------------------------------
// The scripted scenario: deterministic, integer-only, order-sensitive.
// ---------------------------------------------------------------------------

internal sealed class ScriptedObject
{
    public uint Id;
    public FixVector3 Position;
    public FixVector3 Target;
    public bool Moving;
    public Fix64 Health;
    public LogicFrame NextWake;

    public void Xfer(IXfer xfer)
    {
        xfer.BeginModule(new XferModuleId(Id, 0, "ModuleTag_Scripted", "ScriptedMover"));
        xfer.XferFixVector3("Position", ref Position, Tolerance.Band);
        xfer.XferFixVector3("Target", ref Target, Tolerance.Band);
        xfer.XferBool("Moving", ref Moving);
        xfer.XferFix64("Health", ref Health, Tolerance.Quantum);
        xfer.XferFrame("NextWake", ref NextWake);
        xfer.EndModule();
    }
}

internal sealed class PlayerState
{
    public int Player;
    public uint OrdersReceived;
    public uint LastOrder;
    public bool Retaliation;
    public int OrderMode;
    public List<uint> Selection = new();

    public void Xfer(IXfer xfer)
    {
        xfer.BeginModule(new XferModuleId((uint)Player, 0, "Player", "PlayerState"));
        xfer.XferUInt("OrdersReceived", ref OrdersReceived);
        xfer.XferUInt("LastOrder", ref LastOrder);
        xfer.XferBool("Retaliation", ref Retaliation);
        xfer.XferInt("OrderMode", ref OrderMode);
        xfer.XferList("Selection", Selection, static (IXfer x, ref uint id) => x.XferUInt("Id", ref id));
        xfer.EndModule();
    }
}

internal sealed class ObjectsChannel : ICrcChannelSource
{
    // Ids only ever grow, so appends keep the list in ascending-ObjectId walk order.
    public readonly List<ScriptedObject> Objects = new();

    public CrcChannel Channel => CrcChannel.Objects;
    public bool IsActive => true;

    public void Xfer(IXfer xfer)
    {
        foreach (var obj in Objects)
        {
            obj.Xfer(xfer);
        }
    }

    public ScriptedObject? Find(uint id)
    {
        foreach (var obj in Objects)
        {
            if (obj.Id == id)
            {
                return obj;
            }
        }
        return null;
    }
}

internal sealed class PlayersChannel : ICrcChannelSource
{
    // SortedDictionary: ascending player number is the walk order (SIMCORE004-clean).
    public readonly SortedDictionary<int, PlayerState> Players = new();

    public CrcChannel Channel => CrcChannel.Players;
    public bool IsActive => true;

    public void Xfer(IXfer xfer)
    {
        foreach (var kv in Players)
        {
            kv.Value.Xfer(xfer);
        }
    }

    public PlayerState Get(int player)
    {
        if (!Players.TryGetValue(player, out var ps))
        {
            ps = new PlayerState { Player = player };
            Players.Add(player, ps);
        }
        return ps;
    }
}

internal sealed class ScriptedScenario : IDriverScenario
{
    private static readonly Fix64 Speed = Fix64.FromRaw(3L << 32);          // 3.0 units/frame
    private static readonly Fix64 InitialHealth = Fix64.FromRaw(100L << 32);

    private readonly LogicRandom _random;
    private readonly ObjectsChannel _objects = new();
    private readonly PlayersChannel _players = new();
    private readonly SyncChecker _checker;
    private DeepCrcWriter? _writer;
    private uint _nextObjectId = 9;

    public int Dispatched { get; private set; }
    public int Checkpoints { get; private set; }
    public int ObjectCount => _objects.Objects.Count;
    public uint FinalCombined { get; private set; }

    public ScriptedScenario(uint seed)
    {
        _random = LogicRandom.CreateForSimContext(seed);
        _checker = new SyncChecker(new ICrcChannelSource[]
        {
            _objects,
            new LogicRandomChannelSource(_random),
            _players,
        });

        for (var i = 0; i < 8; i++)
        {
            var id = (uint)(i + 1);
            _objects.Objects.Add(new ScriptedObject
            {
                Id = id,
                Position = new FixVector3(
                    Fix64.FromRaw((long)(id * 10) << 32),
                    Fix64.FromRaw((long)(id * 5) << 32),
                    Fix64.Zero),
                Target = default,
                Moving = false,
                Health = InitialHealth,
                NextWake = LogicFrame.Zero,
            });
        }
    }

    public void AttachWriter(DeepCrcWriter writer) => _writer = writer;

    public void IngestOrders(LogicFrame frame)
    {
        // Orders are pre-submitted through OrderIngest by the driver; a live engine drains
        // its connection here.
    }

    public void DispatchOrder(in ScheduledOrder scheduled)
    {
        Dispatched++;
        var order = scheduled.Order;
        var ps = _players.Get(order.PlayerIndex);
        ps.OrdersReceived++;
        ps.LastOrder = (uint)order.Type;

        switch (order.Type)
        {
            case GameMessageType.MSG_CREATE_SELECTED_GROUP:
            case GameMessageType.MSG_CREATE_SELECT_ALL_GROUP:
                {
                    ps.Selection.Clear();
                    foreach (var a in order.Arguments)
                    {
                        if (a.Kind == SimOrderArgKind.ObjectId && a.ObjectId != 0)
                        {
                            ps.Selection.Add(a.ObjectId);
                        }
                    }
                    if (ps.Selection.Count == 0 && _objects.Objects.Count > 0)
                    {
                        // Deterministic fallback: the lowest live id.
                        ps.Selection.Add(_objects.Objects[0].Id);
                    }
                    ps.Selection.Sort();
                    break;
                }
            case GameMessageType.MSG_AREA_SELECTION:
                {
                    ps.Selection.Clear();
                    foreach (var obj in _objects.Objects)
                    {
                        ps.Selection.Add(obj.Id);
                    }
                    break;
                }
            case GameMessageType.MSG_DESTROY_SELECTED_GROUP:
                ps.Selection.Clear();
                break;
            case GameMessageType.MSG_DO_MOVETO:
                {
                    if (TryFirstPosition(order, out var target))
                    {
                        foreach (var id in ps.Selection)
                        {
                            var obj = _objects.Find(id);
                            if (obj is not null)
                            {
                                obj.Target = target;
                                obj.Moving = true;
                            }
                        }
                    }
                    break;
                }
            case GameMessageType.MSG_DOZER_CONSTRUCT:
                {
                    var id = _nextObjectId++;
                    if (!TryFirstPosition(order, out var pos))
                    {
                        pos = new FixVector3(
                            Fix64.FromRaw((long)(id * 8) << 32),
                            Fix64.FromRaw((long)(id * 4) << 32),
                            Fix64.Zero);
                    }
                    _objects.Objects.Add(new ScriptedObject
                    {
                        Id = id,
                        Position = pos,
                        Target = pos,
                        Moving = false,
                        Health = InitialHealth,
                        NextWake = new LogicFrame(scheduled.Frame.Value + 1),
                    });
                    break;
                }
            case GameMessageType.MSG_ENABLE_RETALIATION_MODE:
                {
                    var toggled = !ps.Retaliation;
                    foreach (var a in order.Arguments)
                    {
                        if (a.Kind == SimOrderArgKind.Boolean)
                        {
                            toggled = a.Boolean;
                            break;
                        }
                    }
                    ps.Retaliation = toggled;
                    break;
                }
            case GameMessageType.MSG_CHANGE_ORDERMODE:
                {
                    foreach (var a in order.Arguments)
                    {
                        if (a.Kind == SimOrderArgKind.Integer)
                        {
                            ps.OrderMode = a.Integer;
                            break;
                        }
                        if (a.Kind == SimOrderArgKind.Unsigned)
                        {
                            ps.OrderMode = (int)a.Unsigned;
                            break;
                        }
                    }
                    break;
                }
        }
    }

    private static bool TryFirstPosition(SimOrder order, out FixVector3 position)
    {
        foreach (var a in order.Arguments)
        {
            if (a.Kind == SimOrderArgKind.Position)
            {
                position = a.Position;
                return true;
            }
        }
        position = default;
        return false;
    }

    public void ModuleUpdate(LogicFrame frame)
    {
        foreach (var obj in _objects.Objects)
        {
            if (frame.Value < obj.NextWake.Value)
            {
                continue;
            }

            // One RNG draw per awake object per frame: keeps the LogicRandom channel and
            // (later) draw-count accounting exercised, and couples the walk to dispatch
            // effects (a spawned object shifts every later draw).
            var step = _random.Next(-2, 2);

            if (obj.Moving)
            {
                obj.Position = new FixVector3(
                    StepToward(obj.Position.X, obj.Target.X),
                    StepToward(obj.Position.Y, obj.Target.Y),
                    StepToward(obj.Position.Z, obj.Target.Z));
                if (obj.Position.X == obj.Target.X
                    && obj.Position.Y == obj.Target.Y
                    && obj.Position.Z == obj.Target.Z)
                {
                    obj.Moving = false;
                }
            }

            obj.Health += Fix64.FromRaw((long)step << 20);
            obj.NextWake = new LogicFrame(frame.Value + 1);
        }
    }

    private static Fix64 StepToward(Fix64 current, Fix64 target)
    {
        var delta = target - current;
        if (delta > Speed)
        {
            return current + Speed;
        }
        if (delta < -Speed)
        {
            return current - Speed;
        }
        return target;
    }

    public void PartitionUpdate(LogicFrame frame)
    {
    }

    public void CrcCheckpoint(LogicFrame frame)
    {
        if (_writer is null)
        {
            throw new InvalidOperationException("no DeepCrcWriter attached");
        }
        var message = _checker.ComputeDeepCheckpoint(frame, _writer);
        _writer.CrcVector(frame.Value, message.Combined, message.ChannelCrcs);
        Checkpoints++;
        FinalCombined = message.Combined;
    }
}
