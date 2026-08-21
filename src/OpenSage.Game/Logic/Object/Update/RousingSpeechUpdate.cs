// RousingSpeechUpdate - R13 port (research/modules-r13/specs/RousingSpeechUpdateModuleData.md).
// BFME-only (no generals-gpl sibling - grep confirms zero hits); data-derivable from two live
// retail INI instances (Theoden's cinematicobjects.ini block, plus a corroborating
// commented-out bard.ini block), no Ghidra/game.dat material read or cited anywhere in this
// file (spec §0).
//
// MECHANISM (spec §0.1): a special-power-gated timed aura, reusing
// AttributeModifierAuraUpdate's own scan/grant/revoke shape (R12, also BFME-only/field-name-
// derived) verbatim for the periodic QueryObjectsInRadius -> ObjectFilter/RequiredConditions
// gate -> AddAttributeModifier/RemoveAttributeModifier mechanics. What differs from that
// exemplar is what starts and stops the scan: here a SpecialPowerTemplate trigger
// (InitiateIntentToDoSpecialPower, the identical gate idiom already landed by
// ToggleHiddenSpecialAbilityUpdate/ReplaceObjectUpdate) plus a fixed SpeechDuration window,
// instead of an upgrade trigger with indefinite duration. Driven input (no landed special-
// power/command system calls InitiateIntentToDoSpecialPower yet), same posture as every other
// module in this family.
//
// Ownership/targeting gate (spec §1.2, reasoned not GPL-cited): this class has no TargetEnemy
// field (unlike AttributeModifierAuraUpdate), so it follows that exemplar's own non-TargetEnemy
// branch unconditionally - same-owner-or-allied-owner only, never enemies. No AllowSelf field
// either: the speech-giver is only ever its own target if it independently passes
// ObjectFilter/RequiredConditions, via the plain partition-query loop's natural self-exclusion
// (no special-cased self-inclusion path is added - spec §5).
//
// RequiredConditions - Reading A shipped (spec §1.2/§5): gates CANDIDATES, exact field-name/
// semantics precedent from AttributeModifierAuraUpdateModuleData.RequiredConditions. Reading B
// (gates the speech-giver itself, suggested by the commented-out, non-live bard.ini MOUNTED
// instance) has no field-name precedent anywhere in the landed codebase and is not acted on;
// test case RequiredConditions_GatesCandidatesNotSource_ReadingA below pins this down so a
// future silent flip cannot happen.
//
// PARSED, NOT MODELED (audited gaps, not invented - spec §1.3/§1.5):
//   - ApproachRequiresLos: no LOS/visibility query exists anywhere on ISimContext (grep
//     confirms zero hits), same "no landed capability to gate against" posture as
//     ToggleHiddenSpecialAbilityUpdate's own header note.
//   - CreateWave / WaveWidth: no landed capability renders a ground-wave mesh/decal
//     (ISimContext is deliberately, permanently UI/rendering-absent, S8); read as a
//     client-side visual flourish with no ISimEvents member shaped to request one.
//
// LeaderFX / FollowerFX ARE modeled (spec §1.4), via the landed ISimEvents.FireFXAtObject
// seam: LeaderFX fires once at GameObject on a successful InitiateIntentToDoSpecialPower;
// FollowerFX fires once at each target on the tick it is NEWLY granted the modifier (not on
// every subsequent scan while it stays eligible). Both are output-only events (S8) - no
// CRC/Xfer implication.
//
// Every mutable sim field appears in Xfer exactly once; tolerances are the field's conformance
// class at its declaration site (spec §2's Xfer table).

