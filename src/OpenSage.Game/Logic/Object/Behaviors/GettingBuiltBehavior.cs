// GettingBuiltBehavior - R12 port. BFME-only (no generals-gpl sibling) and no clean-room
// spec in bfme2-workbench/research/ (searched: no spec-getting-built*.md), so this ports
// the sim-observable slice the task packet describes and leaves the presentation-only
// vocabulary parsed but unconsumed (TODO-spec, same posture as EmotionTrackerUpdate and
// AutoHealBehavior's audited-but-inert BFME2 fields):
//
//   - WorkerName / EvilWorkerName: a flyweight is only ever authored with one of the two
//     for a given faction's structure, so the effective template name is simply "prefer
//     EvilWorkerName when set" (no side/faction lookup exists on this seam - a generic
//     Good/Evil player predicate is not part of ISimContext or Player today).
//   - SpawnTimer / RebuildTimeSeconds: the two construction-duration clocks - SpawnTimer
//     drives the first build (started externally by CastleUnpackStamper.StartSelfBuild /
//     BuildOnFoundation, which already flips the AwaitingConstruction/ActivelyBeingConstructed
//     model condition and zeroes BuildProgress before this module ever ticks), RebuildTimeSeconds
//     drives a rebuild-from-rubble restart.
//   - RebuildWhenDead: gates whether reaching BodyDamageType.Rubble (ActiveBody's zero-health
//     state, OnBodyDamageStateChange) restarts the timer in place, rather than leaving the
//     structure permanently rubbled.
//   - DisallowRebuildRange / DisallowRebuildFilter (BFME2): before a rubble restart actually
//     begins, a DisallowRebuildFilter match within DisallowRebuildRange blocks it; the check
//     re-polls every frame while blocked (no cadence field is authored for this poll, so it
//     re-checks every logic frame, matching "no field means no throttle" elsewhere in this
//     batch, e.g. RebuildHoleUpdate's own per-frame Update()).
//   - UseSpawnTimerWithoutWorker: when false (default) and no worker template is configured
//     (or the spawned worker has died), the completion clock does not advance - construction
//     stalls until a worker exists; when true the clock runs unconditionally.
//   - SelfBuildingLoop / SelfRepairFromDamageLoop / SelfRepairFromRubbleLoop / TestFaction:
//     draw-layer condition-state selection (which animation clip plays while
//     ActivelyBeingConstructed combines with Damaged/ReallyDamaged/Rubble, all of which are
//     ALREADY engine model-condition flags driven by ActiveBody) - client-side, not sim
//     state, so parsed and kept for the draw layer but not acted on here (TODO-spec).
//
// Construction completion reuses GameObject.FinishConstruction() (the same completion path
// the classic dozer/AdvanceConstruction system uses), so "transitions to normal gameplay
// state" (ICreateModule.OnBuildComplete, EnergyProduction, clearing the construction model
// conditions) is the one shared implementation rather than a second copy here.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class GettingBuiltBehavior : UpdateModule, IDamageModule
{
    private readonly GettingBuiltBehaviorModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>The spawned self-build worker, or <see cref="ObjectId.Invalid"/>.</summary>
    private ObjectId _workerId;

    /// <summary>Whether a construction (first build or rubble rebuild) clock is running.</summary>
    private bool _active;

    /// <summary>True while the running clock is the rubble-restart clock (RebuildTimeSeconds)
    /// rather than the first-build clock (SpawnTimer).</summary>
    private bool _rebuilding;

    /// <summary>Frame the running clock completes construction, valid only while <see cref="_active"/>.</summary>
    private LogicFrame _completionFrame;

    /// <summary>Reached BodyDamageType.Rubble with RebuildWhenDead pending a
    /// DisallowRebuildFilter/-Range clearance before the rebuild clock can start.</summary>
    private bool _rebuildBlocked;

    public GettingBuiltBehavior(GameObject gameObject, ISimContext context, GettingBuiltBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // The construction start (PrepareConstruction/SetIsBeingConstructed) is driven
        // externally (CastleUnpackStamper) and may not have run yet at module-construction
        // time (module list is built before the spawner flips the construction flags), so
        // the first Update() tick is where we actually detect it and arm the clock.
        SetWakeFrame(UpdateSleepTime.None);
    }

    // ---- public surface (tests / future draw-layer seams) ----

    public ObjectId WorkerId => _workerId;

    public bool IsConstructionActive => _active;

    public bool IsRebuilding => _rebuilding;

    public bool IsRebuildBlocked => _rebuildBlocked;

    public LogicFrame CompletionFrame => _completionFrame;

    /// <summary>WorkerName for good factions, EvilWorkerName for evil ones. Retail authors
    /// exactly one of the two per structure flyweight (see file header); EvilWorkerName wins
    /// when both are somehow present.</summary>
    public string EffectiveWorkerName =>
        !string.IsNullOrEmpty(_data.EvilWorkerName) ? _data.EvilWorkerName : _data.WorkerName;

    public override UpdateSleepTime Update()
    {
        if (GameObject.IsEffectivelyDead)
        {
            return UpdateSleepTime.Forever;
        }

        if (_rebuildBlocked)
        {
            if (RebuildBlockedByNearbyObjects())
            {
                return UpdateSleepTime.None;
            }

            _rebuildBlocked = false;
            StartConstructionClock(rebuild: true);
        }

        if (!_active)
        {
            // Nothing running yet: detect an externally-started first build.
            if (GameObject.IsBeingConstructed())
            {
                StartConstructionClock(rebuild: false);
            }
            else
            {
                return UpdateSleepTime.Forever;
            }
        }

        EnsureWorker();

        if (!_data.UseSpawnTimerWithoutWorker && WorkerMissing())
        {
            // Stalled: keep retrying the worker spawn, but the clock does not advance.
            return UpdateSleepTime.None;
        }

        if (Context.CurrentFrame < _completionFrame)
        {
            return UpdateSleepTime.Frames(_completionFrame - Context.CurrentFrame);
        }

        CompleteConstruction();
        return UpdateSleepTime.Forever;
    }

    public void OnDamage(in DamageInfo damageInfo)
    {
        // Damage taken while a clock is running extends it (SelfRepairFromDamageLoop, per
        // the task summary); no extension-amount field is authored, so the extension is the
        // running clock's own full duration (TODO-spec: the retail formula is unverified).
        if (!_active)
        {
            return;
        }

        var extension = _rebuilding ? _data.RebuildTimeSeconds : _data.SpawnTimer;
        _completionFrame += extension;
        SetWakeFrame(UpdateSleepTime.Frames(_completionFrame - Context.CurrentFrame));
    }

    public void OnBodyDamageStateChange(in DamageInfo damageInfo, BodyDamageType oldState, BodyDamageType newState)
    {
        if (newState != BodyDamageType.Rubble || oldState == BodyDamageType.Rubble)
        {
            return;
        }

        // Reached rubble: the running clock (if any) is moot, and any worker stops building.
        _active = false;
        _rebuilding = false;
        DestroyWorker();

        if (_data.RebuildWhenDead && !GameObject.IsEffectivelyDead)
        {
            _rebuildBlocked = true;
            SetWakeFrame(UpdateSleepTime.None);
        }
        else
        {
            _rebuildBlocked = false;
            SetWakeFrame(UpdateSleepTime.Forever);
        }
    }

    private void StartConstructionClock(bool rebuild)
    {
        _active = true;
        _rebuilding = rebuild;
        var duration = rebuild ? _data.RebuildTimeSeconds : _data.SpawnTimer;
        _completionFrame = Context.CurrentFrame + duration;

        if (rebuild)
        {
            GameObject.SetIsBeingConstructed();
        }

        EnsureWorker();
        // No SetWakeFrame here: this always runs from inside Update() (either directly, or
        // via the _rebuildBlocked branch earlier in the same call), so the sleepy queue is
        // rescheduled by Update()'s own return value, same as every other ported module.
    }

    private void CompleteConstruction()
    {
        DestroyWorker();
        _active = false;
        _rebuilding = false;
        GameObject.FinishConstruction();
    }

    private bool WorkerMissing()
    {
        if (string.IsNullOrEmpty(EffectiveWorkerName))
        {
            return true;
        }

        if (_workerId.IsInvalid)
        {
            return true;
        }

        var worker = Context.GameLogic.GetObjectById(_workerId);
        return worker == null || worker.IsEffectivelyDead;
    }

    private void EnsureWorker()
    {
        var workerName = EffectiveWorkerName;
        if (string.IsNullOrEmpty(workerName))
        {
            return;
        }

        if (_workerId.IsValid)
        {
            var existing = Context.GameLogic.GetObjectById(_workerId);
            if (existing != null && !existing.IsEffectivelyDead)
            {
                return;
            }

            _workerId = ObjectId.Invalid;
        }

        var definition = Context.Assets.GetObjectDefinition(workerName);
        if (definition == null)
        {
            return;
        }

        var worker = Context.GameLogic.CreateObjectAt(definition, GameObject.Owner, GameObject);
        _workerId = worker?.Id ?? ObjectId.Invalid;
    }

    private void DestroyWorker()
    {
        if (_workerId.IsInvalid)
        {
            return;
        }

        var worker = Context.GameLogic.GetObjectById(_workerId);
        if (worker != null)
        {
            Context.GameLogic.DestroyObject(worker);
        }

        _workerId = ObjectId.Invalid;
    }

    /// <summary>BFME2 DisallowRebuildFilter/-Range: any matching object within range blocks
    /// the rebuild restart. No filter or a non-positive range means never blocked.</summary>
    private bool RebuildBlockedByNearbyObjects()
    {
        if (_data.DisallowRebuildFilter == null || _data.DisallowRebuildRange <= Fix64.Zero)
        {
            return false;
        }

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.DisallowRebuildRange))
        {
            if (candidate == GameObject || candidate.IsDestroyed || candidate.IsEffectivelyDead)
            {
                continue;
            }

            if (_data.DisallowRebuildFilter.Matches(candidate))
            {
                return true;
            }
        }

        return false;
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferObjectId("WorkerId", ref _workerId);
        xfer.XferBool("Active", ref _active);
        xfer.XferBool("Rebuilding", ref _rebuilding);
        xfer.XferBool("RebuildBlocked", ref _rebuildBlocked);
        xfer.XferFrame("CompletionFrame", ref _completionFrame, Tolerance.Quantum);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class GettingBuiltBehaviorModuleData : UpdateModuleData
{
    internal static GettingBuiltBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<GettingBuiltBehaviorModuleData> FieldParseTable = new IniParseTable<GettingBuiltBehaviorModuleData>
    {
        { "WorkerName", (parser, x) => x.WorkerName = parser.ParseString() },
        { "SelfBuildingLoop", (parser, x) => x.SelfBuildingLoop = parser.ParseString() },
        { "SelfRepairFromDamageLoop", (parser, x) => x.SelfRepairFromDamageLoop = parser.ParseString() },
        { "SelfRepairFromRubbleLoop", (parser, x) => x.SelfRepairFromRubbleLoop = parser.ParseString() },
        { "SpawnTimer", (parser, x) => x.SpawnTimer = parser.ParseDurationLogicFramesSeconds() },
        { "RebuildTimeSeconds", (parser, x) => x.RebuildTimeSeconds = parser.ParseDurationLogicFramesSeconds() },
        { "RebuildWhenDead", (parser, x) => x.RebuildWhenDead = parser.ParseBoolean() },
        { "EvilWorkerName", (parser, x) => x.EvilWorkerName = parser.ParseString() },
        { "TestFaction", (parser, x) => x.TestFaction = parser.ParseBoolean() },
        { "UseSpawnTimerWithoutWorker", (parser, x) => x.UseSpawnTimerWithoutWorker = parser.ParseBoolean() },
        { "DisallowRebuildRange", (parser, x) => x.DisallowRebuildRange = new Fix64(parser.ParseInteger()) },
        { "DisallowRebuildFilter", (parser, x) => x.DisallowRebuildFilter = ObjectFilter.Parse(parser) }
    };

    public string WorkerName { get; private set; }

    /// <summary>Draw-layer condition-state name; not consumed here (see file header TODO-spec).</summary>
    public string SelfBuildingLoop { get; private set; }

    /// <summary>Draw-layer condition-state name; not consumed here (see file header TODO-spec).</summary>
    public string SelfRepairFromDamageLoop { get; private set; }

    /// <summary>Draw-layer condition-state name; not consumed here (see file header TODO-spec).</summary>
    public string SelfRepairFromRubbleLoop { get; private set; }

    /// <summary>First-build construction duration (seconds in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan SpawnTimer { get; private set; }

    /// <summary>Rubble-restart construction duration (seconds in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan RebuildTimeSeconds { get; private set; }

    public bool RebuildWhenDead { get; private set; }

    public string EvilWorkerName { get; private set; }

    /// <summary>Audited, unconsumed (no behavioral fact recovered for this flag).</summary>
    public bool TestFaction { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool UseSpawnTimerWithoutWorker { get; private set; }

    /// <summary>World-unit rebuild-block scan radius (quantized Q31.32 at parse; the INI value
    /// is a plain integer, never fractional, so this is exact).</summary>
    [AddedIn(SageGame.Bfme2)]
    public Fix64 DisallowRebuildRange { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public ObjectFilter DisallowRebuildFilter { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new GettingBuiltBehavior(gameObject, gameEngine.SimContext, this);
    }
}
