// AttributeModifierAuraUpdate - R12 port. BFME-only (no generals-gpl sibling); no clean-room
// spec exists for the plain grant/revoke behavior, so the periodic scan below is fresh code
// against the frozen contract, matching the shape of the landed LargeGroupBonusUpdate (R11)
// and AttributeModifierUpgrade (R11) ports: every RefreshDelay, walk nearby objects through
// the S3 partition seam and grant/revoke the named ModifierList through the same
// GameObject.AddAttributeModifier/RemoveAttributeModifier registry those modules use (same
// headless caveat: the registration is the sim-visible output, the legacy Scene3D loop applies
// the modifier's field EFFECTS).
//
// The upgrade-trigger fields (StartsActive/TriggeredBy/RequiresAllTriggers/Permanent) are NOT
// routed through the shared UpgradeLogic/UpgradeLogicData mux (the SpyVisionUpdate precedent):
// this module's own retail schema reuses the token "ConflictsWith" for something else entirely
// (the modifier-list names this aura's grant conflicts with, not upgrade-template conflicts),
// which collides with UpgradeLogicData's own "ConflictsWith" key (upgrade-template conflicts)
// in the combined field table. A minimal, module-local trigger flag (below) avoids the
// collision instead of fighting it; this is also why the pre-port [ParseOnly] flyweight
// already carried its own local copy of Permanent rather than deriving from UpgradeModuleData
// (see its own former header note, preserved in spirit here).
//
// Stacking: this port grants exactly the flat, uncomposed modifier record (BonusName's
// ModifierList) through the plain name-keyed GameObject.AddAttributeModifier registry (no
// magnitude composition of any kind lives in this module). The AotR `.danetta` screen-blend
// identity from bfme2-workbench/research/aotr-patch-semantics.md (S1, RESOLVED 2026-08-17) is a
// clean-room behavioral characterization of AttributeModifierPoolUpdate's OWN fold over multiple
// simultaneous modifier RECORDS from arbitrary sources -- that module (AttributeModifierPoolUpdate,
// landed as its own runtime port - see AttributeModifierPoolUpdate.cs) is where composition
// belongs, not here. This aura port previously exposed a
// standalone ComposeAuraStrength utility "for" that spec; it was never called from this module's
// grant path (RefreshTargets below) and has been removed as dead, misleading code (R13 finding:
// tests validating an unused utility misrepresented this module's actual behavior as composed
// when two auras granting the same modifier name to one target simply do not stack -- the second
// grant no-ops against GameObject.AddAttributeModifier's existing-live-entry guard).
//
// TODO-spec (unverified/unmodeled retail behavior, filed not invented):
//   - RequiresAllTriggers: parsed and stored, but TriggeredBy here is a SINGLE upgrade
//     reference (unlike the array-shaped upgrade mux), so "requires all" has no second trigger
//     to require alongside - vestigial until a second TriggeredBy is found in retail data;
//   - AntiCategory/AntiFX (dispel-on-apply of an opposing category, with an FX cue): both are
//     client-presentation adjacent and unmodeled;
//   - MaxActiveRank (rank-gated aura strength scaling): no rank system is exposed through
//     ISimContext; unmodeled;
//   - AffectContainedOnly (restrict targeting to objects contained within the source): no
//     container query beyond S6 hordes is exposed through ISimContext; unmodeled;
//   - AllowPowerWhenAttacking: unmodeled, no special-power/attack-state seam consumed here;
//   - "upgrade removed" engine notification: no landed module (including the R11
//     AttributeModifierUpgrade/SpyVisionUpdate precedents) is called back when a triggering
//     upgrade is later stripped - the module-local trigger flag below only ever transitions
//     untriggered -> triggered on its own. OnTriggerRemoved below is the aura-local reaction
//     (Permanent=No revokes every currently granted target and parks; Permanent=Yes is a
//     no-op), exposed the same way SpyVisionUpdate exposes OnDisabledEdge/
//     SetDisabledUntilFrame (SVU-4 precedent): reachable and tested, but not yet wired to a
//     real engine-side "upgrade removed" trigger.
//
// Every mutable sim field appears in Xfer exactly once; tolerances are the field's conformance
// class at its declaration site.

