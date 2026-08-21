// SlaughterHordeContain - R12 port from [ParseOnly].
//
// Behavioral reference: bfme2-workbench/research/spec-hordes.md §2 (module inventory table,
// the CitadelSlaughterHordeContain row @ 0xc0bae8) - that table entry ("structures/vehicles
// that hold hordes... separate contain family") is the ONLY clean-room fact recovered for
// this module; no per-field decompiled logic exists for the base SlaughterHordeContain class
// itself (BFME-only, no generals-gpl sibling). Like BloodthirstyUpdate and LargeGroupAudioUpdate
// before it, this is therefore the minimal faithful behavior the authored INI fields describe,
// translated (not invented) from the parse table:
//   - SlaughterHordeContainModuleData : UpgradeModuleData is the retail shape (confirmed by the
//     existing parse table, unchanged by this port): membership is gated by the module's own
//     upgrade mux (the LevelUpUpgrade/AutoHealBehavior UpgradeLogic idiom) - a pen that has not
//     been "unlocked" refuses every add.
//   - PassengerFilter + the three Allow*Inside faction flags gate individual candidates once the
//     mux is triggered.
//   - ContainMax/MaxHordeCapacity cap membership.
//   - ObjectStatusOfContained is applied on entry and cleared on release (BitArray<ObjectStatus>
//     GetSetBits over GameObject.SetObjectStatus, the same primitive GarrisonContain's data
//     side already carries).
//   - a contained member's death is detected by the per-frame reap (the SimHordeContain
//     ReapDeadMembers idiom: no third-party death-observer seam exists on ISimContext, S8) and
//     pays CashBackPercent of the member's build cost to the CONTAINER's owner ("feed a unit to
//     the pen for cash").
//
// TODO-spec (unverified, filed not invented):
//   - EnterSound is a raw sound-asset reference, not a UnitSpecificSounds key; ISimEvents has no
//     "play this named sound" request (S8: audio is deliberately absent from ISimContext except
//     the FX/particle/unit-sound-key events it does expose) - parsed, never fired.
//   - EntryPosition/ExitOffset (the pair the R12 task packet names) route the member through
//     SetTargetPosition against the container's own SimLocomotorUpdate anchor, the exact
//     SimHordeContain "HordeMover" idiom, when the container has one (vehicles - the spec row's
//     "structures/vehicles" wording). A stationary container (no SimLocomotorUpdate; the common
//     case for a structure) has no Fix64-valued anchor yet (D-7 boundary: transform is still
//     float substrate for non-locomotor objects) - entry/exit position routing is then a no-op,
//     but every other bookkeeping step (list membership, status flags, capacity, refund) still
//     runs. EntryOffset is parsed (audited vocabulary) but not acted on: only EntryPosition and
//     ExitOffset are named in the module's behavioral contract.
//   - ContainMax vs MaxHordeCapacity: no spec fact distinguishes their difference for this
//     module; both are authored as independent slot caps and the effective capacity is the
//     stricter of the two that is actually set (<= 0 = unset/unlimited).
//   - CashBackPercent's "unit value" is the member's ObjectDefinition.BuildCost, crossed to sim
//     money through the pre-existing CastleUnpackStamper.GetBuildCost helper (the established
//     float-substrate BuildCost crossing, D-7) rather than inventing a new one; feedback sound
//     suppressed the same way AutoHealBehavior/BloodthirstyUpdate suppress it (S8).

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Castle;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class SlaughterHordeContain : UpdateModule, IUpgradeableModule
{
    private static readonly Fix64 MemberSpeedSentinel = Fix64.FromDecimalLiteral("99999");

    private readonly SlaughterHordeContainModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----
    private readonly List<ObjectId> _members = new();

    // Build cost of each seated member, captured on entry and index-parallel to _members.
    // The refund cannot re-read it at reap time: a member that dies is deleted from the
    // object list at the end of its death frame, which can be before the container's own
    // Update() next runs, so by then GetObjectById(memberId) is already null.
    private readonly List<uint> _memberCosts = new();

    public SlaughterHordeContain(GameObject gameObject, ISimContext context, SlaughterHordeContainModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // No periodic candidate scan: entry is driven by the (not-yet-wired) collide/order
        // seam calling TryContain, same "interim public setter" posture as
        // SimHordeContain.SetMeleeAttacking. Only reaping needs a live wake, and only while
        // members are present.
        SetWakeFrame(UpdateSleepTime.Forever);

        // The mux may fire OnUpgradeTriggered synchronously from its ctor when StartsActive.
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    /// <summary>Whether the pen has been unlocked (the module's own upgrade mux).</summary>
    public bool IsActive => _upgradeLogic.Triggered;

    private void OnUpgradeTriggered()
    {
        // Nothing to schedule: TryContain reads IsActive live, and Update() only has reap
        // work once a member is actually seated.
    }

    // ---- public surface (read by tests / future collide-seam + AIUpdate callers) ----

    public int ContainedCount => _members.Count;

    public IEnumerable<ObjectId> ContainedMemberIds => _members;

    public bool IsContained(GameObject member) => member != null && _members.Contains(member.Id);

    /// <summary>
    /// The effective slot cap: the stricter of ContainMax/MaxHordeCapacity that is actually
    /// authored (&lt;= 0 = unset, TODO-spec above).
    /// </summary>
    public int EffectiveCapacity
    {
        get
        {
            var cap = int.MaxValue;
            if (_data.ContainMax > 0)
            {
                cap = _data.ContainMax;
            }
            if (_data.MaxHordeCapacity > 0 && _data.MaxHordeCapacity < cap)
            {
                cap = _data.MaxHordeCapacity;
            }
            return cap;
        }
    }

    /// <summary>
    /// The full entry gate: mux triggered, candidate alive and not already seated, capacity,
    /// PassengerFilter, and the faction barrier (Allow*Inside).
    /// </summary>
    public bool CanContain(GameObject member)
    {
        if (member == null || !_upgradeLogic.Triggered)
        {
            return false;
        }
        if (member.IsDestroyed || member.IsEffectivelyDead)
        {
            return false;
        }
        if (_members.Contains(member.Id))
        {
            return false;
        }
        if (_members.Count >= EffectiveCapacity)
        {
            return false;
        }
        if (_data.PassengerFilter != null && !_data.PassengerFilter.Matches(member))
        {
            return false;
        }
        return PassesFactionFilter(member);
    }

    private bool PassesFactionFilter(GameObject member)
    {
        var ownerPlayer = GameObject.Owner;
        var memberOwner = member.Owner;

        if (memberOwner == ownerPlayer)
        {
            // Your own player's units always feed the pen; the Allow*Inside flags gate other
            // players (no field on the base data covers this case - AllowOwnPlayerInsideOverride
            // only exists on CitadelSlaughterHordeContainModuleData, out of scope here).
            return true;
        }

        // Relationship (Player.Enemies/Allies), not player identity, decides the bucket - the
        // same live convention SabotagePowerPlantCrateCollide/AutoHealBehavior already use
        // rather than the dormant Team-relationship tables (see those files' own notes); a
        // recorded ENEMIES/ALLIES entry wins even when the other side happens to be the
        // sentinel neutral player (HeadlessSimGame's own test convention - PlayerManager's
        // NeutralPlayer doubles as the harness's generic "second player").
        if (ownerPlayer != null && ownerPlayer.Enemies.Contains(memberOwner))
        {
            return _data.AllowEnemiesInside;
        }
        if (ownerPlayer != null && ownerPlayer.Allies.Contains(memberOwner))
        {
            return _data.AllowAlliesInside;
        }

        // No recorded relationship: the neutral bucket (translate-conservatively rather than
        // silently admitting everyone, TODO-spec above).
        return _data.AllowNeutralInside;
    }

    /// <summary>
    /// Seats a member: gate, list membership, ObjectStatusOfContained applied, routed toward
    /// EntryPosition (TODO-spec above). EnterSound is parsed only (S8, TODO-spec above).
    /// </summary>
    public bool TryContain(GameObject member)
    {
        if (!CanContain(member))
        {
            return false;
        }

        _members.Add(member.Id);
        _memberCosts.Add(CastleUnpackStamper.GetBuildCost(member.Definition));
        SetContainedStatus(member, true);
        RouteMemberTo(member, _data.EntryPosition);

        SetWakeFrame(UpdateSleepTime.None);
        return true;
    }

    /// <summary>
    /// Releases a seated member: routed toward ExitOffset BEFORE the list removal (so the
    /// move order still names a member the caller can also see mid-transition), then
    /// ObjectStatusOfContained cleared and the member dropped from the list.
    /// </summary>
    public bool Release(GameObject member)
    {
        if (member == null || !_members.Contains(member.Id))
        {
            return false;
        }

        RouteMemberTo(member, _data.ExitOffset);
        SetContainedStatus(member, false);
        RemoveMemberAt(_members.IndexOf(member.Id));
        return true;
    }

    private void SetContainedStatus(GameObject member, bool value)
    {
        foreach (var status in _data.ObjectStatusOfContained.GetSetBits())
        {
            member.SetObjectStatus(status, value);
        }
    }

    private SimLocomotorUpdate ContainerMover => GameObject.FindBehavior<SimLocomotorUpdate>();

    /// <summary>
    /// Routes a member toward the container's anchor plus a horde-local offset, rotated by
    /// the container's facing - the exact SimHordeContain.SlotWorldPosition idiom. A no-op
    /// when either side lacks a Fix64 transform yet (TODO-spec above).
    /// </summary>
    private void RouteMemberTo(GameObject member, in FixVector3 localOffset)
    {
        var mover = ContainerMover;
        if (mover == null || !mover.TransformInitialized)
        {
            return;
        }
        var memberMover = member.FindBehavior<SimLocomotorUpdate>();
        if (memberMover == null)
        {
            return;
        }

        var anchor = mover.Physics.Position;
        var yaw = mover.Physics.Yaw;
        var cos = FixTrig.Cos(yaw);
        var sin = FixTrig.Sin(yaw);
        var target = new FixVector3(
            anchor.X + localOffset.X * cos - localOffset.Y * sin,
            anchor.Y + localOffset.X * sin + localOffset.Y * cos,
            anchor.Z + localOffset.Z);

        memberMover.SetTargetPosition(target, MemberSpeedSentinel);
    }

    // ---- per-frame: reap dead members and pay the cash-back refund ----

    public override UpdateSleepTime Update()
    {
        ReapDeadMembers();
        return _members.Count > 0 ? UpdateSleepTime.None : UpdateSleepTime.Forever;
    }

    private void ReapDeadMembers()
    {
        for (var i = _members.Count - 1; i >= 0; i--)
        {
            var memberId = _members[i];
            var member = Context.GameLogic.GetObjectById(memberId);
            if (member != null && !member.IsDestroyed && !member.IsEffectivelyDead)
            {
                continue;
            }

            var cost = i < _memberCosts.Count ? _memberCosts[i] : 0u;
            RemoveMemberAt(i);
            IssueCashBackRefund(cost);
        }
    }

    private void RemoveMemberAt(int index)
    {
        if (index < 0 || index >= _members.Count)
        {
            return;
        }

        _members.RemoveAt(index);
        if (index < _memberCosts.Count)
        {
            _memberCosts.RemoveAt(index);
        }
    }

    private void IssueCashBackRefund(uint cost)
    {
        if (GameObject.Owner == null)
        {
            return;
        }

        if (cost == 0)
        {
            return;
        }

        var refund = (uint)((Fix64)(long)cost * _data.CashBackPercent);
        if (refund > 0)
        {
            // Feedback suppressed: the deposit sting is client audio (S8) and the headless
            // host has no audio system, same posture as AutoHealBehavior/BloodthirstyUpdate.
            GameObject.Owner.BankAccount.Deposit(refund, playSound: false);
        }
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);
        xfer.XferList("Members", _members, XferMember);
        xfer.XferList("MemberCosts", _memberCosts, XferMemberCost);
    }

    private static void XferMember(IXfer xfer, ref ObjectId item) => xfer.XferObjectId("Id", ref item);

    private static void XferMemberCost(IXfer xfer, ref uint item) => xfer.XferUInt("Cost", ref item);
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public class SlaughterHordeContainModuleData : UpgradeModuleData
{
    internal static SlaughterHordeContainModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static new readonly IniParseTable<SlaughterHordeContainModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<SlaughterHordeContainModuleData>
        {
            { "PassengerFilter", (parser, x) => x.PassengerFilter = ObjectFilter.Parse(parser) },
            { "ObjectStatusOfContained", (parser, x) => x.ObjectStatusOfContained = parser.ParseEnumBitArray<ObjectStatus>() },
            { "CashBackPercent", (parser, x) => x.CashBackPercent = parser.ParseFix64Percentage() },
            { "ContainMax", (parser, x) => x.ContainMax = parser.ParseInteger() },
            { "MaxHordeCapacity", (parser, x) => x.MaxHordeCapacity = parser.ParseInteger() },
            { "AllowAlliesInside", (parser, x) => x.AllowAlliesInside = parser.ParseBoolean() },
            { "AllowEnemiesInside", (parser, x) => x.AllowEnemiesInside = parser.ParseBoolean() },
            { "AllowNeutralInside", (parser, x) => x.AllowNeutralInside = parser.ParseBoolean() },
            { "EnterSound", (parser, x) => x.EnterSound = parser.ParseAssetReference() },
            { "EntryOffset", (parser, x) => x.EntryOffset = parser.ParseFixVector3() },
            { "ExitOffset", (parser, x) => x.ExitOffset = parser.ParseFixVector3() },
            { "EntryPosition", (parser, x) => x.EntryPosition = parser.ParseFixVector3() },
        });

    public ObjectFilter PassengerFilter { get; private set; }
    public BitArray<ObjectStatus> ObjectStatusOfContained { get; private set; } = new();
    public Fix64 CashBackPercent { get; private set; }
    public int ContainMax { get; private set; }
    public int MaxHordeCapacity { get; private set; }
    public bool AllowAlliesInside { get; private set; }
    public bool AllowEnemiesInside { get; private set; }
    public bool AllowNeutralInside { get; private set; }
    public string EnterSound { get; private set; }

    /// <summary>Parsed (audited vocabulary); not acted on - only EntryPosition/ExitOffset are
    /// named in the module's behavioral contract (TODO-spec on the runtime class above).</summary>
    public FixVector3 EntryOffset { get; private set; }
    public FixVector3 ExitOffset { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public FixVector3 EntryPosition { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SlaughterHordeContain(gameObject, gameEngine.SimContext, this);
    }
}
