using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;

namespace OpenSage.Logic.Object;

// R13 port: HordeTransportContain is BFME2/AotR's ordinary "carry passengers, eject them out
// exit-door bones with a per-exit delay, damage/kill them if the carrier dies" transport
// container - structurally the same mechanism as GPL Zero Hour's TransportContain, reused on
// siege/naval/garrison objects (mumakil, ships, watchtowers). It is NOT the horde-formation
// module (that's HordeGarrisonContain / HordeContain) - despite the BFME2 name, it contains
// individual GameObjects exactly like GPL's TransportContain.
//
// Disclosed gap (see modules-r13/specs/HordeTransportContainModuleData.md §5): KillPassengersOnDeath
// is parsed and exposed (see the KillPassengersOnDeath property below) but has no wired
// kill-in-place branch in this packet - parent death always takes the base
// OpenContainModule eject-with-DamagePercentToUnits path. Filling this in is the sibling
// HordeTransportContainDamage packet's job (its OnDamage hook reads this module's state).
[AddedIn(SageGame.Bfme)]
public class HordeTransportContainModuleData : OpenContainModuleData
{
    internal static HordeTransportContainModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static new readonly IniParseTable<HordeTransportContainModuleData> FieldParseTable = OpenContainModuleData.FieldParseTable
        .Concat(new IniParseTable<HordeTransportContainModuleData>
        {
            { "ObjectStatusOfContained", (parser, x) => x.ObjectStatusOfContained = parser.ParseEnumBitArray<ObjectStatus>() },
            { "Slots", (parser, x) => x.Slots = parser.ParseInteger() },
            { "PassengerFilter", (parser, x) => x.PassengerFilter = ObjectFilter.Parse(parser) },
            { "ExitDelay", (parser, x) => x.ExitDelay = parser.ParseInteger() },
            { "NumberOfExitPaths", (parser, x) => x.NumberOfExitPaths = parser.ParseInteger() },
            { "ForceOrientationContainer", (parser, x) => x.ForceOrientationContainer = parser.ParseBoolean() },
            { "PassengerBonePrefix", (parser, x) => x.PassengerBonePrefix = PassengerBonePrefix.Parse(parser) },
            { "EjectPassengersOnDeath", (parser, x) => x.EjectPassengersOnDeath = parser.ParseBoolean() },
            { "AllowOwnPlayerInsideOverride", (parser, x) => x.AllowOwnPlayerInsideOverride = parser.ParseBoolean() },
            { "ShowPips", (parser, x) => x.ShouldDrawPips = parser.ParseBoolean() },
            { "FadeFilter", (parser, x) => x.FadeFilter = ObjectFilter.Parse(parser) },
            { "FadePassengerOnEnter", (parser, x) => x.FadePassengerOnEnter = parser.ParseBoolean() },
            { "EnterFadeTime", (parser, x) => x.EnterFadeTime = parser.ParseInteger() },
            { "FadePassengerOnExit", (parser, x) => x.FadePassengerOnExit = parser.ParseBoolean() },
            { "ExitFadeTime", (parser, x) => x.ExitFadeTime = parser.ParseInteger() },
            { "KillPassengersOnDeath", (parser, x) => x.KillPassengersOnDeath = parser.ParseBoolean() },
            { "InitialPayload", (parser, x) => x.InitialPayloads.Add(Payload.Parse(parser)) },
        });

    public BitArray<ObjectStatus> ObjectStatusOfContained { get; private set; }
    public int Slots { get; private set; }
    public ObjectFilter PassengerFilter { get; private set; }

    /// <summary>Delay between successive exits, in milliseconds.</summary>
    public int ExitDelay { get; private set; }

    /// <summary>
    /// Defaults to 1. Set 0 to not use ExitStart/ExitEnd, set higher than 1 to use
    /// ExitStart01-nn/ExitEnd01-nn. (R13 bug fix: the pre-port ParseOnly stub had no explicit
    /// default here, silently defaulting to C#'s int 0 - see TransportContainModuleData.NumberOfExitPaths,
    /// which is the precedent this default matches.)
    /// </summary>
    public int NumberOfExitPaths { get; private set; } = 1;

