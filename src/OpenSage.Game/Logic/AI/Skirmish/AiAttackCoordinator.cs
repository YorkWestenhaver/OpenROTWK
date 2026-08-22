#nullable enable

// S9-09 (R15 L3): AiAttackCoordinator v1 - the manager that makes the AI actually attack.
//
// This is the dr-0039 M-d criterion in one file: ">= 1 wave launched, engagements > 0". S9-08
// makes units and forms them into teams; if this manager does nothing, those teams stand in the
// base at Ready forever and the match is a build-off.
//
// THE KEY DESIGN CHOICE: THE COORDINATOR OWNS THE ENGAGE LOOP
//
// The obvious implementation is "order the team to attack-move at the enemy base and let the
// per-object AI take it from there". That is NOT what this does, and the difference is the whole
// reason the packet exists. The engine's attack-move path (AIUpdate's AttackMoveState) is a
// shell on this fork - Part I of design-aiupdate.md, the per-object AIUpdate rework, is
// explicitly deferred (ruling S9-R15-B) - so a wave handed to it would walk somewhere and then
// stop, and every symptom would look like "the AI attacks but never kills anything".
//
// So the coordinator re-scans and re-issues EXPLICIT AttackObject orders itself, on a cadence,
// through the one order path this fork has actually exercised end-to-end
// (AiOrderEmitter -> SetSelection + AttackObject -> OrderProcessor). Nothing here depends on a
// module port landing, and when Part I does land this loop can be thinned rather than rewritten:
// the re-issue cadence becomes longer, the wave machine below stays.
//
// WAVE LIFECYCLE
//
//   (no wave)  --cadence + a free Ready team + a legal target-->  Engaging
//   Engaging   --re-scan every ReissueInterval, or target died-->  Engaging (retarget)
//   Engaging   --lost RetreatAtPercentOfPeak of peak strength -->  Mustering (team -> Retreating)
//   Mustering  --team regrouped to Ready, still big enough    -->  Engaging (relaunch)
//   Mustering  --team regrouped to Ready, too small           -->  Ended (team disbanded,
//                                                                   members re-recruited by
//                                                                   AiTeamManager next frame)
//   any        --team wiped or disbanded under us             -->  Ended
//
// The retreat->muster->disband/re-recruit arc is deliberately expressed as coordinator state
// plus calls into AiTeamManager's existing transitions rather than as new state on AiTeam: the
// team machine is S9-08's and stays exactly as it shipped.
//
// DETERMINISM
//
// Cadence is frame arithmetic on IAiWorldView.CurrentFrame (no wall clock). Team selection is
// lowest team id. Target selection is AiTargetScoring, which is a pure total order. Orders go
// out through AiOrderEmitter, which normalizes and sorts actor ids. Two peers with the same
// snapshot emit the same order stream.
//
// CLEAN-ROOM: every constant below is a v1 heuristic chosen to make wave behaviour observable
// inside a gate-length run. None of it is recovered retail tuning. TODO S9-11: these are the
// numbers that packet should lift out of C# constants and into the mod's own data.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>What an <see cref="AiWave"/> is currently doing.</summary>
public enum AiWaveState
{
    /// <summary>Moving on and attacking a chosen target; re-scanned on a cadence.</summary>
    Engaging,

    /// <summary>Pulled back to the muster point after losses; waiting for its team to regroup.</summary>
    Mustering,

    /// <summary>Terminal. The coordinator compacts it away at the end of the frame.</summary>
    Ended,
}

/// <summary>
/// One attack in progress: a team, the target it is currently on, and the bookkeeping the
/// coordinator needs to decide when to re-scan or pull back.
/// </summary>
public sealed class AiWave
{
    /// <summary>Match-unique, monotonically increasing wave id. Trace text and stable identity.</summary>
    public int Id { get; }

    /// <summary>The team executing this wave. Owned exclusively: one wave per team.</summary>
    public AiTeam Team { get; }

    /// <summary>Current state.</summary>
    public AiWaveState State { get; internal set; } = AiWaveState.Engaging;

    /// <summary>The object the wave is currently ordered onto.</summary>
    public ObjectId TargetId { get; internal set; }

    /// <summary>Priority class the current target scored in. Trace only.</summary>
    public AiAttackPriority TargetPriority { get; internal set; }

    /// <summary>Frame the wave launched on.</summary>
    public uint LaunchedFrame { get; }

    /// <summary>Frame the coordinator last emitted an attack order for this wave.</summary>
    public uint LastOrderFrame { get; internal set; }

