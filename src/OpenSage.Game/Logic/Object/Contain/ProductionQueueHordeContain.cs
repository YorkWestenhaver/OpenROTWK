// ProductionQueueHordeContain - the Round-12 runtime port. BFME-only module (no GPL sibling)
// and no dedicated clean-room spec doc: bfme2-workbench/research/spec-hordes.md only carries
// the one-line family table entry (own vtable at 0xc0bacc, "separate contain family: garrisons
// keep the horde grouping intact while contained") - no decompiled per-frame logic has been
// recovered for this module specifically (CLEAN-ROOM RULE: nothing below is transcribed from
// binary-derived; every behavior traces to the parsed field's own name/shape, the same
// generic Contain-family semantics every sibling module in this folder already documents
// (OpenContainModuleData's ContainMax/DamagePercentToUnits/Allow*Inside/PassengerFilter/
// EnterSound), or to the shared-contain header fields the task packet calls out
// (ObjectStatusOfContained/InitialPayload-shaped Slots).
//
// Shape: a structure (archery range, barracks, ...) that gates externally-produced horde
// units through ContainMax fixed slots before they walk onto the field. TryAddMember seats
// an already-live unit (PassengerFilter + Allow*Inside faction-stance gate), steers it toward
// EntryPosition+EntryOffset (rotated to the building's facing) through the member's own S2
// locomotor - the same "steer, don't teleport" shape SimHordeContain uses, since ISimContext
// has no set-position primitive for a live (non-donor) object (D-7). Release walks the member
// back out through one of NumberOfExitPaths, round-robin, offset by ExitOffset. Container
// damage propagates DamagePercentToUnits of the ACTUAL damage dealt to every seated member -
// the IDamageModule.OnDamage half lives in ProductionQueueHordeContainDamage.cs (D-7: DamageInfo
// is legacy float substrate, the same seam SimHordeMember's OnDamage rides, so that one crossing
// stays OUT of this [SimState] file - see that file's header).
//
// EnterSound (S8 finding): ISimEvents has no member that fires a raw named sound asset outside
// a UnitSpecificSounds key (FireUnitSoundAtObject) or an FXList (FireFXAtObject) - neither
// shape matches a bare EnterSound asset reference, and reservedNames is empty for this task
// packet (no seam growth authorized). EnterSound stays parsed data (matching every other real
// Contain module in this folder - OpenContainModuleData.EnterSound is parsed everywhere and
// consumed nowhere) with the cue surfaced as an observable counter for contract tests / a
// future audio-host port, rather than invented against a mismatched event shape.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.Logic;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed partial class ProductionQueueHordeContain : UpdateModule
{
    private readonly ProductionQueueHordeContainModuleData _data;

    private static readonly Fix64 MemberSpeedSentinel = Fix64.FromDecimalLiteral("99999");

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----
    private readonly List<ObjectId> _slots;
    private int _nextExitPathIndex;

    // ---- diagnostic-only (NOT sim state, NOT xfered - see EnterSound note above) ----
    public int EnterSoundFiredCount { get; private set; }

    public ProductionQueueHordeContain(GameObject gameObject, ISimContext context, ProductionQueueHordeContainModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        var slotCount = _data.ContainMax > 0 ? _data.ContainMax : 0;
        _slots = new List<ObjectId>(slotCount);
        for (var i = 0; i < slotCount; i++)
        {
            _slots.Add(ObjectId.Invalid);
        }
        SetWakeFrame(UpdateSleepTime.None);
    }

    // ---- public surface (read by tests / the production-queue caller) ----

    public int SlotCount => _slots.Count;

    public int MemberCount
    {
        get
        {
            var count = 0;
            foreach (var occupant in _slots)
            {
                if (occupant.IsValid)
                {
                    count++;
                }
            }
            return count;
        }
    }

    public bool IsFull => MemberCount >= SlotCount;

    public IEnumerable<ObjectId> MemberIds
    {
        get
        {
            foreach (var occupant in _slots)
            {
                if (occupant.IsValid)
                {
                    yield return occupant;
                }
            }
        }
    }

    public int SlotIndexOf(ObjectId memberId) => _slots.IndexOf(memberId);

    /// <summary>Index of the exit path the NEXT released member would use (round-robin).</summary>
    public int NextExitPathIndex => _slots.Count == 0 ? 0 : _nextExitPathIndex % ExitPathCount;

    private int ExitPathCount => _data.NumberOfExitPaths > 0 ? _data.NumberOfExitPaths : 1;

    /// <summary>EntryPosition + EntryOffset rotated to the building's current facing (spec: "member
    /// unit positioning within production queue slots relative to the building's entry point and
    /// orientation").</summary>
    public FixVector3 EntryWorldPosition()
    {
        var anchor = SimTransformBridge.PullPosition(GameObject);
        var yaw = SimTransformBridge.PullYaw(GameObject);
        var local = _data.EntryPosition + _data.EntryOffset;
        return RotateAndAdd(anchor, yaw, local);
    }

    /// <summary>ExitOffset rotated to the building's current facing - the world position a
    /// released member is steered toward, regardless of which of the NumberOfExitPaths round-robin
    /// slots it was assigned (a single ExitOffset vector is all the parsed data carries; the
    /// rotating index staggers exit ORDER/timing, not spatial destination).</summary>
    public FixVector3 ExitWorldPosition()
    {
        var anchor = SimTransformBridge.PullPosition(GameObject);
        var yaw = SimTransformBridge.PullYaw(GameObject);
        return RotateAndAdd(anchor, yaw, _data.ExitOffset);
    }

    /// <summary>
    /// Seats an already-live unit (production/garrison entry path) into the first vacant slot,
    /// subject to PassengerFilter and the Allow*Inside faction-stance gate. Steers the member
    /// toward EntryWorldPosition through its own S2 locomotor when it has one (D-7: ISimContext
    /// has no set-position primitive for a live object), sets the ObjectStatusOfContained bits,
    /// and counts the EnterSound cue. Returns false when rejected or full.
    /// </summary>
    public bool TryAddMember(GameObject member)
    {
        if (member == null || member == GameObject || member.IsDestroyed || member.IsEffectivelyDead)
        {
            return false;
        }
        if (!CanAccept(member))
        {
            return false;
        }
        var slotIndex = _slots.IndexOf(ObjectId.Invalid);
        if (slotIndex < 0)
        {
            return false;
        }
        _slots[slotIndex] = member.Id;
        ApplyContainedStatus(member, true);
        SteerToward(member, EntryWorldPosition());
        EnterSoundFiredCount++;
        return true;
    }

    /// <summary>Releases one seated member: clears its slot, clears the ObjectStatusOfContained
    /// bits, and steers it out through the next round-robin exit path. False when not seated.</summary>
    public bool TryRemoveMember(ObjectId memberId)
    {
        var slotIndex = _slots.IndexOf(memberId);
        if (slotIndex < 0)
        {
            return false;
        }
        _slots[slotIndex] = ObjectId.Invalid;
        ReleaseThroughExitPath(Context.GameLogic.GetObjectById(memberId));
        return true;
    }

    /// <summary>Releases every seated member (e.g. the queue structure is destroyed/sold).</summary>
    public void ReleaseAll()
    {
        for (var i = 0; i < _slots.Count; i++)
        {
            var occupant = _slots[i];
            if (!occupant.IsValid)
            {
                continue;
            }
            _slots[i] = ObjectId.Invalid;
            ReleaseThroughExitPath(Context.GameLogic.GetObjectById(occupant));
        }
    }

    // ---- per-frame ----

    public override UpdateSleepTime Update()
    {
        ReapDeadMembers();
        return UpdateSleepTime.None;
    }

    // ---- internals ----

    private bool CanAccept(GameObject member)
    {
        if (IsFull)
        {
            return false;
        }
        if (!_data.PassengerFilter.Matches(member))
        {
            return false;
        }
        // "candidate.GetRelationship(self)" resolves through the CANDIDATE's owner/team - the
        // established GameObject.GetRelationship(GameObject) shape (EMP-F2 precedent).
        return member.GetRelationship(GameObject) switch
        {
            RelationshipType.Allies => _data.AllowAlliesInside,
            RelationshipType.Enemies => _data.AllowEnemiesInside,
            _ => _data.AllowNeutralInside,
        };
    }

    private void ApplyContainedStatus(GameObject member, bool contained)
    {
        foreach (var bit in _data.ObjectStatusOfContained.GetSetBits())
        {
            member.SetObjectStatus(bit, contained);
        }
    }

    private void ReleaseThroughExitPath(GameObject member)
    {
        var pathIndex = _nextExitPathIndex % ExitPathCount;
        _nextExitPathIndex = (pathIndex + 1) % ExitPathCount;
        LastExitPathIndex = pathIndex;

        if (member == null)
        {
            return;
        }
        ApplyContainedStatus(member, false);
        SteerToward(member, ExitWorldPosition());
    }

    /// <summary>The exit path index the MOST RECENTLY released member was assigned (test seam).</summary>
    public int LastExitPathIndex { get; private set; } = -1;

    private static void SteerToward(GameObject member, in FixVector3 worldPosition)
    {
        var mover = member.FindBehavior<SimLocomotorUpdate>();
        mover?.SetTargetPosition(worldPosition, MemberSpeedSentinel);
    }

    private void ReapDeadMembers()
    {
        for (var i = 0; i < _slots.Count; i++)
        {
            var occupant = _slots[i];
            if (!occupant.IsValid)
            {
                continue;
            }
            var member = Context.GameLogic.GetObjectById(occupant);
            if (member == null || member.IsDestroyed || member.IsEffectivelyDead)
            {
                _slots[i] = ObjectId.Invalid;
            }
        }
    }

    private static FixVector3 RotateAndAdd(in FixVector3 anchor, Fix64 yaw, in FixVector3 local)
    {
        var cos = FixTrig.Cos(yaw);
        var sin = FixTrig.Sin(yaw);
        return new FixVector3(
            anchor.X + local.X * cos - local.Y * sin,
            anchor.Y + local.X * sin + local.Y * cos,
            anchor.Z + local.Z);
    }

    // ---- the single walk (F8 Objects channel; field order = declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferList("Slots", _slots, XferSlotOccupant);
        var nextExitPathIndex = _nextExitPathIndex;
        xfer.XferInt("NextExitPathIndex", ref nextExitPathIndex);
        _nextExitPathIndex = nextExitPathIndex;
    }

    private static void XferSlotOccupant(IXfer xfer, ref ObjectId occupant) =>
        xfer.XferObjectId("Occupant", ref occupant);
}

