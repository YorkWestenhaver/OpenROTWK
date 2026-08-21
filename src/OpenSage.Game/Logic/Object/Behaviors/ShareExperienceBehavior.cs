// ShareExperienceBehavior - R13 port. No generals-gpl sibling ("ShareExperience" does not
// appear anywhere in generals-gpl or generals-community); this is a from-field-set derivation
// over two already-landed, in-repo mechanism halves - not an invention. See
// bfme2-workbench/research/modules-r13/specs/ShareExperienceBehaviorModuleData.md for the full
// grounding. Summary:
//   - the sink half is already ported and already cross-object: ExperienceTracker.
//     AddExperiencePoints already grants XP to an object other than the one that earned it
//     (ExperienceTracker.cs, the ExperienceSink redirect) - this module calls the exact same
//     API on each shared recipient, just from a different caller;
//   - the radius+ObjectFilter range-query half is the established Update-module pattern landed
//     identically in EnemyNearUpdate/EmotionTrackerUpdate/PilotFindVehicleUpdate/
//     StealthDetectorUpdate (Context.Partition.QueryObjectsInRadius + a per-candidate
//     ObjectFilter.Matches predicate).
//
// The tick (ModuleData spec §1.2): every frame (no delay field authored, same "tick every
// frame, no periodic re-arm" shape as EmpUpdate), read CurrentExperience and diff it against
// the last-observed value (edge-detection idiom, same rising/falling-edge shape
// EnemyNearUpdate/EmotionTrackerUpdate already use for their own scans). A positive delta is a
// gain to share: scan Radius for ObjectFilter-matching, live candidates and grant each one
// AddExperiencePoints(delta) directly (flat share, no distance falloff - see F-SEB-1 below).
// A non-positive delta (no gain, or a loss/reset from SetExperienceAndLevel) shares nothing -
// this module only mirrors AddExperiencePoints-shaped gains, never losses, matching the
// ExperienceSink precedent (which only ever pledges gains, never losses).
//
// FINDINGS (behavior-fact gaps, filed not invented):
//   F-SEB-1 (DropOff non-zero branch - the one every one of the ~9 retail-authored instances
//     actually sets - NOT modeled by this port, genuinely open): DropOff == 1 (100% of retail
//     data corpus authors exactly this; every site carries the .ini comment "Must be one or
//     zero.") plausibly reads, per the WWAudio "DropOff" idiom
//     (generals-gpl/.../WWAudio/SoundPseudo3D.h:111), as "linearly interpolate the shared
//     amount from full at distance 0 to zero at distance Radius." Implementing that needs a
//     per-candidate Fix64 distance value, and no Fix64 distance-between-two-GameObjects facade
//     exists anywhere in this engine today (GameObject has no Distance/FixVector3 member,
//     ISimContext has no distance query, QueryObjectsInRadius returns a bare
//     IEnumerable<GameObject> with no distance out-value). This port therefore ships the
//     DropOff == 0 (flat) branch only; every currently-authored retail instance will share flat
//     instead of falling off with distance until a GameObject.DistanceFix64To(GameObject)
//     facade lands (reserved name, not built by this port - see the spec's §2/§4).
//   F-SEB-2 (ObjectFilter.Matches gap, pre-existing, not new to this port): Matches only
//     consults Exclude/Include ObjectKinds bits and the All rule; it never consults
//     Rules.None/Any/Allies/Enemies/SamePlayer, nor IncludeThings/ExcludeThings. Retail's
//     `NONE +RhunBelokhZa` filter (wardog.ini) authors a Things entry that Matches today never
//     checks, so that filter currently evaluates as "matches nothing." The same gap every other
//     landed ObjectFilter consumer (EnemyNearUpdate/EmpUpdate/EmotionTrackerUpdate) already
//     inherits unfixed - not this port's file to fix.
//   F-SEB-3 (poll idiom cannot separate a gain from an initialization): the engine adds an
//     ExperienceUpdate helper ("ModuleTag_ExperienceHelper") to every object on every
//     non-Generals game, and that helper raises a still-zero CurrentExperience to the rank-1
//     floor of 1 on its first tick (SetExperienceAndLevel, not AddExperiencePoints). Polling
//     sees that as an ordinary +1 delta. There is no per-point "XP gained" event to subscribe
//     to, so the baseline is seeded at the rank-1 floor instead of at literal zero (see the
//     ctor) - which costs at most a single point, once, on an object that genuinely earns
//     exactly 1 XP before this module's first tick.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class ShareExperienceBehavior : UpdateModule
{
    /// <summary>
    /// Rank-1 experience floor every BFME object is initialized to by the engine-added
    /// ExperienceUpdate helper (GameObject adds "ModuleTag_ExperienceHelper" to every object on
    /// every non-Generals game; on its first tick it raises a still-zero CurrentExperience to
    /// this value through SetExperienceAndLevel). That is an initialization, not a gain, and
    /// §1.2 shares only AddExperiencePoints-shaped gains - never SetExperienceAndLevel-shaped
    /// rewrites - so the baseline below starts at the floor rather than at literal zero.
    /// </summary>
    private const int EngineRankOneExperienceFloor = 1;

    private readonly ShareExperienceBehaviorModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Experience last observed on our own ExperienceTracker; a positive delta since
    /// this value is what gets shared (edge-detection idiom, §1.2).</summary>
    private int _lastObservedExperience;

    public ShareExperienceBehavior(GameObject gameObject, ISimContext context, ShareExperienceBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // Seed from whatever XP we already have at construction time, so an object that is
        // never observed with zero XP (e.g. a template with a nonzero starting veterancy) does
        // not falsely broadcast its entire starting total as a "gain" on its first live tick.
        //
        // Clamped up to the rank-1 floor because our ctor runs before the ExperienceUpdate
        // helper's first tick: seeding the raw 0 here would make that helper's own 0 -> 1
        // initialization look like a +1 gain on our next tick and broadcast a phantom point to
        // every candidate in Radius. The clamp is ordering-independent (it holds whether the
        // helper ticks before or after us on the first frame), which a lazy first-tick seed
        // would not be. A genuine 1-point gain earned from zero before our first tick is
        // indistinguishable from that guaranteed-to-happen seed under the poll idiom, and is
        // absorbed by this clamp.
        var startingExperience = gameObject.ExperienceTracker.CurrentExperience;
        _lastObservedExperience = startingExperience > EngineRankOneExperienceFloor
            ? startingExperience
            : EngineRankOneExperienceFloor;

        // No delay field is authored on this block - tick every frame, same "absent delay
        // field" shape as EmpUpdate, no periodic re-arm.
        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        var currentExperience = GameObject.ExperienceTracker.CurrentExperience;
        var delta = currentExperience - _lastObservedExperience;

        if (delta > 0)
        {
            ShareGain(delta);
        }

        _lastObservedExperience = currentExperience;

        return UpdateSleepTime.None;
    }

    /// <summary>Broadcasts a flat share of a positive XP gain to every live, filter-matching
    /// candidate inside Radius (§1.2 step 4; DropOff == 0 branch only, see F-SEB-1).</summary>
    private void ShareGain(int sharedAmount)
    {
        var self = GameObject;

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(self, _data.Radius))
        {
            if (candidate == self || candidate.IsDestroyed || candidate.IsEffectivelyDead)
            {
                continue;
            }

            if (!_data.ObjectFilter.Matches(candidate))
            {
                continue;
            }

            // AddExperiencePoints already internally no-ops for a non-trainable candidate with
            // no sink of its own (ExperienceTracker.IsAcceptingExperiencePoints), so no
            // separate pre-check is required for correctness.
            candidate.ExperienceTracker.AddExperiencePoints(sharedAmount, canScaleForBonus: true);
        }
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferInt("LastObservedExperience", ref _lastObservedExperience, Tolerance.Quantum);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Shares a flat portion of this object's own experience-point gains, as they happen, with
/// every live nearby object matching ObjectFilter (see ShareExperienceBehaviorModuleData.md,
/// modules-r13, for the full field-by-field grounding).
/// </summary>
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class ShareExperienceBehaviorModuleData : UpdateModuleData
{
    internal static ShareExperienceBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<ShareExperienceBehaviorModuleData> FieldParseTable = new IniParseTable<ShareExperienceBehaviorModuleData>
    {
        { "ObjectFilter", (parser, x) => x.ObjectFilter = ObjectFilter.Parse(parser) },
        { "Radius", (parser, x) => x.Radius = parser.ParseFix64() },
        { "DropOff", (parser, x) => x.DropOff = parser.ParseFix64() }
    };

    /// <summary>Recipient-eligibility filter for the broadcast scan.</summary>
    public ObjectFilter ObjectFilter { get; private set; }

    /// <summary>Scan radius for the broadcast, same role/shape as
    /// EmotionTrackerUpdate.FearScanDistance / EmpUpdate.EffectRadius.</summary>
    public Fix64 Radius { get; private set; }

    /// <summary>Distance-falloff mode switch; every retail-authored instance sets this to
    /// exactly 1.0 (data-authored "Must be one or zero."). Parsed as Fix64 (the generic
    /// ParseFix64() call already exists for this shape and a stray non-0/1 value in an
    /// unaudited .ini should parse rather than throw), but this port's own consumption treats
    /// it as a boolean gate: only DropOff == 0 (flat share) is implemented (F-SEB-1); a
    /// non-zero value currently has no distance-falloff effect, pending a Fix64
    /// GameObject-distance facade that does not yet exist.</summary>
    public Fix64 DropOff { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ShareExperienceBehavior(gameObject, gameEngine.SimContext, this);
    }
}
