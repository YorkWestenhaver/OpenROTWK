using System;
using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

/// <summary>
/// BFME self-build/rebuild driver. Lives directly on the structure it is building (not on a
/// separate GPL-style RebuildHoleBehavior hole object - confirmed by every AotR usage and by
/// <see cref="Castle.CastleUnpackStamper"/>'s own "starts the BFME self-build (GettingBuiltBehavior)
/// path" comment). Drives construction either via a spawned <see cref="WorkerAIUpdate"/> build
/// target (the same <see cref="DozerAndWorkerState.UpdateBuildTarget"/> mechanism
/// <see cref="RebuildHoleUpdate"/> exercises) or, per <c>UseSpawnTimerWithoutWorker</c>, by ticking
/// <see cref="GameObject.AdvanceConstruction"/> itself on a <c>SpawnTimer</c>-second cadence.
/// See R13 port spec (bfme2-workbench/research/modules-r13/specs/GettingBuiltBehaviorModuleData.md)
/// for the full behavioral derivation and findings F-GBB-1..4.
/// </summary>
public sealed class GettingBuiltBehavior : UpdateModule, IDamageModule
{
    private readonly GettingBuiltBehaviorModuleData _data;

    private ObjectId _workerId; // spawned construction worker; Invalid = none/self-build
    private LogicFrameSpan _framesUntilWorkerAction; // SpawnTimer countdown: respawn delay OR self-tick interval (§1.3)
    private bool _isRebuilding; // true only while restarting from Rubble (F-GBB-3 pacing branch)

    // F-GBB-1: WorkerName selected unconditionally today; EvilWorkerName is parsed/stored but the
    // faction predicate that would pick it is not pinned (filed, not guessed).
    private string _workerObjectName;

    private ObjectDefinition WorkerObjectDefinition =>
        GameEngine.AssetLoadContext.AssetStore.ObjectDefinitions.GetByName(_workerObjectName);

    internal GettingBuiltBehavior(GameObject gameObject, IGameEngine gameEngine, GettingBuiltBehaviorModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _data = moduleData;
        _workerObjectName = moduleData.WorkerName;

        // Worker path: SpawnTimer only ever gates a *respawn* after the assigned worker dies
        // (§1.3) - the very first spawn always happens at the first opportunity, so the countdown
        // starts pre-elapsed. No-worker self-tick path: SpawnTimer *is* the self-tick cadence
        // itself, so counting starts immediately from the full interval.
        _framesUntilWorkerAction = string.IsNullOrEmpty(_workerObjectName) ? SpawnTimerOrDisabledSentinel() : LogicFrameSpan.Zero;

        // GameObject.IsBeingConstructed() is already true here whenever
        // CastleUnpackStamper.StartSelfBuild ran before this module's ctor (the confirmed caller,
        // spec §1.2) - CreateObject builds BehaviorModules as part of object construction, so this
        // module observes whatever the caller already set. Nothing else to do at creation;
        // Update() takes over. A map-placed, already-complete structure never has
        // IsBeingConstructed() == true here, so this ctor is a no-op for it.
    }

    private LogicFrameSpan SpawnTimerOrDisabledSentinel() =>
        // SpawnTimer < 0 ("no autoheal") is captured as WorkerRespawnDisabled on the ModuleData
        // rather than folded into a LogicFrameSpan sentinel - LogicFrameSpan is unsigned, and
        // Zero already means "now", not "never" (spec §1.3 / ModuleData.ParseSpawnTimer). A huge
        // (but finite) frame count stands in for "never" so the countdown can still use ordinary
        // decrement/compare rather than a second branch everywhere it is read.
        _data.WorkerRespawnDisabled ? new LogicFrameSpan(uint.MaxValue) : _data.SpawnTimer;

    private void ResetWorkerTimer() => _framesUntilWorkerAction = SpawnTimerOrDisabledSentinel();

    /// <summary>
    /// Decrements the shared SpawnTimer countdown (§1.3) and returns true exactly on the call
    /// where it reaches zero - the call that should act (self-tick advance, or worker (re)spawn).
    /// A timer already at zero (default/unset SpawnTimer, or the worker path's pre-elapsed ctor
    /// value) fires immediately, without decrementing.
    /// </summary>
    private bool TickSpawnTimer()
    {
        if (_framesUntilWorkerAction != LogicFrameSpan.Zero)
        {
            _framesUntilWorkerAction--;
        }

        return _framesUntilWorkerAction == LogicFrameSpan.Zero;
    }

