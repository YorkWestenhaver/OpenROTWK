// EmpUpdate - R12 port, translated from generals-gpl Generals/GeneralsMD EMPUpdate.cpp/.h
// (GPL semantics reference; api-freeze-v1 §6 / template v1.1).
//
// Behavioral facts translated from the GPL source (base Generals EMPUpdate.cpp/.h plus the
// GeneralsMD header, whose EffectRadius/DoesNotAffect/DoesNotAffectMyOwnBuildings additions
// match the fields this module's ModuleData already parses):
//   - ctor: capture StartScale as the current visual scale, draw a random TargetScale in
//     [TargetScaleMin, TargetScaleMax] from the logic-RNG stream (GPL
//     GameLogicRandomValueReal(min, max)), and compute the die frame (now + Lifetime) and the
//     fade-trigger frame (now + StartFadeTime).
//   - update() ticks every frame (GPL UPDATE_SLEEP_NONE):
//       1. blend the tracked scale 5% of the way toward TargetScale each tick (GPL
//          m_currentScale += (m_targetScale - m_currentScale) * 0.05f) - asymptotic, never
//          exactly reaches TargetScale, same as the original.
//       2. on the EXACT frame CurrentFrame == fadeFrame (never before, never after - GPL's
//          `now == m_tintEnvPlayFrame` branch), fire the disabling attack once.
//       3. once CurrentFrame >= dieFrame, kill the EMP object (GPL `if (now >= m_dieFrame)
//          obj->kill()`).
//   - doDisableAttack(): scans EffectRadius for every live object other than self (GPL
//     ThePartitionManager->iterateObjectsInRange, FROM_BOUNDINGSPHERE_3D - IPartitionQuery's
//     "live objects" contract already excludes dead/destroyed ones, so no separate liveness
//     filter is added here). Per candidate:
//       - DoesNotAffectMyOwnBuildings: a structure owned by the same player as the EMP is
//         exempt (GPL's own-structure guard).
//       - DoesNotAffect: candidates matching the authored filter are exempt (see F-EMP-2 below
//         on why ObjectFilter.Matches is the exemption test here).
//       - non-vehicle, non-structure, non-SPAWNS_ARE_THE_WEAPONS, non-AIRCRAFT candidates
//         are skipped entirely (GPL: "DONT DISABLE PEOPLE, EXCEPT FOR STINGER SOLDIERS");
//         AIRCRAFT passes this guard so the dedicated airborne-aircraft branch below is
//         reachable.
//       - an airborne AIRCRAFT is killed outright (GPL "this should use some sort of DEADSTICK
//         DIE"), UNLESS it is EMP_HARDENED (ZH patch exemption) or it is an allied TRANSPORT
//         (GPL "DONT DISABLE YOUR OWN TRANSPORT PLANES").
//       - a STRUCTURE is skipped unless IsFactionStructure (GPL isFactionStructure(); this
//         engine's IsFactionStructure is a standing `=> false` stub - see F-EMP-3).
//       - everything remaining is disabled for DisabledDuration frames (GPL
//         curVictim->setDisabledUntil(DISABLED_EMP, now + m_disabledDuration), ported as
//         GameObject.Disable(DisabledType.Emp, now + DisabledDuration)) and requests one
//         attached disable-FX particle system via ISimEvents (see F-EMP-1 on emitter-count
//         scaling).
//
// FINDINGS (behavior-fact gaps, filed not invented):
//   F-EMP-1 (DisableFXParticleSystem emitter count): GPL spawns MAX(15,
//     ceil(SparksPerCubicFoot * footprintArea * min(height, 10))) separate emitters per victim,
//     each with its own random offset/delay (GameLogicRandomValue draws per emitter). Neither
//     SparksPerCubicFoot nor a Fix64 footprint-area/height facade exists on GameObject today
//     (Geometry's footprint/height accessors are float-typed and this module is [SimState] -
//     scoped analyzer mode bans float in this file), and the task packet's field list omits
//     SparksPerCubicFoot, so the volume-weighted emitter count is not invented here. This port
//     requests exactly one attached particle-system event per disabled victim (GPL's minimum
//     floor behavior, MAX(15, ...) without the volume term) via
//     ISimEvents.FireParticleSystemAtObject - the emitter multiplicity and the per-emitter
//     random placement/delay are left as an unmodeled client-visual detail pending a Fix64
//     geometry-volume facade.
//   F-EMP-2 (DoesNotAffect semantics): the GeneralsMD .cpp actually gates DoesNotAffect through
//     a WEAPON_AFFECTS bitmask (m_rejectMask, parsed via TheWeaponAffectsMaskNames) plus a
//     pair of KindOfMaskType fields that are dead code in the shipped source (commented out at
//     the call site). Neither shape matches ObjectFilter, the type this module's field already
//     parses as (predating this port). Per the task packet's plain-language summary
//     ("DoesNotAffect filter ... to exclude protected objects"), this port treats a
//     ObjectFilter match as the exemption test (`DoesNotAffect.Matches(candidate)` => skip),
//     the natural reading of an object-kind exemption filter and consistent with how
//     ObjectFilter is used as a membership predicate elsewhere (e.g. LargeGroupBonusUpdate).
//   F-EMP-3 (IsFactionStructure): GameObject.IsFactionStructure is a standing `=> false` stub
//     (also relied on as-is by ActiveBody), so the STRUCTURE branch below never currently
//     passes; structures are ported faithfully to the existing (incomplete) stub rather than
//     given ad hoc special-casing. Filed, not invented around.
//   F-EMP-4 (orientation randomization, GPL `setOrientation(GameLogicRandomValueReal(-PI,PI))`
//     in the ctor): position/orientation are unmigrated float transform substrate that a
//     [SimState] module may not touch (design-module-api D-7), so this draw and its visual
//     effect are not modeled. Only the TargetScale draw is taken, so this module's logic-RNG
//     draw count differs from retail by the one omitted orientation draw - a known, filed gap,
//     not a determinism hazard for THIS module's own state (nothing here reads the omitted
//     draw back).
//   F-EMP-5 (visual scale/tint output): GPL pushes m_currentScale to Drawable::setInstanceScale
//     and the Start/EndColor tint envelope to Drawable::colorTint/colorFlash every tick. Both
//     are float-typed client-render calls with no Fix64-safe entry point from [SimState] code
//     today (same class of gap BoneFXUpdate's particle-system TODOs document). The scale
//     interpolation math itself IS translated and tracked as Fix64 sim state (so its
//     convergence behavior is faithfully modeled and testable); only the final "hand it to the
//     renderer" step is parked. StartColor/EndColor are still parsed (ColorRgb, byte-valued) so
//     authored data round-trips, but are not applied to any Drawable.
//   F-EMP-6 (DisabledDuration auto-expiry): GameObject.Disable(type, frame) records the
//     un-disable frame, but the sweep that would clear it once that frame passes
//     (GameObject.CheckDisabledStates, private) is only ever called from the internal
//     GameObject.Update() method - which nothing in this engine snapshot's GameLogic.Update()
//     sleepy-module loop calls (that loop dispatches UpdateModule.Update() directly; the
//     per-object CheckDisabledStates sweep is dead code today, a pre-existing engine gap, not
//     something introduced by or fixable from this module). A victim this module disables
//     therefore stays disabled past DisabledDuration in the current engine, exactly as any
//     other module's Disable() call would. This port still records the correct un-disable
//     frame (Context.CurrentFrame + DisabledDuration, matching GPL's setDisabledUntil target)
//     so the fix is a one-line wiring change in GameLogic/GameObject, not a re-port of this
//     module, once that sweep is connected.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4). Field order mirrors the GPL xfer() member
// order (dieFrame, fadeFrame, currentScale, targetScale) as closely as the GPL's own
// (near-empty) xfer() allows - the original's xfer() persists nothing beyond the version byte,
// so this port's ordering is OUR choice (F9), not a translated one.

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Graphics.ParticleSystems;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class EmpUpdate : UpdateModule
{
    /// <summary>The GPL literal 0.05f-per-frame scale-blend factor.</summary>
    private static readonly Fix64 ScaleBlendFactor = Fix64.FromDecimalLiteral("0.05");

    private readonly EmpUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Frame we die on (GPL m_dieFrame).</summary>
    private LogicFrame _dieFrame;

    /// <summary>Frame the disabling attack fires on (GPL m_tintEnvPlayFrame).</summary>
    private LogicFrame _fadeFrame;

    /// <summary>Tracked visual scale, blended toward <see cref="_targetScale"/> each tick
    /// (GPL m_currentScale; F-EMP-5: never actually pushed to a renderer here).</summary>
    private Fix64 _currentScale;

    /// <summary>Randomized target scale drawn once at construction (GPL m_targetScale).</summary>
    private Fix64 _targetScale;

    public EmpUpdate(GameObject gameObject, ISimContext context, EmpUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        var now = Context.CurrentFrame;
        _currentScale = data.StartScale;
        _dieFrame = now + data.Lifetime;
        _fadeFrame = now + data.StartFadeTime;
        _targetScale = Context.GameLogicRandom.NextFix64(data.TargetScaleMin, data.TargetScaleMax);

        // GPL ticks every frame (UPDATE_SLEEP_NONE).
        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        var now = Context.CurrentFrame;

        // GPL: m_currentScale += (m_targetScale - m_currentScale) * 0.05f (F-EMP-5: tracked,
        // not pushed to a renderer).
        _currentScale += (_targetScale - _currentScale) * ScaleBlendFactor;

        // GPL: `if (now == m_tintEnvPlayFrame)` - exactly once, never before or after.
        if (now == _fadeFrame)
        {
            DoDisableAttack();
        }

        if (now >= _dieFrame)
        {
            GameObject.Kill();
            // Guard against a repeat kill if this module still ticks before the object is
            // reaped (same defensive shape as LifetimeUpdate's post-kill frame push).
            _dieFrame = LogicFrame.MaxValue;
        }

        return UpdateSleepTime.None;
    }

    /// <summary>GPL doDisableAttack(): scan EffectRadius, disable/kill qualifying victims.</summary>
    private void DoDisableAttack()
    {
        if (_data.EffectRadius <= Fix64.Zero)
        {
            return;
        }

        var self = GameObject;

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(self, _data.EffectRadius))
        {
            if (candidate == self)
            {
                continue;
            }

            // GPL: doesNotAffectMyOwnBuildings guard (structures only).
            if (_data.DoesNotAffectMyOwnBuildings
                && candidate.IsKindOf(ObjectKinds.Structure)
                && candidate.Owner == self.Owner)
            {
                continue;
            }

            // F-EMP-2: DoesNotAffect exempts candidates it matches.
            if (_data.DoesNotAffect != null && _data.DoesNotAffect.Matches(candidate))
            {
                continue;
            }

            if (!candidate.IsKindOf(ObjectKinds.Vehicle)
                && !candidate.IsKindOf(ObjectKinds.Structure)
                && !candidate.IsKindOf(ObjectKinds.SpawnsAreTheWeapons)
                && !candidate.IsKindOf(ObjectKinds.Aircraft))
            {
                // GPL: "DONT DISABLE PEOPLE, EXCEPT FOR STINGER SOLDIERS". Aircraft must
                // still pass through here - the dedicated airborne-AIRCRAFT branch right
                // below (kill outright / EMP_HARDENED / allied-TRANSPORT exemptions) would
                // otherwise be unreachable dead code.
                continue;
            }

            if (candidate.IsKindOf(ObjectKinds.Aircraft) && Context.Terrain.IsSignificantlyAboveTerrain(candidate))
            {
                if (candidate.IsKindOf(ObjectKinds.EmpHardened))
                {
                    continue;
                }

                if (candidate.IsKindOf(ObjectKinds.Transport) && candidate.GetRelationship(self) == RelationshipType.Allies)
                {
                    // GPL: "DONT DISABLE YOUR OWN TRANSPORT PLANES".
                    continue;
                }

                candidate.Kill();
                continue;
            }

            if (candidate.IsKindOf(ObjectKinds.Structure) && !candidate.IsFactionStructure)
            {
                // F-EMP-3: IsFactionStructure is a standing stub, so this branch never
                // currently passes - ported faithfully to the existing engine behavior.
                continue;
            }

            candidate.Disable(DisabledType.Emp, Context.CurrentFrame + _data.DisabledDuration);

            // F-EMP-1: one attached emitter per victim; volume-weighted emitter count is not
            // modeled (no SparksPerCubicFoot field, no Fix64 footprint/height facade).
            var fxTemplate = _data.DisableFXParticleSystem?.Value;
            if (fxTemplate != null)
            {
                Context.Events.FireParticleSystemAtObject(fxTemplate.Name, candidate.Id, string.Empty, false);
            }
        }
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferFrame("DieFrame", ref _dieFrame, Tolerance.Exact);
        xfer.XferFrame("FadeFrame", ref _fadeFrame, Tolerance.Exact);
        xfer.XferFix64("CurrentScale", ref _currentScale, Tolerance.Exact);
        xfer.XferFix64("TargetScale", ref _targetScale, Tolerance.Exact);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Simulates an electromagnetic pulse effect: grows/fades a visual effect, then disables
/// (and kills airborne aircraft among) nearby vehicles, structures, and aircraft.
/// </summary>
[SimDataAudited]
public sealed class EmpUpdateModuleData : UpdateModuleData
{
    internal static EmpUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<EmpUpdateModuleData> FieldParseTable = new IniParseTable<EmpUpdateModuleData>
    {
        { "DisabledDuration", (parser, x) => x.DisabledDuration = parser.ParseDurationLogicFrames() },
        { "Lifetime", (parser, x) => x.Lifetime = parser.ParseDurationLogicFrames() },
        { "StartFadeTime", (parser, x) => x.StartFadeTime = parser.ParseDurationLogicFrames() },
        { "StartScale", (parser, x) => x.StartScale = parser.ParseFix64() },
        { "TargetScaleMin", (parser, x) => x.TargetScaleMin = parser.ParseFix64() },
        { "TargetScaleMax", (parser, x) => x.TargetScaleMax = parser.ParseFix64() },
        { "StartColor", (parser, x) => x.StartColor = parser.ParseColorRgb() },
        { "EndColor", (parser, x) => x.EndColor = parser.ParseColorRgb() },
        { "DisableFXParticleSystem", (parser, x) => x.DisableFXParticleSystem = parser.ParseFXParticleSystemTemplateReference() },
        { "DoesNotAffect", (parser, x) => x.DoesNotAffect = ObjectFilter.Parse(parser) },
        { "DoesNotAffectMyOwnBuildings", (parser, x) => x.DoesNotAffectMyOwnBuildings = parser.ParseBoolean() },
        { "EffectRadius", (parser, x) => x.EffectRadius = parser.ParseFix64() },
    };

    /// <summary>Frames a disabled victim stays disabled (ms in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan DisabledDuration { get; private set; }

    /// <summary>Frames until this EMP object kills itself (ms in INI, ceil-quantized, S5).</summary>
    public LogicFrameSpan Lifetime { get; private set; } = LogicFrameSpan.One;

    /// <summary>Frames until the disabling attack fires (ms in INI, ceil-quantized, S5).</summary>
    public LogicFrameSpan StartFadeTime { get; private set; }

    /// <summary>Initial tracked visual scale.</summary>
    public Fix64 StartScale { get; private set; } = Fix64.One;

    /// <summary>Lower bound of the randomized target scale.</summary>
    public Fix64 TargetScaleMin { get; private set; } = Fix64.One;

    /// <summary>Upper bound of the randomized target scale.</summary>
    public Fix64 TargetScaleMax { get; private set; } = Fix64.One;

    /// <summary>F-EMP-5: parsed for authoring round-trip fidelity; not applied to a renderer.</summary>
    public ColorRgb StartColor { get; private set; }

    /// <summary>F-EMP-5: parsed for authoring round-trip fidelity; not applied to a renderer.</summary>
    public ColorRgb EndColor { get; private set; }

    /// <summary>F-EMP-1: one instance requested per disabled victim; emitter count is not
    /// volume-scaled.</summary>
    public LazyAssetReference<FXParticleSystemTemplate> DisableFXParticleSystem { get; private set; }

    /// <summary>F-EMP-2: candidates this filter matches are exempt from the disabling attack.</summary>
    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public ObjectFilter DoesNotAffect { get; private set; }

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public bool DoesNotAffectMyOwnBuildings { get; private set; }

    /// <summary>Scan radius for the disabling attack.</summary>
    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public Fix64 EffectRadius { get; private set; } = Fix64.FromDecimalLiteral("200");

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new EmpUpdate(gameObject, gameEngine.SimContext, this);
    }
}