using System.Collections.Generic;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class AttributeModifierAuraUpdate : UpdateModule, IUpgradeableModule
{
    private readonly AttributeModifierAuraUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Whether the upgrade trigger has fired (module-local mux, see file header for
    /// why this does not reuse the shared UpgradeLogic). Never resets once set, matching
    /// UpgradeLogic's own "untriggered -&gt; triggered" one-way contract.</summary>
    private bool _triggered;

    /// <summary>Whether the aura is currently scanning/granting: true once triggered, false
    /// again if OnTriggerRemoved fires on a Permanent=No aura.</summary>
    private bool _active;

    /// <summary>ObjectIds currently holding this aura's BonusName modifier, in the order they
    /// were granted (a function of the ascending-ObjectId partition scan, so deterministic
    /// across peers given identical prior events - F9, our own order, never sorted post hoc).</summary>
    private readonly List<ObjectId> _grantedTargets = new();

    public AttributeModifierAuraUpdate(GameObject gameObject, ISimContext context, AttributeModifierAuraUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        SetWakeFrame(UpdateSleepTime.Forever);
        if (_data.StartsActive)
        {
            Activate();
        }
    }

    /// <summary>Test/inspection view of the activation flag.</summary>
    internal bool IsActive => _active;

    /// <summary>Test/inspection view of the currently-granted target set.</summary>
    internal IReadOnlyList<ObjectId> GrantedTargets => _grantedTargets;

    public bool CanUpgrade(UpgradeSet existingUpgrades)
    {
        if (_triggered)
        {
            return false;
        }

        var trigger = _data.TriggeredBy?.Value;
        return trigger != null && existingUpgrades.Contains(trigger);
    }

    public void TryUpgrade(UpgradeSet completedUpgrades)
    {
        if (!CanUpgrade(completedUpgrades))
        {
            return;
        }

        Activate();
    }

    private void Activate()
    {
        _triggered = true;
        _active = true;
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>
    /// Aura-local reaction to the triggering upgrade going away (TODO-spec: no engine callback
    /// exists yet that calls this - see the file header). Permanent=Yes (GPL's "once granted,
    /// stays granted") is a no-op; Permanent=No revokes every currently-granted target and
    /// parks the module, matching the plain reading of "the modifier drops with the upgrade".
    /// </summary>
    internal void OnTriggerRemoved()
    {
        if (_data.Permanent)
        {
            return;
        }

        RevokeAll();
        _active = false;
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    public override UpdateSleepTime Update()
    {
        if (!_active)
        {
            return UpdateSleepTime.Forever;
        }

        RefreshTargets();

        return _data.RefreshDelay.Value > 0
            ? UpdateSleepTime.Frames(_data.RefreshDelay)
            : UpdateSleepTime.Forever;
    }

    /// <summary>
    /// One scan/apply/revoke pass: query the S3 partition seam within Range, apply BonusName to
    /// every still-eligible candidate that is not already granted and does not conflict, and
    /// revoke it from any previously-granted target that fell out of eligibility. Repeated calls
    /// with an unchanged world produce an unchanged granted set (the refresh-loop consistency
    /// contract).
    /// </summary>
    private void RefreshTargets()
    {
        var bonus = _data.BonusName?.Value;
        if (bonus == null || _data.Range <= Fix64.Zero)
        {
            return;
        }

        if (!_data.RunWhileDead && GameObject.IsEffectivelyDead)
        {
            RevokeAll();
            return;
        }

        var owner = GameObject.Owner;

        // Ascending-ObjectId order (the S3 seam's frozen contract), so this list's own order is
        // deterministic given the same world state. AllowSelf is checked separately first: the
        // S3 query's entry-based overload always excludes its own center (SimPartitionGrid's
        // "exclude" parameter), so a source granting itself the bonus is never in the query
        // results and has to be considered explicitly.
        var eligible = new List<ObjectId>();
        if (_data.AllowSelf && IsEligible(GameObject, owner))
        {
            eligible.Add(GameObject.Id);
        }
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.Range))
        {
            if (IsEligible(candidate, owner))
            {
                eligible.Add(candidate.Id);
            }
        }

        // Revoke from targets that fell out of eligibility (moved away, died, lost the
        // required condition, ...).
        for (var i = _grantedTargets.Count - 1; i >= 0; i--)
        {
            var id = _grantedTargets[i];
            if (eligible.Contains(id))
            {
                continue;
            }

            Revoke(id, bonus);
            _grantedTargets.RemoveAt(i);
        }

        // Grant to newly-eligible targets (already-granted targets are left untouched: a
        // conflicting modifier granted to a target AFTER our own grant does not retroactively
        // strip ours, matching ConflictsWith's plain reading as an apply-time gate).
        foreach (var id in eligible)
        {
            if (_grantedTargets.Contains(id))
            {
                continue;
            }

            var target = Context.GameLogic.GetObjectById(id);
            if (target == null || HasConflict(target))
            {
                continue;
            }

            target.AddAttributeModifier(bonus.Name, new Logic.AttributeModifier(bonus));
            _grantedTargets.Add(id);
        }
    }

    private void RevokeAll()
    {
        var bonus = _data.BonusName?.Value;
        if (bonus == null)
        {
            _grantedTargets.Clear();
            return;
        }

        foreach (var id in _grantedTargets)
        {
            Revoke(id, bonus);
        }
        _grantedTargets.Clear();
    }

    private void Revoke(ObjectId id, ModifierList bonus)
    {
        var target = Context.GameLogic.GetObjectById(id);
        if (target != null && target.HasAttributeModifier(bonus.Name))
        {
            target.RemoveAttributeModifier(bonus.Name);
        }
    }

    /// <summary>
    /// The per-candidate gate: liveness, TargetEnemy/AllowSelf relationship, ObjectFilter, and
    /// RequiredConditions (every set bit must be present on the candidate's model conditions).
    /// </summary>
    private bool IsEligible(GameObject candidate, Player owner)
    {
        if (candidate.IsDestroyed || candidate.IsEffectivelyDead || candidate.IsOffMap)
        {
            return false;
        }

        if (candidate == GameObject)
        {
            if (!_data.AllowSelf)
            {
                return false;
            }
        }
        else if (_data.TargetEnemy)
        {
            if (owner == null || candidate.Owner == null || !owner.Enemies.Contains(candidate.Owner))
            {
                return false;
            }
        }
        else
        {
            if (owner == null || candidate.Owner == null)
            {
                return false;
            }
            if (!ReferenceEquals(owner, candidate.Owner) && !owner.Allies.Contains(candidate.Owner))
            {
                return false;
            }
        }

        if (_data.Filter != null && !_data.Filter.Matches(candidate))
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

    private bool HasConflict(GameObject target)
    {
        var conflicts = _data.ConflictsWith;
        if (conflicts == null)
        {
            return false;
        }

        foreach (var name in conflicts)
        {
            if (target.HasAttributeModifier(name))
            {
                return true;
            }
        }

        return false;
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Triggered", ref _triggered);
        xfer.XferBool("Active", ref _active);
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
public sealed class AttributeModifierAuraUpdateModuleData : UpdateModuleData
{
    internal static AttributeModifierAuraUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<AttributeModifierAuraUpdateModuleData> FieldParseTable = new IniParseTable<AttributeModifierAuraUpdateModuleData>
    {
        { "StartsActive", (parser, x) => x.StartsActive = parser.ParseBoolean() },
        { "BonusName", (parser, x) => x.BonusName = parser.ParseModifierListReference() },
        { "TriggeredBy", (parser, x) => x.TriggeredBy = parser.ParseUpgradeReference() },
        // ms in INI, ceil-quantized to logic frames at parse (S5 wire boundary).
        { "RefreshDelay", (parser, x) => x.RefreshDelay = parser.ParseDurationLogicFrames() },
        // Deterministic S3-query radius -> Fix64 (never float across the analyzer wall).
        { "Range", (parser, x) => x.Range = parser.ParseFix64() },
        { "TargetEnemy", (parser, x) => x.TargetEnemy = parser.ParseBoolean() },
        { "ObjectFilter", (parser, x) => x.Filter = ObjectFilter.Parse(parser) },
        // The modifier-list names this aura's grant conflicts with (NOT an upgrade conflict -
        // see the file header on why this module does not share the UpgradeLogicData mux).
        { "ConflictsWith", (parser, x) => x.ConflictsWith = parser.ParseAssetReferenceArray() },
        { "RunWhileDead", (parser, x) => x.RunWhileDead = parser.ParseBoolean() },
        { "RequiredConditions", (parser, x) => x.RequiredConditions = parser.ParseEnumBitArray<ModelConditionFlag>() },
        { "AntiCategory", (parser, x) => x.AntiCategory = parser.ParseEnum<ModifierCategory>() },
        { "AntiFX", (parser, x) => x.AntiFX = parser.ParseAssetReference() },
        { "AllowSelf", (parser, x) => x.AllowSelf = parser.ParseBoolean() },
        { "AllowPowerWhenAttacking", (parser, x) => x.AllowPowerWhenAttacking = parser.ParseBoolean() },
        { "MaxActiveRank", (parser, x) => x.MaxActiveRank = parser.ParseInteger() },
        { "AffectContainedOnly", (parser, x) => x.AffectContainedOnly = parser.ParseBoolean() },
        { "RequiresAllTriggers", (parser, x) => x.RequiresAllTriggers = parser.ParseBoolean() },
        { "Permanent", (parser, x) => x.Permanent = parser.ParseBoolean() },
    };

    public bool StartsActive { get; private set; }
    public LazyAssetReference<ModifierList> BonusName { get; private set; }
    public LazyAssetReference<UpgradeTemplate> TriggeredBy { get; private set; }

    /// <summary>Frames between aura scans (ms in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan RefreshDelay { get; private set; }

    public Fix64 Range { get; private set; }
    public bool TargetEnemy { get; private set; }

    /// <summary>KindOf-bit gate via <see cref="ObjectFilter.Matches"/> (the engine's own
    /// implementation). Template-name +/- entries (<see cref="ObjectFilter.IncludeThings"/>/
    /// <see cref="ObjectFilter.ExcludeThings"/>) are parsed but not consumed by
    /// <c>Matches</c> itself - same documented gap as the LargeGroupBonusUpdate port.</summary>
    public ObjectFilter Filter { get; private set; }
    public string[] ConflictsWith { get; private set; }
    public bool RunWhileDead { get; private set; }
    public BitArray<ModelConditionFlag> RequiredConditions { get; private set; }
    public ModifierCategory AntiCategory { get; private set; }
    public string AntiFX { get; private set; }
    public bool AllowSelf { get; private set; }
    public bool AllowPowerWhenAttacking { get; private set; }
    public int MaxActiveRank { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool AffectContainedOnly { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public bool RequiresAllTriggers { get; private set; }

    /// <summary>This module duplicates the upgrade-mux field rather than deriving from
    /// <see cref="UpgradeModuleData"/>/<see cref="UpgradeLogicData"/> - see the file header.</summary>
    [AddedIn(SageGame.Bfme2)]
    public bool Permanent { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new AttributeModifierAuraUpdate(gameObject, gameEngine.SimContext, this);
    }
}