    public override UpdateSleepTime Update()
    {
        if (!GameObject.IsBeingConstructed())
        {
            // Not building/rebuilding: nothing to drive. Also covers F-GBB-4's no-driver
            // combination (WorkerName empty, UseSpawnTimerWithoutWorker unset/No): that
            // combination never leaves IsBeingConstructed() true via any driver this module owns.
            return UpdateSleepTime.None;
        }

        if (_isRebuilding)
        {
            // F-GBB-3: RebuildTimeSeconds paces every frame, unconditionally - the same
            // unconditional per-frame percentage idiom RebuildHoleUpdate's own
            // _healPercentagePerFrame uses - independent of the worker-respawn/self-tick cadence
            // below, which only ever paces the *initial* (non-Rubble) build.
            AdvanceRebuildProgress();
        }

        var result = string.IsNullOrEmpty(_workerObjectName) ? UpdateNoWorker() : UpdateWithWorker();

        if (!GameObject.IsBeingConstructed())
        {
            _isRebuilding = false;
        }

        return result;
    }

    private UpdateSleepTime UpdateNoWorker()
    {
        if (_isRebuilding)
        {
            return UpdateSleepTime.None; // already paced above; no self-tick driver during a rebuild
        }

        if (!_data.UseSpawnTimerWithoutWorker)
        {
            // F-GBB-4: no authored driver for this shape - track state, add no unauthored advance.
            return UpdateSleepTime.None;
        }

        if (!TickSpawnTimer())
        {
            return UpdateSleepTime.None; // still waiting for the next self-tick interval
        }

        GameObject.AdvanceConstruction(); // §1.3: SpawnTimer as the self-tick interval
        _framesUntilWorkerAction = _data.SpawnTimer;
        return UpdateSleepTime.None;
    }

    private UpdateSleepTime UpdateWithWorker()
    {
        var worker = _workerId.IsInvalid ? null : GameEngine.GameLogic.GetObjectById(_workerId);

        if (worker == null || worker.IsEffectivelyDead)
        {
            if (!_workerId.IsInvalid)
            {
                // Transition edge: the worker we had just died. Arm the SpawnTimer-frame respawn
                // delay (§1.3) - a fresh module (never had a worker yet) starts this timer
                // pre-elapsed via the ctor instead, so its very first spawn is immediate.
                _workerId = ObjectId.Invalid;
                ResetWorkerTimer();
            }

            if (!TickSpawnTimer())
            {
                return UpdateSleepTime.None; // still waiting to (re)spawn
            }

            worker = GameEngine.GameLogic.CreateObject(WorkerObjectDefinition, GameObject.Owner);
            worker.SetTransformMatrix(GameObject.TransformMatrix);
            worker.SetSelectable(false); // matches RebuildHoleUpdate.cs:102 precedent
            _workerId = worker.Id;
        }

        if (worker.AIUpdate is WorkerAIUpdate workerAiUpdate && workerAiUpdate.BuildTarget != GameObject)
        {
            workerAiUpdate.SetBuildTarget(GameObject); // §1.2: target is self, unlike RebuildHoleUpdate
        }
        // else: DozerAndWorkerState.UpdateBuildTarget already calls GameObject.AdvanceConstruction()
        // for the initial-build path (spec §1.2) once the worker is assigned - this module does
        // not duplicate that call; during a rebuild, Update()'s unconditional AdvanceRebuildProgress
        // call above is the sole BuildProgress writer (F-GBB-3), so the worker only carries its own
        // approach/anim state, not the exact reading DozerAndWorkerState would otherwise drive.

        if (!GameObject.IsBeingConstructed())
        {
            // Construction finished this tick (either driver): release the worker.
            worker.Destroy();
            _workerId = ObjectId.Invalid;
        }

        return UpdateSleepTime.None;
    }

    private void AdvanceRebuildProgress()
    {
        var increment = 1.0f / (_data.RebuildTimeSeconds * GameEngine.LogicFramesPerSecond);
        var lastProgress = GameObject.BuildProgress;
        GameObject.BuildProgress = Math.Clamp(lastProgress + increment, 0.0f, 1.0f);
        GameObject.AttemptHealing((GameObject.BuildProgress - lastProgress) * GameObject.BodyModule.MaxHealth, GameObject);

        if (GameObject.BuildProgress >= 1.0f)
        {
            GameObject.FinishConstruction();
        }
    }

    // IDamageModule (Damage/DamageModule.cs) - the RebuildWhenDead trigger, dispatched by
    // ActiveBody whenever DamageState changes (spec §1.2). No IDieModule: RUBBLE structures never
    // call Kill()/die (spec §1.2, matching every AotR ini neighbor's own comment on the subject).
    public void OnBodyDamageStateChange(in DamageInfo damageInfo, BodyDamageType oldState, BodyDamageType newState)
    {
        if (newState != BodyDamageType.Rubble || !_data.RebuildWhenDead)
        {
            return;
        }

        // F-GBB-2: DisallowRebuildRange/Filter gate - skip restarting this tick if a matching
        // object already occupies the exclusion radius. No oracle usage exercises this (0 AotR
        // occurrences); re-evaluated every OnBodyDamageStateChange call while still Rubble and not
        // yet restarted, the cheapest reading with no unauthored side effect.
        if (_data.DisallowRebuildRange > 0 && DisallowedObjectInRange())
        {
            return;
        }

        // Same terrain guard as the confirmed initial-build caller, CastleUnpackStamper.StartSelfBuild
        // (spec §1.2): PrepareConstruction unconditionally touches the terrain heightmap, which a
        // headless host does not stand up. The ModelConditionFlags/BuildProgress reset below (what
        // IsBeingConstructed/rebuild pacing read) happens either way.
        if (GameEngine.Terrain != null)
        {
            GameObject.PrepareConstruction();
        }

        GameObject.SetIsBeingConstructed();
        GameObject.BuildProgress = 0.0f;
        _isRebuilding = true;
        _workerId = ObjectId.Invalid;
        ResetWorkerTimer();
    }