[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class ProductionQueueHordeContainModuleData : UpdateModuleData
{
    internal static ProductionQueueHordeContainModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<ProductionQueueHordeContainModuleData> FieldParseTable = new IniParseTable<ProductionQueueHordeContainModuleData>
    {
        { "ObjectStatusOfContained", (parser, x) => x.ObjectStatusOfContained = parser.ParseEnumBitArray<ObjectStatus>() },
        { "ContainMax", (parser, x) => x.ContainMax = parser.ParseInteger() },
        { "DamagePercentToUnits", (parser, x) => x.DamagePercentToUnits = parser.ParseFix64Percentage() },
        { "PassengerFilter", (parser, x) => x.PassengerFilter = ObjectFilter.Parse(parser) },
        { "AllowEnemiesInside", (parser, x) => x.AllowEnemiesInside = parser.ParseBoolean() },
        { "AllowNeutralInside", (parser, x) => x.AllowNeutralInside = parser.ParseBoolean() },
        { "AllowAlliesInside", (parser, x) => x.AllowAlliesInside = parser.ParseBoolean() },
        { "NumberOfExitPaths", (parser, x) => x.NumberOfExitPaths = parser.ParseInteger() },
        { "PassengerBonePrefix", (parser, x) => x.PassengerBonePrefix = PassengerBonePrefix.Parse(parser) },
        { "EntryPosition", (parser, x) => x.EntryPosition = parser.ParseFixVector3() },
        { "EntryOffset", (parser, x) => x.EntryOffset = parser.ParseFixVector3() },
        { "ExitOffset", (parser, x) => x.ExitOffset = parser.ParseFixVector3() },
        { "EnterSound", (parser, x) => x.EnterSound = parser.ParseAssetReference() }
    };

    public BitArray<ObjectStatus> ObjectStatusOfContained { get; private set; } = new();
    public int ContainMax { get; private set; }
    public Fix64 DamagePercentToUnits { get; private set; }
    public ObjectFilter PassengerFilter { get; private set; } = new();
    public bool AllowEnemiesInside { get; private set; }
    public bool AllowNeutralInside { get; private set; }
    public bool AllowAlliesInside { get; private set; }
    public int NumberOfExitPaths { get; private set; }
    public PassengerBonePrefix PassengerBonePrefix { get; private set; }
    public FixVector3 EntryPosition { get; private set; }
    public FixVector3 EntryOffset { get; private set; }
    public FixVector3 ExitOffset { get; private set; }
    public string EnterSound { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ProductionQueueHordeContain(gameObject, gameEngine.SimContext, this);
    }
}