    /// <summary>Frame the wave last changed <see cref="State"/>.</summary>
    public uint StateSinceFrame { get; internal set; }

    /// <summary>Largest member count this wave has held. The retreat threshold is a fraction of it.</summary>
    public int PeakSize { get; internal set; }

    /// <summary>Attack orders emitted for this wave, including retargets.</summary>
    public int OrdersIssued { get; internal set; }

    /// <summary>Times this wave changed target.</summary>
    public int Retargets { get; internal set; }

    internal AiWave(int id, AiTeam team, uint frame)
    {
        Id = id;
        Team = team;
        LaunchedFrame = frame;
        StateSinceFrame = frame;
        PeakSize = team.Members.Count;
    }

    internal void MoveTo(AiWaveState state, uint frame)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateSinceFrame = frame;
    }
}

/// <summary>
/// v1 attack coordinator: schedules waves at a difficulty-scaled cadence, scores their targets,
/// and keeps them engaged by re-issuing explicit attack orders.
/// </summary>
public sealed class AiAttackCoordinator : IAiBrainManager
{
    /// <summary>Trace/report tag. Keep stable - the match report groups evidence on it.</summary>
    public const string ManagerName = "attack";

    /// <summary>
    /// Counter bumped when a new wave is launched. THE M-d "waves launched" grading key.
    /// </summary>
    public const string WaveLaunchedCounter = "attack.wave.launched";

    /// <summary>
    /// Counter bumped per attack order the coordinator emits (launch, retarget or re-issue).
    /// THE M-d "engagements" grading key.
    /// </summary>
    public const string EngageCounter = "attack.engage";

    /// <summary>Counter bumped when a wave switches to a different target.</summary>
    public const string RetargetCounter = "attack.retarget";

    /// <summary>Counter bumped when a wave is pulled back after losses.</summary>
    public const string RetreatCounter = "attack.retreat";

    /// <summary>Counter bumped when a retreating wave is sent to the muster point.</summary>
    public const string MusterCounter = "attack.muster";

    /// <summary>Counter bumped when a mustered wave is sent back in.</summary>
    public const string RelaunchCounter = "attack.relaunch";

    /// <summary>Counter bumped when a wave ends for any reason.</summary>
    public const string WaveEndedCounter = "attack.wave.ended";

    /// <summary>Counter bumped when a mustered wave's team is dissolved as too small to fight.</summary>
    public const string WaveDisbandedCounter = "attack.wave.disband";

    /// <summary>
    /// Counter bumped when the cadence and a free team were both ready but the snapshot held no
    /// legal target. Non-zero with zero launches is the signature of "the AI wants to attack and
    /// cannot see anybody", which is a very different bug from "the AI never tries".
    /// </summary>
    public const string NoTargetCounter = "attack.notarget";

    /// <summary>
    /// Percent of its peak size a wave may fall to before it is pulled back. Int percent, not a
    /// float fraction, matching the int-only arithmetic rule the economy manager set.
    /// </summary>
    public const int RetreatAtPercentOfPeak = 50;

    /// <summary>
    /// Percent of the team's TARGET size a mustered wave must still have to be sent back in.
    /// Below it the team is dissolved and AiTeamManager re-recruits the survivors into a fresh
    /// team next frame, which is how a mauled army becomes a whole one again.
    /// </summary>
    public const int RelaunchAtPercentOfTargetSize = 50;

    private readonly AiOrderEmitter _emitter;
    private readonly AiTeamManager _teams;
    private readonly List<AiWave> _waves = new();
    private readonly uint _waveIntervalOverride;
    private readonly uint _reissueIntervalOverride;
    private readonly int _maxWavesOverride;

    private int _nextWaveId = 1;
    private uint _nextWaveFrame;
    private bool _disabledReported;

    /// <inheritdoc />
    public string Name => ManagerName;

    /// <summary>Waves in progress, in launch order. Ended waves are compacted out each frame.</summary>
    public IReadOnlyList<AiWave> Waves => _waves;

    /// <summary>Waves launched over this coordinator's life. The M-d number.</summary>
    public int WavesLaunched { get; private set; }

    /// <summary>Attack orders emitted over this coordinator's life.</summary>
    public int EngagementsOrdered { get; private set; }

    /// <summary>Waves that ended, for any reason.</summary>
    public int WavesEnded { get; private set; }