    private bool DisallowedObjectInRange()
    {
        foreach (var candidate in GameEngine.Quadtree.FindNearby(GameObject, GameObject.Transform, _data.DisallowRebuildRange))
        {
            if (_data.DisallowRebuildFilter?.Matches(candidate) == true)
            {
                return true;
            }
        }

        return false;
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistObjectId(ref _workerId);
        reader.PersistLogicFrameSpan(ref _framesUntilWorkerAction);
        reader.PersistBoolean(ref _isRebuilding);
        reader.PersistAsciiString(ref _workerObjectName);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class GettingBuiltBehaviorModuleData : BehaviorModuleData
{
    internal static GettingBuiltBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<GettingBuiltBehaviorModuleData> FieldParseTable = new IniParseTable<GettingBuiltBehaviorModuleData>
    {
        { "WorkerName", (parser, x) => x.WorkerName = parser.ParseString() },
        { "SelfBuildingLoop", (parser, x) => x.SelfBuildingLoop = parser.ParseString() },
        { "SelfRepairFromDamageLoop", (parser, x) => x.SelfRepairFromDamageLoop = parser.ParseString() },
        { "SelfRepairFromRubbleLoop", (parser, x) => x.SelfRepairFromRubbleLoop = parser.ParseString() },
        { "SpawnTimer", (parser, x) => ParseSpawnTimer(parser, x) },
        { "RebuildTimeSeconds", (parser, x) => x.RebuildTimeSeconds = parser.ParseFloat() },
        { "RebuildWhenDead", (parser, x) => x.RebuildWhenDead = parser.ParseBoolean() },
        { "EvilWorkerName", (parser, x) => x.EvilWorkerName = parser.ParseString() },
        { "TestFaction", (parser, x) => x.TestFaction = parser.ParseBoolean() },
        { "UseSpawnTimerWithoutWorker", (parser, x) => x.UseSpawnTimerWithoutWorker = parser.ParseBoolean() },
        { "DisallowRebuildRange", (parser, x) => x.DisallowRebuildRange = parser.ParseInteger() },
        { "DisallowRebuildFilter", (parser, x) => x.DisallowRebuildFilter = ObjectFilter.Parse(parser) },
    };

    private static void ParseSpawnTimer(IniParser parser, GettingBuiltBehaviorModuleData x)
    {
        var seconds = parser.ParseFloat();

        // §1.3: negative == "no autoheal"/no worker respawn - a sign, not a duration; captured as
        // its own bool rather than folding into a LogicFrameSpan sentinel (LogicFrameSpan is
        // unsigned; time fields are int/frame-count, never signed-via-hack).
        x.WorkerRespawnDisabled = seconds < 0f;
        x.SpawnTimer = x.WorkerRespawnDisabled
            ? LogicFrameSpan.Zero
            : LogicFrameSpan.FromSeconds(seconds, parser.SageGame.LogicFramesPerSecond());
    }

    public string WorkerName { get; private set; }
    public string SelfBuildingLoop { get; private set; }
    public string SelfRepairFromDamageLoop { get; private set; }
    public string SelfRepairFromRubbleLoop { get; private set; }
    public LogicFrameSpan SpawnTimer { get; private set; }
    public bool WorkerRespawnDisabled { get; private set; } // derived from SpawnTimer's sign, §1.3
    public float RebuildTimeSeconds { get; private set; } // F-GBB-3: consumed as raw seconds, own pacing loop
    public bool RebuildWhenDead { get; private set; }
    public string EvilWorkerName { get; private set; } // F-GBB-1: parsed/stored, not yet selected
    public bool TestFaction { get; private set; } // F-GBB-1

    [AddedIn(SageGame.Bfme2)]
    public bool UseSpawnTimerWithoutWorker { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public int DisallowRebuildRange { get; private set; } // F-GBB-2

    [AddedIn(SageGame.Bfme2)]
    public ObjectFilter DisallowRebuildFilter { get; private set; } // F-GBB-2

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new GettingBuiltBehavior(gameObject, gameEngine, this);
    }
}
