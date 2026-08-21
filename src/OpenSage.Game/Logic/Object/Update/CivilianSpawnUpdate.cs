// CivilianSpawnUpdate - R13 port. BFME-only (no generals-gpl sibling, confirmed §0 of the spec);
// the audit's mis-specification fix (Civilian: BitArray<ObjectKinds> -> LazyAssetReference
// <ObjectDefinition>[]) plus two S5-class distance/duration retypes (SpawnDelayTime ->
// LogicFrameSpan, MaximumDistance -> Fix64). Three primitives, all data-derivable (audit +
// civilianbuildings.ini:2428-2433, the only live usage in the corpus):
//   1. periodic random-pick spawn (Civilian pool, Context.GameLogicRandom.Next, S3) - fully modeled;
//   2. MaximumDistance distance cap - PARSED, ENFORCEMENT UNMODELED (F-CSU-1: no move-order
//      surface exists for a spawned civilian to wander away from this module's own
//      always-at-donor-position spawn point - see spec §1a, same "parked not invented" posture
//      PickupStuffUpdate's own header already establishes for the identical missing primitive);
//   3. RunToFilter flee-target - SELECTION modeled (lowest-ObjectId live match within
//      MaximumDistance, same modeling choice PickupStuffUpdate makes for "nearest" - no
//      GameObject-to-GameObject distance primitive exists, D-7), MOVE unmodeled (F-CSU-2, same
//      unported-primitive reason as (2)). RunToFilter's two template-name Include entries hit
//      ObjectFilter.Matches's own pre-existing, already-documented gap (Matches never consults
//      IncludeThings/ExcludeThings - see AttributeModifierAuraUpdateModuleData.Filter's own doc
//      comment) - NOT fixed here (shared frozen primitive, out of this task's scope, filed as a
//      name reservation, not silently patched).
//
// Every mutable sim field appears in Xfer exactly once.

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class CivilianSpawnUpdate : UpdateModule
{
    private readonly CivilianSpawnUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>How many civilians this spawner has created.</summary>
    private int _numSpawned;

    public CivilianSpawnUpdate(GameObject gameObject, ISimContext context, CivilianSpawnUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        SetWakeFrame(
            _data.SpawnDelayTime.Value > 0 && _data.Civilian.Length > 0
                ? UpdateSleepTime.Frames(_data.SpawnDelayTime)
                : UpdateSleepTime.Forever);
    }

    public int NumSpawned => _numSpawned;

    public override UpdateSleepTime Update()
    {
        // §1 primitive 1: periodic random-pick spawn.
        if (_data.Civilian.Length > 0)
        {
            var index = Context.GameLogicRandom.Next(0, _data.Civilian.Length - 1);
            var template = _data.Civilian[index]?.Value;
            if (template != null)
            {
                Context.GameLogic.CreateObjectAt(template, GameObject.Owner, GameObject);
                _numSpawned++;
            }
        }

        // §1a (F-CSU-1): MaximumDistance is parsed and available (below) but deliberately not
        // enforced here - see spec §1a for why an enforcement branch would be unreachable dead
        // code given this module's own always-at-donor-position spawn mechanic.

        return UpdateSleepTime.Frames(_data.SpawnDelayTime);
    }

    /// <summary>
    /// §1b (F-CSU-2): the SELECTION half of "flee toward the nearest RunToFilter match" - the
    /// lowest-ObjectId live object matching RunToFilter within MaximumDistance (the only radius
    /// value this module's data carries; "nearest" is unmodeled, no GameObject-to-GameObject
    /// distance primitive exists, D-7). Does NOT issue a move order (unmodeled, no ISimContext
    /// member exists, matching PickupStuffUpdate's own filed move-order gap). Returns false
    /// (target left default) when RunToFilter is unset, nothing is in range, or nothing matches -
    /// callable and testable, not wired to anything upstream yet.
    /// </summary>
    public bool TryFindRunToTarget(out ObjectId target)
    {
        target = default;

        if (_data.RunToFilter == null || _data.MaximumDistance <= Fix64.Zero)
        {
            return false;
        }

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.MaximumDistance))
        {
            if (candidate == GameObject || candidate.IsDestroyed || candidate.IsEffectivelyDead)
            {
                continue;
            }
            if (_data.RunToFilter.Matches(candidate))
            {
                target = candidate.Id;
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
        xfer.XferInt("NumSpawned", ref _numSpawned);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

[SimDataAudited]
[AddedIn(SageGame.Bfme)]
public sealed class CivilianSpawnUpdateModuleData : UpdateModuleData
{
    internal static CivilianSpawnUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<CivilianSpawnUpdateModuleData> FieldParseTable = new IniParseTable<CivilianSpawnUpdateModuleData>
    {
        { "SpawnDelayTime", (parser, x) => x.SpawnDelayTime = parser.ParseDurationLogicFrames() },
        { "MaximumDistance", (parser, x) => x.MaximumDistance = parser.ParseFix64() },
        { "RunToFilter", (parser, x) => x.RunToFilter = ObjectFilter.Parse(parser) },
        { "Civilian", (parser, x) => x.Civilian = parser.ParseObjectReferenceArray() },
    };

    /// <summary>Frames between spawns (ms in INI, ceil-quantized at parse, S5 finding — was
    /// ParseInteger/int).</summary>
    public LogicFrameSpan SpawnDelayTime { get; private set; }

    /// <summary>Parsed and stored (Fix64, S5-class finding — was ParseInteger/int); doubles as
    /// the RunToFilter search radius (§1b) since no second radius field exists on this module.
    /// Enforcement as a wander cap is unmodeled (F-CSU-1, §1a).</summary>
    public Fix64 MaximumDistance { get; private set; }

    /// <summary>Selection-only (F-CSU-2, §1b): callers get a matching, in-range object via
    /// <see cref="CivilianSpawnUpdate.TryFindRunToTarget"/>, never a move order. Note: the live
    /// AotR usage's two Include entries are template names, which
    /// <see cref="ObjectFilter.Matches"/> does not consult (pre-existing, shared gap — not
    /// fixed by this port).</summary>
    public ObjectFilter RunToFilter { get; private set; }

    /// <summary>The spawn pool (audit's fix — was BitArray&lt;ObjectKinds&gt;, wrong type for
    /// the object-template names the live data actually carries). One entry is drawn uniformly
    /// per cadence tick via the logic RNG (S3).</summary>
    public LazyAssetReference<ObjectDefinition>[] Civilian { get; private set; } = System.Array.Empty<LazyAssetReference<ObjectDefinition>>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new CivilianSpawnUpdate(gameObject, gameEngine.SimContext, this);
    }
}