    /// <summary>Earliest frame the next wave may launch on.</summary>
    public uint NextWaveFrame => _nextWaveFrame;

    /// <param name="emitter">The brain's single order emitter (S9-04).</param>
    /// <param name="teams">The brain's team manager (S9-08); waves are built out of its teams.</param>
    /// <param name="waveIntervalOverride">
    /// Forces the wave cadence in frames, bypassing the difficulty scale. 0 (the default) means
    /// "use the difficulty"; tests launch a second wave in three frames instead of fifty seconds.
    /// </param>
    /// <param name="reissueIntervalOverride">Forces the re-scan cadence in frames. 0 means difficulty.</param>
    /// <param name="maxWavesOverride">Forces the concurrent-wave cap. Non-positive means difficulty.</param>
    public AiAttackCoordinator(
        AiOrderEmitter emitter,
        AiTeamManager teams,
        uint waveIntervalOverride = 0,
        uint reissueIntervalOverride = 0,
        int maxWavesOverride = 0)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentNullException.ThrowIfNull(teams);

        _emitter = emitter;
        _teams = teams;
        _waveIntervalOverride = waveIntervalOverride;
        _reissueIntervalOverride = reissueIntervalOverride;
        _maxWavesOverride = maxWavesOverride;
    }

    // ---- difficulty scaling ------------------------------------------------------------
    //
    // TODO S9-11: lift all three of these out of C# constants. SkirmishAIData ships the
    // DifficultyTuning block the economy lane already reads, and the attack cadence belongs
    // there next to it; these values exist so the lane can be graded before that plumbing does.
    // Deliberately NOT hidden behind a table: three switch expressions read as tuning, a table
    // reads as data and would invite somebody to think it came from the game.

    /// <summary>
    /// Frames between wave launches. Harder AIs attack more often; at the SAGE 30-frame logic
    /// second these are ~80s / ~50s / ~30s / ~20s.
    /// </summary>
    public static uint WaveIntervalFrames(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => 2400,
        Difficulty.Hard => 900,
        Difficulty.Brutal => 600,
        _ => 1500,
    };

    /// <summary>
    /// Frames between re-scans of an engaged wave's target. This is the engage loop's heartbeat:
    /// too long and a wave stands over a corpse, too short and the emitter's per-frame budget
    /// goes entirely on re-selection. ~5s / ~4s / ~3s / ~2s.
    /// </summary>
    public static uint ReissueIntervalFrames(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => 150,
        Difficulty.Hard => 90,
        Difficulty.Brutal => 60,
        _ => 120,
    };

    /// <summary>
    /// Waves allowed in flight at once. Also bounds the per-frame order cost of the engage loop.
    /// </summary>
    public static int MaxConcurrentWaves(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => 1,
        Difficulty.Hard => 3,
        Difficulty.Brutal => 4,
        _ => 2,
    };

    // ---- tick ---------------------------------------------------------------------------

    /// <summary>
    /// One frame: run the waves already in flight, then launch a new one if the cadence, the
    /// team pool and the enemy snapshot all allow it.
    /// </summary>
    /// <remarks>
    /// Running waves BEFORE launching is load-bearing, not stylistic: a mustered wave that
    /// regroups this frame either relaunches (its team leaves the Ready pool) or dissolves (its
    /// team disappears) during the first half, so the launch half can never hand a second wave
    /// to a team that already has one. The ownership check in <see cref="FindFreeReadyTeam"/> is
    /// belt-and-braces on top of that ordering.
    /// </remarks>
    public void Update(SkirmishAIBrain brain)
    {
        ArgumentNullException.ThrowIfNull(brain);

        var world = brain.World;
        var frame = world.CurrentFrame;

        // Mod-level off switch, same policy as AiBaseManager.DisableBaseBuilding and
        // AiTeamManager.DisableTeamBuilding. DisableTacticalAI is the block's own name for
        // "this mod does not want the engine picking fights".
        if (world.SkirmishAIData is { DisableTacticalAI: true })
        {
            if (!_disabledReported)
            {
                _disabledReported = true;
                Line(brain, string.Create(CultureInfo.InvariantCulture, $"f={frame} disabled=datatacticalai"));
            }

            return;
        }

        RunWaves(brain, frame);
        TryLaunchWave(brain, frame);
        Compact();
    }

    // ---- the engage loop -----------------------------------------------------------------

    private void RunWaves(SkirmishAIBrain brain, uint frame)
    {
        for (var w = 0; w < _waves.Count; w++)
        {
            var wave = _waves[w];

            if (wave.State == AiWaveState.Ended)
            {
                continue;
            }

            var team = wave.Team;

            // The team manager prunes dead members and disbands wiped teams before this manager
            // ticks (it is registered earlier), so an empty or disbanded team here means the wave
            // lost its army rather than that the snapshot is stale.
            if (team.IsDisbanded || team.Members.Count == 0)
            {
                End(brain, wave, frame, "lost");
                continue;
            }

            if (team.Members.Count > wave.PeakSize)
            {
                wave.PeakSize = team.Members.Count;
            }

            switch (wave.State)
            {
                case AiWaveState.Engaging:
                    RunEngaging(brain, wave, frame);
                    break;

                case AiWaveState.Mustering:
                    RunMustering(brain, wave, frame);
                    break;
            }
        }
    }

    private void RunEngaging(SkirmishAIBrain brain, AiWave wave, uint frame)
    {
        var world = brain.World;
        var team = wave.Team;

        // Either we decide the wave is too mauled, or the team manager already decided it (it
        // applies its own retreat rule to Tasked teams). Both land here so the wave is never left
        // Engaging over a team that is walking home.
        if (ShouldRetreat(wave) || team.State == AiTeamState.Retreating)
        {
            Muster(brain, wave, frame);
            return;
        }

        var centre = AiTargetScoring.CentreOf(world.OwnObjects, team.Members, Vector3.Zero);
        var targetStillLegal = IsLegalTarget(world.EnemyObjects, wave.TargetId);
        var dueForRescan = frame >= wave.LastOrderFrame + ReissueInterval(world.Difficulty);

        if (targetStillLegal && !dueForRescan)
        {
            return;
        }

        var best = AiTargetScoring.PickBest(world.EnemyObjects, centre);

        if (best is null)
        {
            // Nothing left worth attacking: the wave has done its job (or the enemy is gone).
            // The team goes back to Ready so it can be re-tasked, rather than being dissolved.
            _teams.RetreatTeam(brain, team, frame);
            End(brain, wave, frame, "notargets");
            return;
        }

        Engage(brain, wave, best.Value, frame, targetStillLegal ? "reissue" : "retarget");
    }

    private void RunMustering(SkirmishAIBrain brain, AiWave wave, uint frame)
    {
        var team = wave.Team;

        // AiTeamManager owns the regroup timer: a Retreating team reports Ready again once it has
        // regrouped for its configured frames. That transition is the signal this wave waits for
        // - the coordinator does not run a second timer that could disagree with it.
        if (team.State != AiTeamState.Ready)
        {
            return;
        }

        var floor = MinimumRelaunchSize(team);

        if (team.Members.Count < floor)
        {
            // Too few to be an attack. Dissolving returns the survivors to the recruitable pool,
            // so they are folded into the next full team rather than trickling in one at a time.
            _teams.DisbandTeam(brain, team, frame);
            brain.Trace.Count(WaveDisbandedCounter);
            End(brain, wave, frame, "toosmall");
            return;
        }

        var world = brain.World;
        var centre = AiTargetScoring.CentreOf(world.OwnObjects, team.Members, Vector3.Zero);
        var best = AiTargetScoring.PickBest(world.EnemyObjects, centre);

        if (best is null)
        {
            End(brain, wave, frame, "notargets");
            return;
        }

        if (!_teams.TaskTeam(brain, team, frame))
        {
            return;
        }

        wave.MoveTo(AiWaveState.Engaging, frame);
        wave.PeakSize = team.Members.Count;
        brain.Trace.Count(RelaunchCounter);

        Engage(brain, wave, best.Value, frame, "relaunch");
    }

    /// <summary>
    /// Emits the wave's attack order and records it. This is the ONLY place an attack order is
    /// produced, so every launch, retarget and re-issue counts the same way.
    /// </summary>
    /// <remarks>
    /// <c>force: false</c> deliberately: ForceAttackObject bypasses alliance checks, and an AI
    /// that force-attacked would happily order a wave onto a neutral or an ally the moment the
    /// scorer saw one. Ordinary AttackObject lets OrderProcessor apply the same targeting rules
    /// a human's click gets, which is the property that keeps "the AI cannot cheat" true.
    /// </remarks>
    private void Engage(SkirmishAIBrain brain, AiWave wave, in AiTargetScore target, uint frame, string why)
    {
        // "Changed" only counts as a RETARGET from the second order onwards: the first order a
        // wave ever emits always changes the target (from ObjectId.Invalid), and counting that
        // would make every launch look like a retarget in the match report.
        var retargeted = wave.OrdersIssued > 0 && wave.TargetId != target.Id;

        _emitter.AttackWith(wave.Team.Members, target.Id, force: false);

        wave.TargetId = target.Id;
        wave.TargetPriority = target.Priority;
        wave.LastOrderFrame = frame;
        wave.OrdersIssued++;

        EngagementsOrdered++;
        brain.Trace.Count(EngageCounter);

        if (retargeted)
        {
            wave.Retargets++;
            brain.Trace.Count(RetargetCounter);
        }

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} engage wave={wave.Id} team={wave.Team.Id} size={wave.Team.Members.Count} target={target.Id.Index} pri={Tag(target.Priority)} dist={target.ProximityBucket} why={why}"));
    }

    private void Muster(SkirmishAIBrain brain, AiWave wave, uint frame)
    {
        var world = brain.World;
        var team = wave.Team;

        // RetreatTeam refuses unless the team is Tasked, which is exactly right when the team
        // manager already moved it to Retreating: the counter must not be double-bumped.
        if (_teams.RetreatTeam(brain, team, frame))
        {
            brain.Trace.Count(RetreatCounter);
        }

        var muster = MusterPoint(world);

        _emitter.MoveGroup(team.Members, muster);
        brain.Trace.Count(MusterCounter);

        wave.MoveTo(AiWaveState.Mustering, frame);

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} muster wave={wave.Id} team={team.Id} size={team.Members.Count} peak={wave.PeakSize}"));
    }

    // ---- wave scheduling -------------------------------------------------------------------

    private void TryLaunchWave(SkirmishAIBrain brain, uint frame)
    {
        var world = brain.World;

        if (frame < _nextWaveFrame || CountActiveWaves() >= MaxWaves(world.Difficulty))
        {
            return;
        }

        var team = FindFreeReadyTeam();

        if (team is null)
        {
            return;
        }

        var centre = AiTargetScoring.CentreOf(world.OwnObjects, team.Members, Vector3.Zero);
        var best = AiTargetScoring.PickBest(world.EnemyObjects, centre);

        if (best is null)
        {
            // Cadence is NOT consumed: the AI should attack as soon as it can see somebody, not
            // wait out another full interval because the enemy happened to be invisible on the
            // one frame it looked.
            brain.Trace.Count(NoTargetCounter);
            return;
        }

        if (!_teams.TaskTeam(brain, team, frame))
        {
            return;
        }

        var wave = new AiWave(_nextWaveId++, team, frame);
        _waves.Add(wave);

        WavesLaunched++;
        brain.Trace.Count(WaveLaunchedCounter);
        _nextWaveFrame = frame + WaveInterval(world.Difficulty);

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} launch wave={wave.Id} team={team.Id} size={team.Members.Count} next={_nextWaveFrame}"));

        Engage(brain, wave, best.Value, frame, "launch");
    }

    /// <summary>
    /// The lowest-id Ready team that no wave already owns.
    /// </summary>
    /// <remarks>
    /// <see cref="AiTeamManager.NextReadyTeam"/> answers the same question without the ownership
    /// half, which would be wrong here: a wave that has just relaunched leaves its team Tasked,
    /// but a wave still Mustering over a regrouped team leaves it Ready for exactly as long as it
    /// takes this manager to notice. Handing that team to a second wave would give one army two
    /// contradictory orders every frame.
    /// </remarks>
    private AiTeam? FindFreeReadyTeam()
    {
        AiTeam? best = null;
        var teams = _teams.Teams;

        for (var t = 0; t < teams.Count; t++)
        {
            var team = teams[t];

            if (team.State != AiTeamState.Ready || IsOwned(team))
            {
                continue;
            }

            if (best is null || team.Id < best.Id)
            {
                best = team;
            }
        }

        return best;
    }

    private bool IsOwned(AiTeam team)
    {
        for (var w = 0; w < _waves.Count; w++)
        {
            if (_waves[w].State != AiWaveState.Ended && ReferenceEquals(_waves[w].Team, team))
            {
                return true;
            }
        }

        return false;
    }

    private int CountActiveWaves()
    {
        var count = 0;

        for (var w = 0; w < _waves.Count; w++)
        {
            if (_waves[w].State != AiWaveState.Ended)
            {
                count++;
            }
        }

        return count;
    }

    private void End(SkirmishAIBrain brain, AiWave wave, uint frame, string why)
    {
        wave.MoveTo(AiWaveState.Ended, frame);

        WavesEnded++;
        brain.Trace.Count(WaveEndedCounter);

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} end wave={wave.Id} team={wave.Team.Id} orders={wave.OrdersIssued} retargets={wave.Retargets} why={why}"));
    }

    /// <summary>
    /// Removes ended waves, preserving launch order for the survivors - the same order-preserving
    /// compaction AiTeamManager uses, and for the same reason: wave order is iteration order.
    /// </summary>
    private void Compact()
    {
        var write = 0;

        for (var read = 0; read < _waves.Count; read++)
        {
            if (_waves[read].State == AiWaveState.Ended)
            {
                continue;
            }

            _waves[write++] = _waves[read];
        }

        if (write < _waves.Count)
        {
            _waves.RemoveRange(write, _waves.Count - write);
        }
    }

    // ---- predicates and helpers --------------------------------------------------------------

    /// <summary>
    /// True when a wave has lost enough of its peak strength to be pulled back. Int
    /// cross-multiplication, no float ratio - the same shape as
    /// <see cref="AiTeamManager.ShouldRetreat"/>.
    /// </summary>
    public static bool ShouldRetreat(AiWave wave, int retreatAtPercentOfPeak = RetreatAtPercentOfPeak)
    {
        ArgumentNullException.ThrowIfNull(wave);

        return wave.PeakSize > 0
            && wave.Team.Members.Count * 100 < wave.PeakSize * retreatAtPercentOfPeak;
    }

    /// <summary>
    /// Members a mustered team needs before it is sent back in. At least one, so a team of one
    /// cannot be stuck below its own floor forever.
    /// </summary>
    public static int MinimumRelaunchSize(AiTeam team)
    {
        ArgumentNullException.ThrowIfNull(team);

        var floor = team.TargetSize * RelaunchAtPercentOfTargetSize / 100;

        return floor < 1 ? 1 : floor;
    }

    /// <summary>
    /// Where a mauled wave pulls back to: the centre of the player's finished structures, or of
    /// everything it owns when it has no buildings left.
    /// </summary>
    /// <remarks>
    /// Deliberately not "the spawn point" - the AI does not get told where its spawn was, and a
    /// base centre computed from the snapshot degrades gracefully as the base is destroyed. With
    /// no objects at all the wave has nothing to muster to and gets the origin; that case only
    /// arises when the player is already eliminated.
    /// </remarks>
    public static Vector3 MusterPoint(IAiWorldView world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var own = world.OwnObjects;

        if (AiTargetScoring.TryCentreOf(own, static o => o.IsCompletedStructure, out var buildings))
        {
            return buildings;
        }

        return AiTargetScoring.TryCentreOf(own, static o => o.Id.IsValid, out var anything)
            ? anything
            : Vector3.Zero;
    }

    /// <summary>
    /// True when <paramref name="targetId"/> is still in the enemy snapshot AND still a legal
    /// target. Re-read per frame rather than cached: an id the AI holds from an earlier frame can
    /// be dead, and the emitter's file header records what a stale id costs downstream.
    /// </summary>
    private static bool IsLegalTarget(IReadOnlyList<AiObjectView>? enemies, ObjectId targetId)
    {
        if (enemies == null || targetId.IsInvalid)
        {
            return false;
        }

        for (var i = 0; i < enemies.Count; i++)
        {
            if (enemies[i].Id == targetId)
            {
                return AiTargetScoring.Classify(enemies[i]) != AiAttackPriority.None;
            }
        }

        return false;
    }

    private uint WaveInterval(Difficulty difficulty)
        => _waveIntervalOverride > 0 ? _waveIntervalOverride : WaveIntervalFrames(difficulty);

    private uint ReissueInterval(Difficulty difficulty)
        => _reissueIntervalOverride > 0 ? _reissueIntervalOverride : ReissueIntervalFrames(difficulty);

    private int MaxWaves(Difficulty difficulty)
        => _maxWavesOverride > 0 ? _maxWavesOverride : MaxConcurrentWaves(difficulty);

    private static string Tag(AiAttackPriority priority) => priority switch
    {
        AiAttackPriority.MobileUnit => "unit",
        AiAttackPriority.Structure => "structure",
        AiAttackPriority.UnderConstruction => "building",
        _ => "none",
    };

    private void Line(SkirmishAIBrain brain, string message) => brain.Trace.Line(Name, message);
}