    public bool ForceOrientationContainer { get; private set; }
    public PassengerBonePrefix PassengerBonePrefix { get; private set; }
    public bool EjectPassengersOnDeath { get; private set; }
    public bool AllowOwnPlayerInsideOverride { get; private set; }
    public ObjectFilter FadeFilter { get; private set; }
    public bool FadePassengerOnEnter { get; private set; }
    public int EnterFadeTime { get; private set; }
    public bool FadePassengerOnExit { get; private set; }
    public int ExitFadeTime { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool KillPassengersOnDeath { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public List<Payload> InitialPayloads { get; } = new List<Payload>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new HordeTransportContain(gameObject, gameEngine, this);
    }
}

public sealed class HordeTransportContain : OpenContainModule
{
    public override int TotalSlots => _moduleData.Slots;

    /// <summary>Read-only surface for the sibling HordeTransportContainDamage module's OnDamage
    /// hook, which reads this at lethal-hit time (see the disclosed gap in this file's header
    /// comment and modules-r13/specs/HordeTransportContainModuleData.md §5).</summary>
    public bool KillPassengersOnDeath => _moduleData.KillPassengersOnDeath;

    private readonly HordeTransportContainModuleData _moduleData;

    private LogicFrame _nextEvacAllowedAfter;

    internal HordeTransportContain(GameObject gameObject, IGameEngine gameEngine, HordeTransportContainModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;

        foreach (var payload in moduleData.InitialPayloads)
        {
            for (var i = 0; i < payload.Count; i++)
            {
                var unit = gameEngine.GameLogic.CreateObject(payload.Object.Value, gameObject.Owner);
                Add(unit, true);
            }
        }
    }

    public override int SlotValueForUnit(GameObject unit)
    {
        return unit.Definition.TransportSlotCount;
    }

    protected override bool CanUnitEnter(GameObject unit)
    {
        return _moduleData.PassengerFilter == null || _moduleData.PassengerFilter.Matches(unit);
    }

    private protected override void UpdateModuleSpecific()
    {
        var isLoaded = ContainedObjectIds.Count > 0;
        GameObject.ModelConditionFlags.Set(ModelConditionFlag.Loaded, isLoaded);
    }

    protected override bool TryAssignExitPath(GameObject unit)
    {
        if (_moduleData.NumberOfExitPaths > 0)
        {
            var startBoneName = ExitBoneStartName;
            var endBoneName = ExitBoneEndName;

            if (_moduleData.NumberOfExitPaths > 1)
            {
                var pathToChoose = GameEngine.GameLogic.Random.Next(1, _moduleData.NumberOfExitPaths);
                startBoneName = $"{startBoneName}{pathToChoose:00}";
                endBoneName = $"{endBoneName}{pathToChoose:00}";
            }

            var (_, startBone) = GameObject.Drawable.FindBone(startBoneName);
            var (_, endBone) = GameObject.Drawable.FindBone(endBoneName);

            if (startBone == null || endBone == null)
            {
                return false;
            }

            var startPoint = GameObject.ToWorldspace(startBone.Transform);
            unit.UpdateTransform(startPoint.Translation, startPoint.Rotation);
            var exitPoint = GameObject.ToWorldspace(endBone.Transform);
            unit.AIUpdate.AddTargetPoint(exitPoint.Translation);
            return true;
        }

        // NumberOfExitPaths == 0 (the documented "don't use ExitStart/ExitEnd" case): unlike
        // TransportContainModuleData, this class has no ExitBone fallback field (no data-corpus
        // or GPL-class evidence one belongs here - see modules-r13/specs/HordeTransportContainModuleData.md
        // §5) so this degrades to "no exit path assigned" rather than inventing one.
        return false;
    }

    protected override bool TryEvacUnit(LogicFrame currentFrame, ObjectId unitId)
    {
        if (_nextEvacAllowedAfter < currentFrame)
        {
            RemoveUnit(unitId);
            if (_moduleData.ExitDelay > 0)
            {
                var exitDelayFrames = _moduleData.ExitDelay / 1000f * GameEngine.LogicFramesPerSecond;
                _nextEvacAllowedAfter = currentFrame + new LogicFrameSpan((uint)exitDelayFrames);
            }
            return true;
        }

        return false;
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        var unknownInt1 = 1;
        reader.PersistInt32(ref unknownInt1);
        if (unknownInt1 != 1)
        {
            throw new InvalidStateException();
        }

        reader.SkipUnknownBytes(1);

        reader.PersistLogicFrame(ref _nextEvacAllowedAfter);
    }
}