using System.Collections.Generic;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class RousingSpeechUpdate : UpdateModule
{
    private readonly RousingSpeechUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Whether the speech is currently in progress (scanning/granting).</summary>
    private bool _active;

    /// <summary>The frame at which the speech ends and every granted target is revoked.</summary>
    private LogicFrame _speechEndFrame;

    /// <summary>
    /// The next frame at which a scan pass is due. Tracked explicitly (rather than derived from
    /// UpdateSleepTime.Frames, the way AttributeModifierAuraUpdate's own RefreshDelay does it):
    /// this module must ALSO tick every single frame to catch the SpeechDuration deadline
    /// precisely, so the two cadences (scan interval vs. deadline check) are decoupled and the
    /// scan cadence needs its own tracked field (spec §2).
    /// </summary>
    private LogicFrame _nextScanFrame;

    /// <summary>ObjectIds currently holding this speech's ModifierName modifier, in the order
    /// they were granted (a function of the ascending-ObjectId partition scan, so deterministic
    /// across peers given identical prior events - F9, our own order, never sorted post hoc;
    /// identical shape to AttributeModifierAuraUpdate.cs's own _grantedTargets).</summary>
    private readonly List<ObjectId> _grantedTargets = new();

    public RousingSpeechUpdate(GameObject gameObject, ISimContext context, RousingSpeechUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // Idle until triggered (matching AttributeModifierAuraUpdate's own StartsActive-false
        // posture and ModelConditionSpecialAbilityUpdate's Packed-idle posture - spec §2).
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    /// <summary>Test/inspection view of the activation flag.</summary>
    internal bool IsActive => _active;

    /// <summary>Test/inspection view of the currently-granted target set.</summary>
    internal IReadOnlyList<ObjectId> GrantedTargets => _grantedTargets;

    /// <summary>Parsed and held; not currently modeled - see the file-header LOS-absent note
    /// (spec §1.3).</summary>
    public bool ApproachRequiresLos => _data.ApproachRequiresLos;

    /// <summary>Parsed and held; not currently modeled - see the file-header wave-mesh note
    /// (spec §1.5).</summary>
    public bool CreateWave => _data.CreateWave;

    /// <summary>Parsed and held; not currently modeled - see <see cref="CreateWave"/> (spec §1.5).</summary>
    public int WaveWidth => _data.WaveWidth;

    /// <summary>
    /// Starts the speech: only this module's own SpecialPowerTemplate may fire it, only while
    /// not already Active (no re-triggering an in-flight speech), and (reusing
    /// ToggleHiddenSpecialAbilityUpdate's identical StartAbilityRange-gate idiom verbatim) only
    /// when StartAbilityRange is configured (&gt; 0) and <paramref name="triggeringObject"/> is
    /// within that range of GameObject - the gate is skipped when StartAbilityRange is
    /// unconfigured or triggeringObject is null (spec §1.1). On success the speech becomes
    /// Active for SpeechDuration frames and LeaderFX fires once at GameObject.
    /// </summary>
    public bool InitiateIntentToDoSpecialPower(string specialPowerTemplateName, GameObject triggeringObject)
    {
        if (_data.SpecialPowerTemplate != specialPowerTemplateName)
        {
            return false;
        }

        if (_active)
        {
            return false;
        }

        if (_data.StartAbilityRange > Fix64.Zero && triggeringObject != null)
        {
            var inRange = Context.Partition
                .QueryObjectsInRadius(GameObject, _data.StartAbilityRange)
                .Contains(triggeringObject);

            if (!inRange)
            {
                return false;
            }
        }

        _active = true;
        _speechEndFrame = Context.CurrentFrame + _data.SpeechDuration;
        // Due immediately: the first real Update() tick (one logic-frame-span later, per the
        // sleepy-update caveat) performs the first scan.
        _nextScanFrame = Context.CurrentFrame;
        SetWakeFrame(UpdateSleepTime.None);

        if (!string.IsNullOrEmpty(_data.LeaderFX))
        {
            Context.Events.FireFXAtObject(_data.LeaderFX, GameObject.Id);
        }

        return true;
    }

    public override UpdateSleepTime Update()
    {
        if (!_active)
        {
            return UpdateSleepTime.Forever;
        }

        var now = Context.CurrentFrame;

        if (now >= _speechEndFrame)
        {
            RevokeAll();
            _active = false;
            return UpdateSleepTime.Forever;
        }

        if (now >= _nextScanFrame)
        {
            RefreshTargets();

            var interval = _data.UpdateInterval.Value > 0 ? _data.UpdateInterval : new LogicFrameSpan(1);
            _nextScanFrame = now + interval;
        }

        return UpdateSleepTime.None;
    }

    /// <summary>
    /// One scan/apply/revoke pass over the AttributeModifierAuraUpdate.RefreshTargets shape
    /// (spec §0.1/§1.2): ascending-ObjectId order via Context.Partition.QueryObjectsInRadius,
    /// gated per-candidate by liveness, same-owner-or-allied ownership, ObjectFilter, and
    /// RequiredConditions (Reading A - gates candidates). Newly-eligible candidates are granted
    /// ModifierName and fire FollowerFX once; previously-granted targets that fell out of
    /// eligibility are revoked.
    /// </summary>
    private void RefreshTargets()
    {
        var modifier = _data.ModifierName?.Value;
        if (modifier == null || _data.BonusRadius <= Fix64.Zero)
        {
            return;
        }

        var owner = GameObject.Owner;

        var eligible = new List<ObjectId>();
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.BonusRadius))
        {
            if (IsEligible(candidate, owner))
            {
                eligible.Add(candidate.Id);
            }
        }

        // Revoke from targets that fell out of eligibility (moved away, died, lost the
        // required condition, ...) before granting to new ones, matching
        // AttributeModifierAuraUpdate.RefreshTargets' own ordering.
        for (var i = _grantedTargets.Count - 1; i >= 0; i--)
        {
            var id = _grantedTargets[i];
            if (eligible.Contains(id))
            {
                continue;
            }

            Revoke(id, modifier);
            _grantedTargets.RemoveAt(i);
        }

        foreach (var id in eligible)
        {
            if (_grantedTargets.Contains(id))
            {
                continue;
            }

            var target = Context.GameLogic.GetObjectById(id);
            if (target == null)
            {
                continue;
            }

            target.AddAttributeModifier(modifier.Name, new Logic.AttributeModifier(modifier));
            _grantedTargets.Add(id);

            // Fires once, on the tick a target is NEWLY granted - not on every subsequent scan
            // while it remains eligible (spec §1.4).
            if (!string.IsNullOrEmpty(_data.FollowerFX))
            {
                Context.Events.FireFXAtObject(_data.FollowerFX, id);
            }
        }
    }

    private void RevokeAll()
    {
        var modifier = _data.ModifierName?.Value;
        if (modifier == null)
        {
            _grantedTargets.Clear();
            return;
        }

        foreach (var id in _grantedTargets)
        {
            Revoke(id, modifier);
        }
        _grantedTargets.Clear();
    }

    private void Revoke(ObjectId id, ModifierList modifier)
    {
        var target = Context.GameLogic.GetObjectById(id);
        if (target != null && target.HasAttributeModifier(modifier.Name))
        {
            target.RemoveAttributeModifier(modifier.Name);
        }
    }

    /// <summary>
    /// The per-candidate gate: liveness, same-owner-or-allied ownership (no TargetEnemy field
    /// on this class, so the AttributeModifierAuraUpdate non-TargetEnemy branch is followed
    /// unconditionally), ObjectFilter, and RequiredConditions (every set bit must be present on
    /// the candidate's own model conditions - Reading A, spec §1.2/§5).
    /// </summary>
    private bool IsEligible(GameObject candidate, Player owner)
    {
        if (candidate.IsDestroyed || candidate.IsEffectivelyDead || candidate.IsOffMap)
        {
            return false;
        }

        if (owner == null || candidate.Owner == null)
        {
            return false;
        }

        if (!ReferenceEquals(owner, candidate.Owner) && !owner.Allies.Contains(candidate.Owner))
        {
            return false;
        }

        if (_data.ObjectFilter != null && !_data.ObjectFilter.Matches(candidate))
        {
            return false;
        }

        if (_data.RequiredConditions != null)
        {
            foreach (var flag in _data.RequiredConditions.GetSetBits())
            {
                if (!candidate.ModelConditionFlags.Get(flag))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----
    //
    // Tolerances: Active is a lifecycle fact (Exact/no tolerance). SpeechEndFrame and
    // NextScanFrame are timers, so Quantum (XferFrame's own default). GrantedTargets' per-item
    // helper is id semantics (no tolerance) - identical shape to AttributeModifierAuraUpdate's
    // own XferGrantedTarget.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Active", ref _active);
        xfer.XferFrame("SpeechEndFrame", ref _speechEndFrame);
        xfer.XferFrame("NextScanFrame", ref _nextScanFrame);
        xfer.XferList("GrantedTargets", _grantedTargets, XferGrantedTarget);
    }

    private static void XferGrantedTarget(IXfer xfer, ref ObjectId id)
    {
        xfer.XferObjectId("Target", ref id);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class RousingSpeechUpdateModuleData : UpdateModuleData
{
    internal static RousingSpeechUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<RousingSpeechUpdateModuleData> FieldParseTable = new IniParseTable<RousingSpeechUpdateModuleData>
    {
        { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseAssetReference() },
        { "RequiredConditions", (parser, x) => x.RequiredConditions = parser.ParseEnumBitArray<ModelConditionFlag>() },
        // Deterministic S3-partition-query radius -> Fix64 (never float/int across the
        // analyzer wall, F3), same field name/role as ToggleHiddenSpecialAbilityUpdate's own.
        { "StartAbilityRange", (parser, x) => x.StartAbilityRange = parser.ParseFix64() },
        // ms in INI, ceil-quantized to logic frames at parse (S5 wire boundary).
        { "UpdateInterval", (parser, x) => x.UpdateInterval = parser.ParseDurationLogicFrames() },
        { "ApproachRequiresLOS", (parser, x) => x.ApproachRequiresLos = parser.ParseBoolean() },
        // Deterministic S3-query radius (F3), same reasoning as every other *Radius field.
        { "BonusRadius", (parser, x) => x.BonusRadius = parser.ParseFix64() },
        { "SpeechDuration", (parser, x) => x.SpeechDuration = parser.ParseDurationLogicFrames() },
        { "LeaderFX", (parser, x) => x.LeaderFX = parser.ParseAssetReference() },
        { "FollowerFX", (parser, x) => x.FollowerFX = parser.ParseAssetReference() },
        { "CreateWave", (parser, x) => x.CreateWave = parser.ParseBoolean() },
        // Parsed, not modeled (§1.5): never used in a partition query, only (per its name) a
        // client-side wave-mesh width, so it stays plain int rather than Fix64.
        { "WaveWidth", (parser, x) => x.WaveWidth = parser.ParseInteger() },
        // Same field name, resolved via the asset-reference path already used elsewhere in this
        // codebase for the identical field name (PassiveAreaEffectBehavior, spec §0.1).
        { "ModifierName", (parser, x) => x.ModifierName = parser.ParseModifierListReference() },
        { "ObjectFilter", (parser, x) => x.ObjectFilter = ObjectFilter.Parse(parser) },
    };

    public string SpecialPowerTemplate { get; private set; }
    public BitArray<ModelConditionFlag> RequiredConditions { get; private set; }
    public Fix64 StartAbilityRange { get; private set; }
    public LogicFrameSpan UpdateInterval { get; private set; }

    /// <summary>Parsed and held; not currently modeled - no LOS/visibility query exists
    /// anywhere on ISimContext (spec §1.3).</summary>
    public bool ApproachRequiresLos { get; private set; }

    public Fix64 BonusRadius { get; private set; }
    public LogicFrameSpan SpeechDuration { get; private set; }
    public string LeaderFX { get; private set; }
    public string FollowerFX { get; private set; }

    /// <summary>Parsed and held; not currently modeled - no ground-wave-mesh rendering
    /// capability exists on ISimContext (spec §1.5).</summary>
    public bool CreateWave { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see <see cref="CreateWave"/> (spec §1.5).</summary>
    public int WaveWidth { get; private set; }

    public LazyAssetReference<ModifierList> ModifierName { get; private set; }
    public ObjectFilter ObjectFilter { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new RousingSpeechUpdate(gameObject, gameEngine.SimContext, this);
    }
}
