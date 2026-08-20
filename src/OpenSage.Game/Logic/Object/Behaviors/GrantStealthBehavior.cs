// GrantStealthBehavior - R12 port. GPL ref: GeneralsMD/Code/GameEngine/{Include,Source}/
// GameLogic/{Module,Object/Behavior}/GrantStealthBehavior.{h,cpp} (GPL semantics reference
// only; this is fresh code against the frozen contract). Behavior facts used:
//   - ctor: the scan radius starts at StartRadius; an optional RadiusParticleSystemName is
//     created once at the object's position and lives for the module's lifetime (GPL keeps
//     its ParticleSystemID only to destroy it in the destructor - the client owns emitter
//     lifetime here, same posture as TransitionDamageFX/F-TDF-1, so this port holds no id).
//   - update(): dead => sleep forever. Every tick the radius grows by RadiusGrowRate; once it
//     reaches (or passes) FinalRadius it is clamped there and this is the FINAL scan. The
//     live scan (GPL's iterateObjectsInRange with PartitionFilterRelationship::ALLOW_ALLIES +
//     PartitionFilterAlive + PartitionFilterSameMapStatus) is translated as: every object
//     within the current radius, alive, on the same off-map/on-map side as the host, and
//     owned by or allied with the host's owner (relationship ALLOW_ALLIES includes the host
//     itself, which grantStealthToObject then explicitly skips - GPL's own self-skip, kept
//     here). Every survivor gets grantStealthToObject.
//   - grantStealthToObject(obj): skip self; skip objects that fail the module's KindOf mask
//     (unconfigured GPL default is ALL bits, so an unset mask here matches everything, same
//     convention as AutoHealBehavior.MatchesKindOfFilters); objects with a StealthUpdate
//     module receive a permanent grant (GPL stealth->receiveGrant(), no args - permanent
//     until revoked, unlike a special power's timed grant) via the R9-landed
//     StealthUpdate.ReceiveGrant seam.
//   - the final scan destroys the host object and the update returns UPDATE_SLEEP_FOREVER;
//     any earlier scan returns UPDATE_SLEEP_NONE (scan again next frame).
//
// NOT translated (recorded, not invented - see file header precedent in
// SabotageSuperweaponCrateCollide.cs and TransitionDamageFX.cs for the same posture):
//   - Drawable::flashAsSelected() feedback on each granted object: no drawable-flash API is
//     ported on ISimEvents yet (client render concern, S8-eligible once such a seam exists).
//   - the GPL file's checkForGrantStealth/GrantStealthPlayerScanHelper static helper (which
//     would additionally tag a contained object's visible rider): it is dead code in the GPL
//     source - defined but never called from update(), which uses the partition-iterator scan
//     exclusively. Translating unreachable retail code would be inventing behavior, not
//     porting it (task rule: "translate, do not invent"). A rider that independently occupies
//     the partition grid within the scan radius (and matches KindOf) is still granted stealth
//     through the ordinary scan, same as any other candidate.
//
// Every mutable sim field appears in Xfer exactly once (api-freeze-v1 §3); tolerances are the
// field's conformance class at its declaration site (§4).

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Graphics.ParticleSystems;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class GrantStealthBehavior : UpdateModule
{
    private readonly GrantStealthBehaviorModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>The current scan radius, grown by RadiusGrowRate each tick until it reaches
    /// FinalRadius (GPL m_currentScanRadius).</summary>
    private Fix64 _currentScanRadius;

    /// <summary>Whether the one-shot radius particle system has already been requested.
    /// Sim state so a save/load mid-behavior does not re-fire it.</summary>
    private bool _radiusParticleSystemFired;

    public GrantStealthBehavior(GameObject gameObject, ISimContext context, GrantStealthBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        _currentScanRadius = _data.StartRadius;

        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        // GPL ctor creates the radius particle system once, at the object's own position.
        // It is requested here on the first tick rather than in the constructor: GameObject.Id
        // is assigned by GameLogic only AFTER the behavior modules are constructed, so a
        // constructor-time request would carry ObjectId.Invalid and address nothing.
        // Output-only event (S8) - the client owns the emitter's lifetime, same posture as
        // TransitionDamageFX (F-TDF-1); this module keeps no particle-system id.
        if (!_radiusParticleSystemFired)
        {
            _radiusParticleSystemFired = true;

            if (_data.RadiusParticleSystemName != null)
            {
                Context.Events.FireParticleSystemAtObject(
                    _data.RadiusParticleSystemName.Value.Name,
                    GameObject.Id,
                    bone: string.Empty,
                    randomBone: false);
            }
        }

        if (GameObject.IsEffectivelyDead)
        {
            return UpdateSleepTime.Forever;
        }

        _currentScanRadius += _data.RadiusGrowRate;

        var isFinalScan = false;
        if (_currentScanRadius >= _data.FinalRadius)
        {
            _currentScanRadius = _data.FinalRadius;
            isFinalScan = true;
        }

        // GPL scan filters: PartitionFilterRelationship::ALLOW_ALLIES (self included, skipped
        // below), PartitionFilterAlive, PartitionFilterSameMapStatus.
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _currentScanRadius))
        {
            if (candidate.IsEffectivelyDead ||
                !IsAlliedWith(candidate) ||
                candidate.IsOffMap != GameObject.IsOffMap)
            {
                continue;
            }

            GrantStealthToObject(candidate);
        }

        if (isFinalScan)
        {
            Context.GameLogic.DestroyObject(GameObject);
            return UpdateSleepTime.Forever;
        }

        return UpdateSleepTime.None;
    }

    /// <summary>GPL grantStealthToObject: self-skip, KindOf gate, then the permanent grant.</summary>
    private void GrantStealthToObject(GameObject candidate)
    {
        if (candidate == GameObject)
        {
            return;
        }

        if (!MatchesKindOfFilter(candidate))
        {
            return;
        }

        var stealth = candidate.FindBehavior<StealthUpdate>();
        stealth?.ReceiveGrant(true);
    }

    /// <summary>GPL obj->isAnyKindOf(d->m_kindOf). The GPL default seeds every bit, so an
    /// unset mask here matches everything (AutoHealBehavior.MatchesKindOfFilters convention).</summary>
    private bool MatchesKindOfFilter(GameObject candidate) =>
        _data.KindOf?.Intersects(candidate.Definition.KindOf) != false;

    /// <summary>Same owner or an allied owner - the ALLOW_ALLIES relationship test
    /// (AutoHealBehavior.IsAlliedWith convention).</summary>
    private bool IsAlliedWith(GameObject candidate) =>
        candidate.Owner == GameObject.Owner || GameObject.Owner.Allies.Contains(candidate.Owner);

    // ---- the single contract walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferFix64("CurrentScanRadius", ref _currentScanRadius, Tolerance.Exact);
        xfer.XferBool("RadiusParticleSystemFired", ref _radiusParticleSystemFired);
    }

    // ---- legacy retail-save reader (outside the contract, F9): kept byte-for-byte so the
    // corpus self-diff does not regress, remapped onto the real fields. Retail layout: base,
    // 4-byte client particle-system id (discarded - F-TDF-1 posture), then the GPL
    // xferReal(&m_currentScanRadius) float. This file is [SimState]-scoped (SIMCORE001 bans
    // float wholesale), so the four bytes are read as their raw IEEE bit pattern via
    // PersistUInt32 and handed straight to Fix64.FromWireFloat (the blessed F4 wire-float
    // crossing) - no float is ever declared, cast, or literal in this file. ----
    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.SkipUnknownBytes(4);

        var currentScanRadiusBits = 0u;
        reader.PersistUInt32(ref currentScanRadiusBits);
        _currentScanRadius = Fix64.FromWireFloat(currentScanRadiusBits);

        // A retail save is always mid-behavior: the emitter was already created before the
        // save (its client id is the four bytes skipped above), so do not re-request it.
        _radiusParticleSystemFired = true;
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[SimDataAudited]
public sealed class GrantStealthBehaviorModuleData : BehaviorModuleData
{
    internal static GrantStealthBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<GrantStealthBehaviorModuleData> FieldParseTable = new IniParseTable<GrantStealthBehaviorModuleData>
    {
        { "StartRadius", (parser, x) => x.StartRadius = parser.ParseFix64() },
        { "FinalRadius", (parser, x) => x.FinalRadius = parser.ParseFix64() },
        { "RadiusGrowRate", (parser, x) => x.RadiusGrowRate = parser.ParseFix64() },
        { "RadiusParticleSystemName", (parser, x) => x.RadiusParticleSystemName = parser.ParseFXParticleSystemTemplateReference() },
        { "KindOf", (parser, x) => x.KindOf = parser.ParseEnumBitArray<ObjectKinds>() },
    };

    /// <summary>Scan radius at spawn (quantized Q31.32, S5). GPL ctor default 0.</summary>
    public Fix64 StartRadius { get; private set; } = Fix64.Zero;

    /// <summary>Scan radius at which the behavior completes and destroys the host (quantized
    /// Q31.32, S5). GPL ctor default 200.</summary>
    public Fix64 FinalRadius { get; private set; } = Fix64.FromDecimalLiteral("200");

    /// <summary>Radius growth per tick (quantized Q31.32, S5). GPL ctor default 10.</summary>
    public Fix64 RadiusGrowRate { get; private set; } = Fix64.FromDecimalLiteral("10");

    public LazyAssetReference<FXParticleSystemTemplate> RadiusParticleSystemName { get; private set; }

    /// <summary>Kinds eligible for the stealth grant; null = all kinds (GPL default: all bits
    /// set).</summary>
    public BitArray<ObjectKinds> KindOf { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new GrantStealthBehavior(gameObject, gameEngine.SimContext, this);
    }
}
