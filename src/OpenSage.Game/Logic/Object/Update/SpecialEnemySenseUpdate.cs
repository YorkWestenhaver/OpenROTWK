// SpecialEnemySenseUpdate - R13 port through the full task packet.
//
// Classification: data-derivable. No direct GPL source (SpecialEnemySenseUpdate is a
// BFME/BFME2-only module, confirmed absent from generals-gpl/generals-community by a
// class-name grep). Behavior is derived by analogy from the landed R9 near-sibling
// EnemyNearUpdate/EnemyNearUpdateModuleData (GPL-sourced), generalized along the two axes
// the INI schema itself names: an explicit author-specified ScanRange instead of the
// object's vision range, and an ObjectFilter-gated candidate match instead of "any enemy" -
// the latter idiom already established by the landed AttributeModifierAuraUpdate (a separate
// module-level relationship check alongside an independent, KindOf-only ObjectFilter.Matches
// gate). See modules-r13/specs/SpecialEnemySenseUpdateModuleData.md for the full derivation.
//
// State is exactly { scanDelay (countdown), enemyNear (bool) } - the identical two-field
// inventory EnemyNearUpdate already establishes for this exact algorithm shape. Ctor biases
// the first scan by a logic-RNG draw in [0, ScanInterval] frames (GPL "bias a random amount
// so everyone doesn't spike at once", substituting ScanInterval for ScanDelayTime - same
// field role, renamed by the BFME INI schema). Update() runs every frame; on enemyNear's
// rising/falling edge, sets/clears ModelConditionFlag.SpecialEnemyNear (already declared in
// the ported enum). CheckForEnemies() scans Context.Partition.QueryObjectsInRadius(GameObject,
// ScanRange) - ScanRange standing in for EnemyNearUpdate's GameObject.VisionRange.
//
// Candidate predicate (IsMatchingEnemy): self-exclusion; liveness/on-map
// (IsEffectivelyDead/IsOffMap); enemy relationship, hardcoded via Owner.Enemies (reused
// verbatim from EnemyNearUpdate.IsVisibleEnemy / AttributeModifierAuraUpdate.IsEligible's
// TargetEnemy branch - see the spec's "Design decision: relationship source" for why this is
// hardcoded rather than read out of the filter's ENEMIES keyword, which ObjectFilter.Matches
// does not consume); then the filter gate (EnemyFilter.Matches(candidate)), the KindOf-bit-
// only gate ObjectFilter.Matches already implements.
//
// Deliberately NOT ported: EnemyNearUpdate's hardcoded KindOf.Structure reject. Unlike GPL's
// checkForEnemies, this module has an expressive mechanism (the author's own +KindOf filter)
// to say exactly which kinds should trip the sense; carrying the hardcoded reject forward
// would silently override an explicit "+STRUCTURE" inclusion with no way to opt back in. This
// is a deliberate behavior difference from EnemyNearUpdate, not an oversight (F-SES-2).
//
// FINDINGS (behavior-fact gaps, filed not invented - see the spec doc):
//   F-SES-1 relationship keyword inside SpecialEnemyFilter (ENEMIES/ALLIES/NEUTRAL) is inert
//     on today's ObjectFilter.Matches (does not consume Rules.Enemies/.Allies/.Neutrals/
//     .None/.Any at all) - same pre-existing gap AttributeModifierAuraUpdate/
//     LargeGroupBonusUpdate already carry. Not this packet's to fix (ObjectFilter.cs is
//     read-only shared code, out of scope).
//   F-SES-2 no hardcoded structure/building exclusion, differing from EnemyNearUpdate by
//     design - see above.
//   F-SES-3 (= F-ENU-1) no line-of-sight filter: every in-radius, filter-matching enemy
//     counts as "sensed" regardless of terrain occlusion.
//   F-SES-4 (= F-ENU-2) no stealth/detection filter: stealth state is not exposed to a
//     [SimState] module.
//   F-SES-5 (= F-ENU-5) dual relationship representation: consumes Owner.Enemies, the same
//     Player set AutoHealBehavior/EnemyNearUpdate/AttributeModifierAuraUpdate already consume.
//
// Every mutable sim field appears in Xfer exactly once; tolerances are the field's
// conformance class at its declaration site (frame-count field: Exact, matching
// EnemyNearUpdate's tolerance choice for the analogous field).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class SpecialEnemySenseUpdate : UpdateModule
{
    private readonly SpecialEnemySenseUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Frames remaining until the next enemy scan; re-armed to ScanInterval.</summary>
    private LogicFrameSpan _scanDelay;

    /// <summary>Whether a matching enemy was within scan range at the last scan.</summary>
    private bool _enemyNear;

    public SpecialEnemySenseUpdate(GameObject gameObject, ISimContext context, SpecialEnemySenseUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // GPL-analog ctor: bias the first scan by [0, ScanInterval] frames drawn from the
        // context logic stream (S3) so the stagger is lockstep-identical on every peer.
        // The degenerate zero-interval case skips the draw, matching EnemyNearUpdate's ctor.
        if (_data.ScanInterval.Value > 0)
        {
            var stagger = Context.GameLogicRandom.Next(0, (int)_data.ScanInterval.Value);
            _scanDelay = new LogicFrameSpan((uint)stagger);
        }

        // Ticks every frame (UPDATE_SLEEP_NONE analog); the countdown gates the scan.
        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        var wasNear = _enemyNear;

        CheckForEnemies();

        if (_enemyNear && !wasNear)
        {
            // Rising edge: switch the art to its "special enemy near" state (client output).
            GameObject.SetModelConditionState(ModelConditionFlag.SpecialEnemyNear);
        }
        else if (!_enemyNear && wasNear)
        {
            // Falling edge: return to idle art (client output).
            GameObject.ClearModelConditionState(ModelConditionFlag.SpecialEnemyNear);
        }

        return UpdateSleepTime.None;
    }

    /// <summary>Periodic filter-gated enemy scan, ScanRange standing in for vision range.</summary>
    private void CheckForEnemies()
    {
        if (_scanDelay == LogicFrameSpan.Zero)
        {
            _scanDelay = _data.ScanInterval;

            var found = false;
            foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.ScanRange))
            {
                if (IsMatchingEnemy(candidate))
                {
                    found = true;
                    break;
                }
            }

            _enemyNear = found;
        }
        else
        {
            _scanDelay -= LogicFrameSpan.One;
        }
    }

    /// <summary>
    /// A live, on-map, enemy object matching the author-specified filter. Unlike
    /// EnemyNearUpdate.IsVisibleEnemy, no hardcoded structure reject - the filter itself
    /// decides KindOf eligibility (F-SES-2).
    /// </summary>
    private bool IsMatchingEnemy(GameObject candidate)
    {
        if (candidate == GameObject)
        {
            // The partition query already excludes the center; belt-and-suspenders.
            return false;
        }

        // Live and on-map.
        if (candidate.IsEffectivelyDead || candidate.IsOffMap)
        {
            return false;
        }

        // Enemies only, hardcoded (see the spec's "Design decision: relationship source").
        // Consumes the same Player relationship set AutoHealBehavior/EnemyNearUpdate consume;
        // the dual relationship representations in Player are a reconciliation finding
        // (F-SES-5 = F-ENU-5).
        if (GameObject.Owner is null ||
            candidate.Owner is null ||
            !GameObject.Owner.Enemies.Contains(candidate.Owner))
        {
            return false;
        }

        // The author-specified KindOf-bit gate (AttributeModifierAuraUpdate.Filter idiom).
        // EnemyFilter is non-null on every corpus usage found; the null guard is
        // defense-in-depth for a theoretical block that omits the key.
        if (_data.EnemyFilter != null && !_data.EnemyFilter.Matches(candidate))
        {
            return false;
        }

        return true;
    }

    // ---- the single walk: save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferFrameSpan("ScanDelay", ref _scanDelay, Tolerance.Exact); // frame count: Exact (A3)
        xfer.XferBool("EnemyNear", ref _enemyNear);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class SpecialEnemySenseUpdateModuleData : UpdateModuleData
{
    internal static SpecialEnemySenseUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<SpecialEnemySenseUpdateModuleData> FieldParseTable = new IniParseTable<SpecialEnemySenseUpdateModuleData>
    {
        { "SpecialEnemyFilter", (parser, x) => x.EnemyFilter = ObjectFilter.Parse(parser) },
        { "ScanRange", (parser, x) => x.ScanRange = parser.ParseFix64() },
        { "ScanInterval", (parser, x) => x.ScanInterval = parser.ParseDurationLogicFrames() },
    };

    public ObjectFilter EnemyFilter { get; private set; }

    /// <summary>Author-specified scan radius (deterministic S3-query radius, S5-blessed Fix64 boundary).</summary>
    public Fix64 ScanRange { get; private set; }

    /// <summary>Frames between enemy scans (ms in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan ScanInterval { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SpecialEnemySenseUpdate(gameObject, gameEngine.SimContext, this);
    }
}
