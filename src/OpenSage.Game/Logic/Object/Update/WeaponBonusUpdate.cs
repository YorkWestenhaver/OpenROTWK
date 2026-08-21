// WeaponBonusUpdate - R13 port. GPL sibling: generals-community/GeneralsMD/Code/GameEngine/
// Include/GameLogic/Module/WeaponBonusUpdate.h,
// .../Source/GameLogic/Object/Update/WeaponBonusUpdate.cpp (also read, for doTempWeaponBonus's
// real semantics: .../Include/GameLogic/Module/TempWeaponBonusHelper.h,
// .../Source/GameLogic/Object/Helper/TempWeaponBonusHelper.cpp). Full derivation:
// bfme2-workbench/research/modules-r13/specs/WeaponBonusUpdateModuleData.md.
//
// GPL's update() (WeaponBonusUpdate.cpp:126-166): every tick (first tick has no initial delay -
// the ctor sets UPDATE_SLEEP_NONE, WeaponBonusUpdate.cpp:98), scan for live, allied, on/off-map-
// matching objects within BonusRange via the POSITION-based partition query (not the object-
// based one every other range-scan module in this repo uses) - which, unlike the object-based
// overload, does NOT exclude its own center, so self is unconditionally a distance-0 query
// result and receives the bonus too whenever it also passes the kindof gate (F-WBU-3). No
// PartitionFilterAcceptByKindOf is applied in the query itself (the GPL source comment says so
// directly: "I need to reach valid contents of invalid transports"); the required/forbidden
// kindof gate (isKindOfMulti - ALL RequiredAffectKindOf bits present, NO ForbiddenAffectKindOf
// bit present) is applied per-candidate instead, both to the candidate itself and, independently
// of the candidate's own outcome, to each of its direct Contain passengers (single-level, not
// recursive - GPL's iterateContained(..., reverse=FALSE)). Re-arms unconditionally every
// BonusDelay frames (WeaponBonusUpdate.cpp:165).
//
// F-WBU-0 (parser-type fix): the pre-port [ParseOnly] flyweight parsed RequiredAffectKindOf/
// ForbiddenAffectKindOf as a single ObjectKinds value (ParseEnum) where GPL's KindOfMaskType is a
// space-separated multi-value list, and BonusRange as a raw int where GPL's field is Real. Fixed
// here to ParseEnumBitArray<ObjectKinds>() and Fix64 respectively; BonusDuration/BonusDelay move
// to LogicFrameSpan (ms->frames, S5 wire boundary), matching every other *Duration/*Delay field
// in this engine.
//
// F-WBU-1 (per-source expiry approximation, filed not invented): GPL's doTempWeaponBonus
// delegates to a per-OBJECT TempWeaponBonusHelper with exactly one slot (single active
// WeaponBonusConditionType + exact-frame expiry) per TARGET, auto-instantiated on every Object -
// re-granting the same status just resets that one timer; granting a different status clears the
// old one first; the helper self-schedules its own exact-frame wake to clear on expiry,
// independent of whatever source module (or module cadence) granted it. This engine has no
// per-object auto-attached-helper concept and GameObject._weaponBonusTypes has no companion
// per-type expiry-frame array (unlike _disabledTypes/_disabledTypesFrames). This port instead
// tracks expiry on the WeaponBonusUpdate module INSTANCE itself (_grants: target -> expire
// frame, the same shape AttributeModifierAuraUpdate._grantedTargets already uses for its own
// per-source tracking) and sweeps it every Update(). This reproduces GPL's observable
// single-source behavior (apply, refresh-on-re-scan, expire after BonusDuration of no re-scan)
// but does NOT reproduce two GPL facts that would need a target-side primitive to model
// correctly:
//   - cross-source interaction: a second independent source (or WeaponBonusUpgrade/HordeUpdate,
//     both landed and both call AddWeaponBonusType/RemoveWeaponBonusType directly with no expiry
//     of their own) granting a DIFFERENT WeaponBonusType to the same target is never clobbered by
//     this module's sweep (which only ever removes the one type it itself grants) - arguably
//     saner than GPL's fragile single-global-slot design, but a divergence from literal parity;
//   - expiry timing granularity: GPL's per-target expiry fires at the exact frame
//     appliedFrame + BonusDuration; this port's sweep only runs on this module's own BonusDelay
//     cadence, so a bonus can persist up to BonusDelay-1 frames past its literal GPL expiry
//     frame before this module notices and clears it - a bounded, BonusDelay-scale drift, not an
//     unbounded one.
//
// F-WBU-2 (client-visual, unmodeled): TempWeaponBonusHelper also sets/clears a Drawable tint
// status (TINT_STATUS_FRENZY) alongside the weapon-bonus condition bit. [SimState] code has no
// Drawable seam; not modeled, same class of gap as EmpUpdate's F-EMP-5.
//
// Every mutable sim field appears in Xfer exactly once; tolerances are the field's conformance
// class at its declaration site.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class WeaponBonusUpdate : UpdateModule
{
    private readonly WeaponBonusUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Targets this module currently has an active bonus grant on, and the frame each
    /// grant expires (F-WBU-1: per-source tracking, not GPL's per-target single slot - see file
    /// header). Ascending grant order (a function of the ascending-ObjectId partition scan plus
    /// self-first, so deterministic given identical prior world state, F9 - our own order, not a
    /// translated one).</summary>
    private readonly List<GrantedBonus> _grants = new();

    public WeaponBonusUpdate(GameObject gameObject, ISimContext context, WeaponBonusUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // GPL ctor: setWakeFrame(getObject(), UPDATE_SLEEP_NONE) - first scan on the first live
        // tick, no initial delay (WeaponBonusUpdate.cpp:98).
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>Test/inspection view of the currently-tracked grants.</summary>
    internal IReadOnlyList<GrantedBonus> Grants => _grants;

    public override UpdateSleepTime Update()
    {
        var now = Context.CurrentFrame;

        // Step (a): expire sweep (F-WBU-1) - targets this module granted whose window lapsed.
        for (var i = _grants.Count - 1; i >= 0; i--)
        {
            if (_grants[i].ExpireFrame > now)
            {
                continue;
            }

            var target = Context.GameLogic.GetObjectById(_grants[i].Target);
            target?.RemoveWeaponBonusType(_data.BonusConditionType);
            _grants.RemoveAt(i);
        }

        // Step (b): scan + apply. Self first (F-WBU-3: GPL's position-based query always
        // includes self; the S3 seam's object-centered overload excludes it, so this is an
        // explicit, unconditional compensating check, not an authored toggle).
        ApplyIfQualifies(GameObject, now);

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.BonusRange))
        {
            if (candidate.IsDestroyed || candidate.IsEffectivelyDead)
            {
                continue;
            }
            if (GameObject.GetRelationship(candidate) != RelationshipType.Allies)
            {
                continue;
            }
            if (candidate.IsOffMap != GameObject.IsOffMap)
            {
                continue;
            }

            ApplyIfQualifies(candidate, now);

            // Contained-item pass (single-level, non-recursive - GPL's iterateContained(...,
            // reverse=FALSE)) - unconditional, independent of whether `candidate` itself
            // qualified above (the GPL source comment's "reach valid contents of invalid
            // transports").
            if (candidate.Contain != null)
            {
                foreach (var passenger in candidate.Contain.ContainedItems)
                {
                    ApplyIfQualifies(passenger, now);
                }
            }
        }

        // GPL: return UPDATE_SLEEP(data->m_bonusDelay) unconditionally (WeaponBonusUpdate.cpp:165).
        return UpdateSleepTime.Frames(_data.BonusDelay);
    }

    /// <summary>isKindOfMulti(RequiredAffectKindOf, ForbiddenAffectKindOf) (ALL required bits
    /// present, NO forbidden bit present) + grant/refresh (F-WBU-1's per-source approximation of
    /// GPL's doTempWeaponBonus/"same status just resets the timer").</summary>
    private void ApplyIfQualifies(GameObject candidate, LogicFrame now)
    {
        foreach (var required in _data.RequiredAffectKindOf.GetSetBits())
        {
            if (!candidate.Definition.KindOf.Get(required))
            {
                return;
            }
        }
        if (_data.ForbiddenAffectKindOf.Intersects(candidate.Definition.KindOf))
        {
            return;
        }

        candidate.AddWeaponBonusType(_data.BonusConditionType);

        var expireFrame = now + _data.BonusDuration;
        var index = _grants.FindIndex(g => g.Target == candidate.Id);
        if (index >= 0)
        {
            _grants[index] = new GrantedBonus(candidate.Id, expireFrame);
        }
        else
        {
            _grants.Add(new GrantedBonus(candidate.Id, expireFrame));
        }
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferList("Grants", _grants, XferGrant);
    }

    private static void XferGrant(IXfer xfer, ref GrantedBonus grant)
    {
        var target = grant.Target;
        var expire = grant.ExpireFrame;
        xfer.XferObjectId("Target", ref target);
        xfer.XferFrame("ExpireFrame", ref expire, Tolerance.Quantum);
        grant = new GrantedBonus(target, expire);
    }

    internal readonly record struct GrantedBonus(ObjectId Target, LogicFrame ExpireFrame);
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Temporarily triggers use of a specific WeaponBonus from GameData.ini.
/// </summary>
[AddedIn(SageGame.CncGeneralsZeroHour)]
[SimDataAudited]
public sealed class WeaponBonusUpdateModuleData : UpdateModuleData
{
    internal static WeaponBonusUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<WeaponBonusUpdateModuleData> FieldParseTable = new IniParseTable<WeaponBonusUpdateModuleData>
    {
        { "RequiredAffectKindOf", (parser, x) => x.RequiredAffectKindOf = parser.ParseEnumBitArray<ObjectKinds>() },
        { "ForbiddenAffectKindOf", (parser, x) => x.ForbiddenAffectKindOf = parser.ParseEnumBitArray<ObjectKinds>() },
        // ms in INI, ceil-quantized to logic frames at parse (S5 wire boundary).
        { "BonusDuration", (parser, x) => x.BonusDuration = parser.ParseDurationLogicFrames() },
        { "BonusDelay", (parser, x) => x.BonusDelay = parser.ParseDurationLogicFrames() },
        // Deterministic S3-query radius -> Fix64 (never float across the analyzer wall).
        { "BonusRange", (parser, x) => x.BonusRange = parser.ParseFix64() },
        { "BonusConditionType", (parser, x) => x.BonusConditionType = parser.ParseEnum<WeaponBonusType>() },
    };

    public BitArray<ObjectKinds> RequiredAffectKindOf { get; private set; } = new();
    public BitArray<ObjectKinds> ForbiddenAffectKindOf { get; private set; } = new();

    /// <summary>Frames a granted bonus lasts without a re-scan refresh (ms in INI, ceil-quantized
    /// at parse, S5).</summary>
    public LogicFrameSpan BonusDuration { get; private set; }

    /// <summary>Frames between scans (ms in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan BonusDelay { get; private set; }

    public Fix64 BonusRange { get; private set; }
    public WeaponBonusType BonusConditionType { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new WeaponBonusUpdate(gameObject, gameEngine.SimContext, this);
    }
}
