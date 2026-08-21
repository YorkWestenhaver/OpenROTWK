// SiegeEngineContain - R13 SPLIT port (modules-r13/specs/SiegeEngineContainModuleData.md).
// Ported subset (8 GPL-cited fields: Slots, DamagePercentToUnits, Allow*Inside, ExitDelay,
// NumberOfExitPaths, GoAggressiveOnExit) as a fresh [SimState] UpdateModule mirroring GPL
// OpenContain.cpp/TransportContain.cpp semantics - NOT by re-parenting onto the legacy,
// non-[SimState] OpenContainModule lineage, which HordeSiegeEngineContainModuleData (a
// [SimState] derived class) cannot afford to drag in (spec §3.1). Structural precedent:
// Contain/ProductionQueueHordeContain.cs.
//
// Held back (parse-and-hold only, no reader anywhere, per the spec's held-field rule):
// - the crew sub-model (ObjectStatusOfCrew/CrewFilter/CrewMax/InitialCrew/
//   TypeOneForWeaponSet/SpeedPercentPerCrew): zero string hits across generals-gpl and
//   generals-community for any of these keys - the model would have to be invented whole.
// - the BFME-only passenger fields (PassengerFilter/KillPassengersOnDeath/
//   EjectPassengersOnDeath/ObjectStatusOfContained/ShowPips/PassengerBonePrefix/
//   BoneSpecificConditionState): zero GPL parse-table citations, and already
//   parsed-but-unconsumed on the landed TransportContain sibling - this port matches that
//   precedent rather than filling the gap.
//
// Disclosed gaps (spec §6; not invented around):
// 1. PassengerFilter is held, so entry is less restrictive than retail - any
//    relationship-permitted unit fits a free slot.
// 2. The whole crew sub-model is absent - siege engines spawn crewless, at unmodified speed.
// 3. Exit is index-only, not spatial - NumberOfExitPaths picks and records a path index;
//    ISimContext has no bone lookup or live-object set-position seam (D-7), so no occupant
//    is placed at an ExitStart/ExitEnd bone.
// 4. KillPassengersOnDeath / EjectPassengersOnDeath are inert - the death path always takes
//    the GPL processDamageToContained + release route; any coincidence with those fields'
//    names is a coincidence of the data (every corpus block sets DamagePercentToUnits =
//    100%), not an implementation of the fields.
// 5. GPL's processDamageToContained percentDamage == 1.0f tail branch (OpenContain.cpp:1470)
//    is not ported - its body was not read and its semantics are unverified.
// 6. ObjectStatusOfCrew / ObjectStatusOfContained set no status bits on occupants - held.
// 7. Re-basing to UpdateModuleData flips HordeSiegeEngineContainModuleData.ModuleKinds from
//    Behavior to Update as a side effect (a correction of a latent inconsistency, but a
//    change to a class this file does not own - integrate lane should verify no
//    ModuleKinds.Behavior lookup regresses).
// 8. No retail save-format Load(StatePersister) walk - same posture as the landed
//    HordeSiegeEngineContain sibling.
//
// Integrate-lane fix (contract-test failure): the death path is dispatched from
// IDieModule.OnDie (GPL OpenContain::onDie, OpenContain.cpp:857-874), not polled from
// Update() via GameObject.IsEffectivelyDead. A container with no other Die module has zero
// entries in GameObject.OnDie's dieModules list, which auto-Destroy()s it before its own
// sleepy Update() ever gets a tick (GameLogic.DeleteDestroyed reaps a destroyed object
// ahead of any Update() call on the same or a later frame) - so the IsEffectivelyDead poll
// could never actually fire. Implementing IDieModule puts this module in that dispatch,
// matching the existing TunnelContain/ParachuteContain precedent.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.Logic;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class SiegeEngineContain : UpdateModule, IDieModule
{
    private const int NoExitPath = -1;
    private const int SingleExitPath = 0;

    private readonly SiegeEngineContainModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer, spec §3.4) ----
    private readonly List<ObjectId> _occupants = new();
    private readonly List<ObjectId> _exitQueue = new();
    private LogicFrame _nextExitAllowedAfter;
    private int _lastExitPathIndex = NoExitPath;
    private bool _deathDamageApplied;

    public SiegeEngineContain(GameObject gameObject, ISimContext context, SiegeEngineContainModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        SetWakeFrame(UpdateSleepTime.None);
    }

    // ---- public surface (read by tests and by any future crew-seating caller) ----

    public int TotalSlots => _data.Slots;

    public int OccupiedSlots => _occupants.Count;

    public bool IsFull => OccupiedSlots >= TotalSlots;

    public IReadOnlyList<ObjectId> OccupantIds => _occupants;

    public int LastExitPathIndex => _lastExitPathIndex;

    /// <summary>Test seam: the frame before which no queued occupant may leave.</summary>
    public LogicFrame NextExitAllowedAfter => _nextExitAllowedAfter;

    /// <summary>
    /// Seats <paramref name="unit"/> in the first free slot, subject to the capacity gate
    /// (a) and the relationship gate (b). False when rejected or already seated.
    /// </summary>
    public bool TryAddOccupant(GameObject unit)
    {
        if (unit == null || unit == GameObject || unit.IsDestroyed || unit.IsEffectivelyDead)
        {
            return false;
        }
        if (_occupants.Contains(unit.Id))
        {
            // Duplicate-add guard (spec §3.3a): without it a second add would seat a phantom
            // entry that a single-match remove could never reach - same rationale as
            // ProductionQueueHordeContain.cs:154-163.
            return false;
        }
        if (!IsValidContainerFor(unit))
        {
            return false;
        }
        if (OccupiedSlots + 1 > TotalSlots)
        {
            return false;
        }
        _occupants.Add(unit.Id);
        return true;
    }

    /// <summary>Enqueues one occupant for release. False when not currently seated.</summary>
    public bool RequestExit(ObjectId unitId)
    {
        if (!_occupants.Contains(unitId))
        {
            return false;
        }
        if (_exitQueue.Contains(unitId))
        {
            return false;
        }
        _exitQueue.Add(unitId);
        return true;
    }

    /// <summary>Enqueues every occupant, in seating order.</summary>
    public void ExitAll()
    {
        foreach (var occupant in _occupants)
        {
            if (!_exitQueue.Contains(occupant))
            {
                _exitQueue.Add(occupant);
            }
        }
    }

    // ---- per-frame ----

    /// <summary>
    /// GPL OpenContain::onDie (OpenContain.cpp:857-874): a container is itself a die-callback
    /// target that empties its slot list when it dies - the death handling runs from
    /// GameObject::onDie's die-module dispatch, not from a per-frame poll. Ported here as the same event
    /// dispatch rather than an <c>Update()</c>-side <c>GameObject.IsEffectivelyDead</c> check:
    /// when this container has no other Die module of its own (every corpus fixture and the
    /// R13 spec's minimal object shape), <c>GameObject.OnDie</c> auto-destroys the container
    /// the instant it registers zero die modules (GameObject.cs OnDie's
    /// "dieModules.Count == 0 -&gt; Destroy()" branch) - and a destroyed object is reaped
    /// (GameLogic.DeleteDestroyed) before its sleepy <c>Update()</c> ever gets a tick, so the
    /// death-damage-and-release path could never run. Implementing <see cref="IDieModule"/>
    /// registers this module in that dispatch, both firing the death handling synchronously
    /// and (as a side effect matching TunnelContain/ParachuteContain's precedent) suppressing
    /// the auto-destroy so any other Die module on the same object still gets its say.
    /// </summary>
    void IDieModule.OnDie(in DamageInfoInput damageInput)
    {
        if (_deathDamageApplied)
        {
            return;
        }
        _deathDamageApplied = true;
        ApplyDeathDamageToOccupants();
        ReleaseAllOccupantsImmediately();
        _exitQueue.Clear();
    }

    public override UpdateSleepTime Update()
    {
        ReapDeadOccupants();

        while (_exitQueue.Count > 0 && _nextExitAllowedAfter < Context.CurrentFrame)
        {
            var id = _exitQueue[0];
            _exitQueue.RemoveAt(0);
            ReleaseOccupant(id);
            if (_data.ExitDelay.Value > 0)
            {
                _nextExitAllowedAfter = Context.CurrentFrame + _data.ExitDelay;
            }
        }

        return UpdateSleepTime.None;
    }

    // ---- internals ----

    /// <summary>
    /// GPL OpenContain::isValidContainerFor (OpenContain.cpp:880-928): relationship is
    /// measured from the CANDIDATE to the container, not the reverse. The AllowInsideKindOf/
    /// ForbidInsideKindOf mask check that precedes it in GPL has no keys in this module's
    /// parse table and no corpus block sets them, so it is not translated; PassengerFilter -
    /// BFME's kind-gating field here - is held (spec §3.3b), so entry is deliberately less
    /// restrictive than retail (disclosed gap 1).
    /// </summary>
    private bool IsValidContainerFor(GameObject unit)
    {
        return unit.GetRelationship(GameObject) switch
        {
            RelationshipType.Allies => _data.AllowAlliesInside,
            RelationshipType.Enemies => _data.AllowEnemiesInside,
            _ => _data.AllowNeutralInside,
        };
    }

    /// <summary>
    /// Clears the occupant's slot, picks its exit path index (d), and applies
    /// GoAggressiveOnExit (e). Mirrors TransportContain.TryEvacUnit's per-unit release body.
    /// </summary>
    private void ReleaseOccupant(ObjectId unitId)
    {
        _occupants.Remove(unitId);
        _lastExitPathIndex = PickExitPathIndex();

        var unit = Context.GameLogic.GetObjectById(unitId);
        if (_data.GoAggressiveOnExit)
        {
            unit?.AIUpdate?.SetAttitude(AttitudeType.Aggressive);
        }
    }

    /// <summary>
    /// GPL TransportContain::TryAssignExitPath (TransportContain.cs:48-62), index selection
    /// only - see the file header for the spatial half that is NOT ported (disclosed gap 3).
    /// Draw discipline (conformance channel 5, spec §3.3d): exactly one draw per released
    /// occupant, and ONLY when NumberOfExitPaths &gt; 1. The 0 and 1 branches draw nothing -
    /// every shipping corpus block is 0 (spec §2), so live content draws zero random values,
    /// ever.
    /// </summary>
    private int PickExitPathIndex()
    {
        if (_data.NumberOfExitPaths <= 0)
        {
            return NoExitPath;
        }
        if (_data.NumberOfExitPaths == 1)
        {
            return SingleExitPath;
        }
        return Context.GameLogicRandom.Next(1, _data.NumberOfExitPaths);
    }

    /// <summary>
    /// Death path (spec §3.3f, per §0.3): the once-only damage ledger, then every occupant
    /// released immediately with no ExitDelay gating.
    /// </summary>
    private void ApplyDeathDamageToOccupants()
    {
        if (_data.DamagePercentToUnits == Fix64.Zero)
        {
            return;
        }
        foreach (var id in _occupants.ToArray()) // snapshot: the damage can kill occupants
        {
            var unit = Context.GameLogic.GetObjectById(id);
            if (unit?.BodyModule is not ActiveBody body)
            {
                continue;
            }
            unit.AttemptCombatDamage(new CombatDamageInput
            {
                SourceId = GameObject.Id, // GPL: the CONTAINER is the damager, not its killer
                DamageType = DamageType.Unresistable,
                DeathType = DeathType.Burned, // GPL m_isBurnedDeathToUnits default TRUE (OpenContain.cpp:80)
                Amount = body.DamageCore.MaxHealth * _data.DamagePercentToUnits,
            });
        }
    }

    private void ReleaseAllOccupantsImmediately()
    {
        while (_occupants.Count > 0)
        {
            ReleaseOccupant(_occupants[0]);
        }
    }

    /// <summary>Reaps occupants that died or were destroyed while seated (both lists), same
    /// as ProductionQueueHordeContain.ReapDeadMembers.</summary>
    private void ReapDeadOccupants()
    {
        for (var i = _occupants.Count - 1; i >= 0; i--)
        {
            var occupant = _occupants[i];
            var unit = Context.GameLogic.GetObjectById(occupant);
            if (unit == null || unit.IsDestroyed || unit.IsEffectivelyDead)
            {
                _occupants.RemoveAt(i);
                _exitQueue.Remove(occupant);
            }
        }
    }

    // ---- the single walk (F8 Objects channel; field order = declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferList("Occupants", _occupants, XferObjectIdEntry);
        xfer.XferList("ExitQueue", _exitQueue, XferObjectIdEntry);
        xfer.XferFrame("NextExitAllowedAfter", ref _nextExitAllowedAfter);
        xfer.XferInt("LastExitPathIndex", ref _lastExitPathIndex);
        xfer.XferBool("DeathDamageApplied", ref _deathDamageApplied);
    }

    private static void XferObjectIdEntry(IXfer xfer, ref ObjectId item) => xfer.XferObjectId("Id", ref item);
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public class SiegeEngineContainModuleData : UpdateModuleData
{
    internal static SiegeEngineContainModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    // Non-Concat, standalone (HordeSiegeEngineContain.cs:310 concats onto this exact table and
    // must keep compiling) - keep the name, accessibility and generic type unchanged.
    internal static readonly IniParseTable<SiegeEngineContainModuleData> FieldParseTable = new IniParseTable<SiegeEngineContainModuleData>
    {
       // held: no GPL text, no data-file comment, no written spec - the crew sub-model
       // (ObjectStatusOfCrew/CrewFilter/CrewMax/InitialCrew/TypeOneForWeaponSet/
       // SpeedPercentPerCrew) has zero string hits across generals-gpl and
       // generals-community. Parsed and stored only.
       { "ObjectStatusOfCrew", (parser, x) => x.ObjectStatusOfCrew = parser.ParseEnumBitArray<ObjectStatus>() },
       { "Slots", (parser, x) => x.Slots = parser.ParseInteger() },
       { "DamagePercentToUnits", (parser, x) => x.DamagePercentToUnits = parser.ParseFix64Percentage() },
       // held: BFME-only addition with no GPL parse-table entry; parsed-and-unconsumed on the
       // landed TransportContain sibling too. Parsed and stored only.
       { "PassengerFilter", (parser, x) => x.PassengerFilter = ObjectFilter.Parse(parser) },
       { "KillPassengersOnDeath", (parser, x) => x.KillPassengersOnDeath = parser.ParseBoolean() },
       { "AllowAlliesInside", (parser, x) => x.AllowAlliesInside = parser.ParseBoolean() },
       { "AllowEnemiesInside", (parser, x) => x.AllowEnemiesInside = parser.ParseBoolean() },
       { "AllowNeutralInside", (parser, x) => x.AllowNeutralInside = parser.ParseBoolean() },
       // held: crew sub-model, see ObjectStatusOfCrew above.
       { "CrewFilter", (parser, x) => x.CrewFilter = ObjectFilter.Parse(parser) },
       { "CrewMax", (parser, x) => x.CrewMax = parser.ParseInteger() },
       { "InitialCrew", (parser, x) => x.InitialCrew = Crew.Parse(parser) },
       { "ExitDelay", (parser, x) => x.ExitDelay = parser.ParseDurationLogicFrames() },
       { "NumberOfExitPaths", (parser, x) => x.NumberOfExitPaths = parser.ParseInteger() },
       { "GoAggressiveOnExit", (parser, x) => x.GoAggressiveOnExit = parser.ParseBoolean() },
       // held: crew sub-model, see ObjectStatusOfCrew above.
       { "TypeOneForWeaponSet", (parser, x) => x.TypeOneForWeaponSet = parser.ParseEnum<ObjectKinds>() },
       // held: BFME-only addition, see PassengerFilter above.
       { "EjectPassengersOnDeath", (parser, x) => x.EjectPassengersOnDeath = parser.ParseBoolean() },
       { "PassengerBonePrefix", (parser, x) => x.PassengerBonePrefixes.Add(PassengerBonePrefix.Parse(parser)) },
       { "BoneSpecificConditionState", (parser, x) => x.BoneSpecificConditionStates.Add(BoneSpecificConditionState.Parse(parser)) },
       { "ObjectStatusOfContained", (parser, x) => x.ObjectStatusOfContained = parser.ParseEnumBitArray<ObjectStatus>() },
       { "ShowPips", (parser, x) => x.ShowPips = parser.ParseBoolean() },
       // held: crew sub-model. SpeedPercentPerCrew moves Percentage -> Fix64 (ParseFix64Percentage)
       // purely so this class can carry [SimDataAudited] honestly - a representation change at
       // the parse boundary with no consumer, not an invented behavior.
       { "SpeedPercentPerCrew", (parser, x) => x.SpeedPercentPerCrew = parser.ParseFix64Percentage() }
    };

    // ---- held: crew sub-model (spec §1.2) ----
    public BitArray<ObjectStatus> ObjectStatusOfCrew { get; private set; }
    public ObjectFilter CrewFilter { get; private set; }
    public int CrewMax { get; private set; }
    public Crew InitialCrew { get; private set; }
    public ObjectKinds TypeOneForWeaponSet { get; private set; }
    public Fix64 SpeedPercentPerCrew { get; private set; }

    // ---- held: BFME-only passenger fields (spec §1.3) ----
    public ObjectFilter PassengerFilter { get; private set; }
    public bool KillPassengersOnDeath { get; private set; }
    public bool EjectPassengersOnDeath { get; private set; }
    public List<PassengerBonePrefix> PassengerBonePrefixes { get; } = new List<PassengerBonePrefix>();
    public List<BoneSpecificConditionState> BoneSpecificConditionStates { get; } = new List<BoneSpecificConditionState>();
    public BitArray<ObjectStatus> ObjectStatusOfContained { get; private set; }
    public bool ShowPips { get; private set; }

    // ---- ported (spec §1.1) ----

    /// <summary>GPL TransportContain.cpp:60 m_slotCapacity = 0.</summary>
    public int Slots { get; private set; }

    /// <summary>GPL OpenContain.cpp:79 m_damagePercentageToUnits = 0.</summary>
    public Fix64 DamagePercentToUnits { get; private set; } = Fix64.Zero;

    /// <summary>GPL TransportContain.cpp:71 m_exitDelay = 0; parsed as parseDurationUnsignedInt
    /// (ms), converted to whole logic frames at parse time (ceil(ms*fps/1000)).</summary>
    public LogicFrameSpan ExitDelay { get; private set; }

    /// <summary>GPL OpenContain.cpp:78 m_numberOfExitPaths = 1 - the current stub's C# 0
    /// default was a bug; every corpus block's own comment says "Defaults to 1".</summary>
    public int NumberOfExitPaths { get; private set; } = 1;

    /// <summary>GPL OpenContain.cpp:85-87 set all three TRUE - the current stub's C# false
    /// defaults were a bug: with them, an unset key meant "forbidden" instead of "allowed".</summary>
    public bool AllowAlliesInside { get; private set; } = true;
    public bool AllowEnemiesInside { get; private set; } = true;
    public bool AllowNeutralInside { get; private set; } = true;

    /// <summary>GPL TransportContain.cpp:64 m_goAggressiveOnExit = FALSE.</summary>
    public bool GoAggressiveOnExit { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SiegeEngineContain(gameObject, gameEngine.SimContext, this);
    }
}

public struct Crew
{
    internal static Crew Parse(IniParser parser)
    {
        return new Crew()
        {
            CrewObject = parser.ParseAssetReference(),
            NumMembers = parser.ParseInteger()
        };
    }

    public string CrewObject { get; private set; }
    public int NumMembers { get; private set; }
}

public struct BoneSpecificConditionState
{
    internal static BoneSpecificConditionState Parse(IniParser parser)
    {
        return new BoneSpecificConditionState()
        {
            ID = parser.ParseInteger(),
            Condition = parser.ParseEnum<ModelConditionFlag>()
        };
    }

    public int ID { get; private set; }
    public ModelConditionFlag Condition { get; private set; }
}
