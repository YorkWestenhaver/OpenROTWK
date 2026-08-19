// S8 script-runtime system tests: core evaluator semantics against a recording fake host
// (timers, counters, flags, one-shot/false-action branches, enable/disable/subroutine,
// evaluation order), Xfer round-trip + mid-run save/load continuation, a HeadlessSimGame
// end-to-end (real spawn through GameLogic, real weapon-target order), and the compile-and-
// run of an actual scenariogen map (job005_spawn_fight.map) — the "our engine natively runs
// a generated scenario" validation lever.

using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OpenSage.Data.Map;
using OpenSage.Logic.Object;
using OpenSage.Logic.Script;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Rng;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Script;

public class SimScriptEngineTests
{
    private static ISimRandom NewRandom(uint seed = 0xB00) =>
        new CountingSimRandom(LogicRandom.CreateForSimContext(seed));

    // ---- fake host ----

    private sealed class FakeScriptHost : ISimScriptHost
    {
        public LogicFrame Frame;
        public readonly List<string> Log = new();
        public readonly Dictionary<string, bool> Units = new(); // name -> alive
        public readonly HashSet<string> DestroyedTeams = new();
        public bool CreateSucceeds = true;
        public bool MapExitRequested;

        public LogicFrame CurrentFrame => Frame;

        public bool TryGetNamedUnit(string name, out bool aliveNotDead)
        {
            aliveNotDead = false;
            if (name == null || !Units.TryGetValue(name, out var alive))
            {
                return false;
            }

            aliveNotDead = alive;
            return true;
        }

        public bool IsTeamDestroyed(string teamName) => DestroyedTeams.Contains(teamName);

        public bool IsPlayerAllDestroyed(string playerName) => false;

        public bool CreateUnitOnTeamAtWaypoint(string unitName, string objectTypeName, string teamName, string waypointName)
        {
            Log.Add($"create:{unitName}:{objectTypeName}:{teamName}:{waypointName}");
            if (!CreateSucceeds)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(unitName))
            {
                Units[unitName] = true;
            }

            return true;
        }

        public void TeamAttackTeam(string attackerTeamName, string victimTeamName) =>
            Log.Add($"teamattack:{attackerTeamName}:{victimTeamName}");

        public void NamedAttackNamed(string attackerName, string victimName) =>
            Log.Add($"attack:{attackerName}:{victimName}");

        public void RequestMapExit()
        {
            MapExitRequested = true;
            Log.Add($"mapexit:{Frame.Value}");
        }
    }

    // ---- hand-built program helper ----

    private sealed class ProgramBuilder
    {
        private readonly List<SimScript> _scripts = new();
        private readonly List<SimScriptGroup> _groups = new();
        private readonly List<string> _counters = new();
        private readonly List<string> _flags = new();
        private readonly List<string> _units = new();

        public int Counter(string name) => Slot(_counters, name);
        public int Flag(string name) => Slot(_flags, name);
        public int Unit(string name) => Slot(_units, name);

        private static int Slot(List<string> table, string name)
        {
            var index = table.IndexOf(name);
            if (index < 0)
            {
                index = table.Count;
                table.Add(name);
            }

            return index;
        }

        public int AddScript(
            string name,
            SimScriptCondition[] conditions,
            SimScriptAction[] actionsIfTrue,
            SimScriptAction[] actionsIfFalse = null,
            bool oneShot = true,
            bool active = true,
            bool subroutine = false,
            uint intervalFrames = 0,
            int groupIndex = -1)
        {
            _scripts.Add(new SimScript
            {
                Name = name,
                PlayerIndex = 0,
                GroupIndex = groupIndex,
                InitiallyActive = active,
                DeactivateUponSuccess = oneShot,
                IsSubroutine = subroutine,
                ActiveInEasy = true,
                ActiveInNormal = true,
                ActiveInHard = true,
                EvaluationInterval = new LogicFrameSpan(intervalFrames),
                OrClauses = new IReadOnlyList<SimScriptCondition>[] { conditions },
                ActionsIfTrue = actionsIfTrue ?? [],
                ActionsIfFalse = actionsIfFalse ?? [],
            });
            return _scripts.Count - 1;
        }

        public int AddGroup(string name, bool active, bool subroutine, params int[] memberIndices)
        {
            _groups.Add(new SimScriptGroup
            {
                Name = name,
                PlayerIndex = 0,
                InitiallyActive = active,
                IsSubroutine = subroutine,
                ScriptIndices = memberIndices,
            });
            return _groups.Count - 1;
        }

        public SimScriptProgram Build() => new()
        {
            Scripts = _scripts,
            Groups = _groups,
            CounterNames = _counters,
            FlagNames = _flags,
            UnitNames = _units,
            UnknownConditionIds = [],
            UnknownActionIds = [],
        };
    }

    private static SimScriptCondition True() => new() { Kind = SimScriptConditionKind.True };

    private static SimScriptCondition TimerExpired(int slot) =>
        new() { Kind = SimScriptConditionKind.TimerExpired, SlotIndex = slot };

    private static SimScriptAction SetTimer(int slot, int frames) =>
        new() { Kind = SimScriptActionKind.SetTimer, SlotIndex = slot, IntValue = frames };

    private static SimScriptAction MapExit() => new() { Kind = SimScriptActionKind.MapExit };

    private static void Run(SimScriptEngine engine, FakeScriptHost host, uint frames, uint startFrame = 0)
    {
        for (var f = startFrame; f < startFrame + frames; f++)
        {
            host.Frame = new LogicFrame(f);
            engine.Update();
        }
    }

    // ---- timer semantics ----

    [Fact]
    public void SetTimer_ExpiresExactlyAfterItsFrameCount()
    {
        var b = new ProgramBuilder();
        var t = b.Counter("ExitTlm");
        b.AddScript("Arm", [True()], [SetTimer(t, 5)]);
        b.AddScript("Exit", [TimerExpired(t)], [MapExit()]);

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());

        Run(engine, host, 20);

        // Armed with 5 on frame 0 (after the decrement pass), first decrement on frame 1,
        // reaches 0 — "< 1", expired — on frame 5.
        Assert.True(engine.MapExitRequested);
        Assert.Equal(5u, engine.MapExitFrame.Value);
        Assert.True(host.MapExitRequested);
    }

    [Fact]
    public void SetTimer_Reschedule_RestartsTheCountdown()
    {
        // The scenariogen telemetry idiom: arm with the default, a case script reschedules
        // the SAME timer; the value in effect at expiry encodes which script last set it.
        var b = new ProgramBuilder();
        var t = b.Counter("ExitTlm");
        var flag = b.Flag("Probe");
        b.AddScript("Arm", [True()], [SetTimer(t, 200)]);
        b.AddScript("Case",
            [new SimScriptCondition { Kind = SimScriptConditionKind.Flag, SlotIndex = flag, IntValue = 1 }],
            [SetTimer(t, 100)]);
        b.AddScript("SetProbe", [True()],
            [new SimScriptAction { Kind = SimScriptActionKind.SetFlag, SlotIndex = flag, IntValue = 1 }]);
        b.AddScript("Exit", [TimerExpired(t)], [MapExit()]);

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());

        Run(engine, host, 150);

        // Flag set on frame 0 (SetProbe) but the Case script sits BEFORE SetProbe's write
        // is visible... walk order: Arm(200) then Case (flag still false) then SetProbe.
        // Frame 1: Case sees the flag, reschedules to 100 -> expiry on frame 101.
        Assert.Equal(101u, engine.MapExitFrame.Value);
    }

    [Fact]
    public void PauseAndRestartTimer_FreezeAndResumeTheCountdown()
    {
        var b = new ProgramBuilder();
        var t = b.Counter("T");
        b.AddScript("Arm", [True()], [SetTimer(t, 10)]);
        b.AddScript("Pause",
            [new SimScriptCondition
            {
                Kind = SimScriptConditionKind.Counter,
                SlotIndex = t,
                Comparison = SimScriptComparison.Equal,
                IntValue = 7,
            }],
            [new SimScriptAction { Kind = SimScriptActionKind.PauseTimer, SlotIndex = t }]);
        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());

        Run(engine, host, 6); // frames 0..5; armed frame 0; value 7 on frame 3, paused there
        Assert.Equal(7, engine.GetCounterValue("T"));
        Assert.False(engine.IsTimerRunning("T"));

        Run(engine, host, 3, startFrame: 6); // paused: no decrement
        Assert.Equal(7, engine.GetCounterValue("T"));
    }

    [Fact]
    public void TimerExpired_IsFalse_BeforeTheTimerWasEverStarted()
    {
        var b = new ProgramBuilder();
        var t = b.Counter("Never");
        b.AddScript("Exit", [TimerExpired(t)], [MapExit()]);

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());

        Run(engine, host, 10);

        Assert.False(engine.MapExitRequested); // value 0 but isCountdownTimer false
    }

    // ---- counters / flags / conditions ----

    [Fact]
    public void Counter_AllSixComparisons()
    {
        var b = new ProgramBuilder();
        var c = b.Counter("C");
        var results = new List<string>();
        foreach (var (cmp, operand, label) in new[]
        {
            (SimScriptComparison.LessThan, 5, "lt5"),
            (SimScriptComparison.LessEqual, 3, "le3"),
            (SimScriptComparison.Equal, 3, "eq3"),
            (SimScriptComparison.GreaterEqual, 3, "ge3"),
            (SimScriptComparison.Greater, 2, "gt2"),
            (SimScriptComparison.NotEqual, 4, "ne4"),
        })
        {
            var flag = b.Flag(label);
            b.AddScript(label,
                [new SimScriptCondition { Kind = SimScriptConditionKind.Counter, SlotIndex = c, Comparison = cmp, IntValue = operand }],
                [new SimScriptAction { Kind = SimScriptActionKind.SetFlag, SlotIndex = flag, IntValue = 1 }]);
            results.Add(label);
        }

        // Set C = 3 first in walk order? No - append last; counter starts 0, one frame to set.
        var setter = b.AddScript("Set", [True()],
            [new SimScriptAction { Kind = SimScriptActionKind.SetCounter, SlotIndex = c, IntValue = 3 }]);

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());

        Run(engine, host, 2); // frame 0 sets C=3 (comparisons that frame saw 0); frame 1 evaluates all vs 3

        Assert.True(engine.GetFlagValue("lt5"));
        Assert.True(engine.GetFlagValue("le3"));
        Assert.True(engine.GetFlagValue("eq3"));
        Assert.True(engine.GetFlagValue("ge3"));
        Assert.True(engine.GetFlagValue("gt2"));
        Assert.True(engine.GetFlagValue("ne4"));
    }

    [Fact]
    public void IncrementDecrement_UseGplParameterMeaning()
    {
        var b = new ProgramBuilder();
        var c = b.Counter("C");
        b.AddScript("Add", [True()],
            [new SimScriptAction { Kind = SimScriptActionKind.AddCounter, SlotIndex = c, IntValue = 7 }]);
        b.AddScript("Sub", [True()],
            [new SimScriptAction { Kind = SimScriptActionKind.SubCounter, SlotIndex = c, IntValue = 3 }]);

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());

        Run(engine, host, 1);
        Assert.Equal(4, engine.GetCounterValue("C"));
    }

    [Fact]
    public void InvertedCondition_FlipsTheResult()
    {
        var b = new ProgramBuilder();
        var flag = b.Flag("Out");
        b.AddScript("NotFalse",
            [new SimScriptCondition { Kind = SimScriptConditionKind.False, Inverted = true }],
            [new SimScriptAction { Kind = SimScriptActionKind.SetFlag, SlotIndex = flag, IntValue = 1 }]);

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());
        Run(engine, host, 1);

        Assert.True(engine.GetFlagValue("Out"));
    }

    // ---- one-shot / false actions / enable-disable / subroutines ----

    [Fact]
    public void OneShot_DeactivatesAfterTrue_EvenWithNoActions()
    {
        var b = new ProgramBuilder();
        b.AddScript("Empty", [True()], []);

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());
        Run(engine, host, 1);

        Assert.False(engine.IsScriptActive("Empty")); // GPL: one-shot gates on the CONDITION
    }

    [Fact]
    public void FalseActions_RunWhenConditionsFail_AndOneShotDeactivates()
    {
        var b = new ProgramBuilder();
        var flag = b.Flag("SawFalse");
        b.AddScript("F",
            [new SimScriptCondition { Kind = SimScriptConditionKind.False }],
            [MapExit()],
            actionsIfFalse: [new SimScriptAction { Kind = SimScriptActionKind.SetFlag, SlotIndex = flag, IntValue = 1 }]);

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());
        Run(engine, host, 3);

        Assert.True(engine.GetFlagValue("SawFalse"));
        Assert.False(engine.MapExitRequested);
        Assert.False(engine.IsScriptActive("F"));
    }

    [Fact]
    public void EnableScript_ActivatesADisabledScript()
    {
        var b = new ProgramBuilder();
        var sleeper = b.AddScript("Sleeper", [True()], [MapExit()], active: false);
        b.AddScript("Waker", [True()],
            [new SimScriptAction { Kind = SimScriptActionKind.EnableScript, Name0 = "Sleeper", TargetScriptIndex = sleeper }]);

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());

        Run(engine, host, 2);

        // Waker enabled it on frame 0 but Sleeper sits BEFORE the wake in that frame's
        // walk? No: Sleeper is index 0, Waker index 1 -> Sleeper runs frame 1.
        Assert.True(engine.MapExitRequested);
        Assert.Equal(1u, engine.MapExitFrame.Value);
    }

    [Fact]
    public void CallSubroutine_RunsASubroutineScriptInline()
    {
        var b = new ProgramBuilder();
        var flag = b.Flag("SubRan");
        var sub = b.AddScript("Sub", [True()],
            [new SimScriptAction { Kind = SimScriptActionKind.SetFlag, SlotIndex = flag, IntValue = 1 }],
            subroutine: true, oneShot: false);
        b.AddScript("Caller", [True()],
            [new SimScriptAction { Kind = SimScriptActionKind.CallSubroutine, Name0 = "Sub", TargetScriptIndex = sub }]);

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());
        Run(engine, host, 1);

        Assert.True(engine.GetFlagValue("SubRan"));
    }

    [Fact]
    public void InactiveGroup_MembersDoNotRun_UntilEnabled()
    {
        var b = new ProgramBuilder();
        var member = b.AddScript("Member", [True()], [MapExit()], groupIndex: 0);
        var group = b.AddGroup("Folder", active: false, subroutine: false, member);
        b.AddScript("Enabler", [True()],
            [new SimScriptAction { Kind = SimScriptActionKind.EnableScript, Name0 = "Folder", TargetGroupIndex = group }]);

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());
        Run(engine, host, 3);

        Assert.True(engine.MapExitRequested);
        Assert.Equal(1u, engine.MapExitFrame.Value); // enabled frame 0, member runs frame 1
    }

    // ---- named-unit conditions ----

    [Fact]
    public void NamedCreated_TracksHostWorld_AndNamedDestroyedUsesEverExisted()
    {
        var b = new ProgramBuilder();
        var slot = b.Unit("Atk_1");
        var created = b.Flag("SawCreated");
        var destroyed = b.Flag("SawDestroyed");
        b.AddScript("Created",
            [new SimScriptCondition { Kind = SimScriptConditionKind.NamedCreated, NameSlotIndex = slot, SubjectName = "Atk_1" }],
            [new SimScriptAction { Kind = SimScriptActionKind.SetFlag, SlotIndex = created, IntValue = 1 }]);
        b.AddScript("Destroyed",
            [new SimScriptCondition { Kind = SimScriptConditionKind.NamedDestroyed, NameSlotIndex = slot, SubjectName = "Atk_1" }],
            [new SimScriptAction { Kind = SimScriptActionKind.SetFlag, SlotIndex = destroyed, IntValue = 1 }]);

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());

        Run(engine, host, 2);
        Assert.False(engine.GetFlagValue("SawCreated"));
        Assert.False(engine.GetFlagValue("SawDestroyed")); // never existed -> not destroyed

        host.Units["Atk_1"] = true;
        Run(engine, host, 1, startFrame: 2);
        Assert.True(engine.GetFlagValue("SawCreated"));
        Assert.False(engine.GetFlagValue("SawDestroyed"));

        host.Units.Remove("Atk_1"); // left the world after having existed
        Run(engine, host, 1, startFrame: 3);
        Assert.True(engine.GetFlagValue("SawDestroyed"));
    }

    [Fact]
    public void CreateNamed_DuplicateLiveName_BlocksTheSpawn()
    {
        var b = new ProgramBuilder();
        var slot = b.Unit("Probe_1");
        b.AddScript("Spawn1", [True()],
            [new SimScriptAction
            {
                Kind = SimScriptActionKind.CreateNamedOnTeamAtWaypoint,
                NameSlotIndex = slot,
                Name0 = "Probe_1", Name1 = "GondorFighterHorde", Name2 = "teamScnAttacker", Name3 = "wpProbe",
            }]);
        b.AddScript("Spawn2", [True()],
            [new SimScriptAction
            {
                Kind = SimScriptActionKind.CreateNamedOnTeamAtWaypoint,
                NameSlotIndex = slot,
                Name0 = "Probe_1", Name1 = "GondorFighterHorde", Name2 = "teamScnAttacker", Name3 = "wpProbe",
            }]);

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());
        Run(engine, host, 1);

        // Spawn1 created it; Spawn2 saw the live duplicate and never called the host.
        Assert.Single(host.Log.FindAll(entry => entry.StartsWith("create:Probe_1")));
    }

    // ---- evaluation interval ----

    [Fact]
    public void EvaluationInterval_SkipsFramesBetweenEvaluations()
    {
        var b = new ProgramBuilder();
        var c = b.Counter("Evals");
        b.AddScript("Periodic", [True()],
            [new SimScriptAction { Kind = SimScriptActionKind.AddCounter, SlotIndex = c, IntValue = 1 }],
            oneShot: false, intervalFrames: 10);

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(b.Build(), host, NewRandom());

        Run(engine, host, 100);

        // Stagger start in [0, 10] then every 10 frames: about 10 evaluations in 100
        // frames, never 100 (the per-frame rate) — the interval gate is what's under test.
        var evals = engine.GetCounterValue("Evals");
        Assert.InRange(evals, 9, 11);
    }

    // ---- Xfer: round-trip, CRC equality, mid-run save/load continuation ----

    private static (ProgramBuilder, int) TelemetryProgram()
    {
        var b = new ProgramBuilder();
        var t = b.Counter("ExitTlm");
        b.AddScript("Arm", [True()], [SetTimer(t, 12)]);
        b.AddScript("Exit", [TimerExpired(t)], [MapExit()]);
        return (b, t);
    }

    private static uint CrcOf(SimScriptEngine engine)
    {
        var visitor = new XferCrcVisitor();
        engine.Xfer(visitor);
        return visitor.Value;
    }

    [Fact]
    public void Xfer_MidRunSaveLoad_ContinuationMatchesUnperturbedRun()
    {
        var (b, _) = TelemetryProgram();
        var program = b.Build();

        var hostA = new FakeScriptHost();
        var reference = new SimScriptEngine(program, hostA, NewRandom());
        Run(reference, hostA, 20);
        Assert.Equal(12u, reference.MapExitFrame.Value);

        // Second run: save at frame 5, load into a FRESH engine, continue.
        var hostB = new FakeScriptHost();
        var original = new SimScriptEngine(program, hostB, NewRandom());
        Run(original, hostB, 5);

        var stream = new MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            original.Xfer(save);
        }

        var hostC = new FakeScriptHost();
        var restored = new SimScriptEngine(program, hostC, NewRandom(0xDEAD)); // different seed: state must come from the stream
        stream.Position = 0;
        using (var load = new XferLoad(stream, leaveOpen: true))
        {
            restored.Xfer(load);
        }

        Assert.Equal(CrcOf(original), CrcOf(restored));

        Run(restored, hostC, 15, startFrame: 5);
        Assert.Equal(12u, restored.MapExitFrame.Value);
    }

    [Fact]
    public void Xfer_RejectsStateFromADifferentProgram()
    {
        var (b, _) = TelemetryProgram();
        var engine = new SimScriptEngine(b.Build(), new FakeScriptHost(), NewRandom());

        var other = new ProgramBuilder();
        other.Counter("SomethingElse");
        other.AddScript("Other", [True()], []);
        var otherEngine = new SimScriptEngine(other.Build(), new FakeScriptHost(), NewRandom());

        var stream = new MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            engine.Xfer(save);
        }

        stream.Position = 0;
        Assert.ThrowsAny<System.Exception>(() =>
        {
            using var load = new XferLoad(stream, leaveOpen: true);
            otherEngine.Xfer(load);
        });
    }

    [Fact]
    public void TwoRuns_SameSeed_AreCrcIdenticalEveryFrame()
    {
        var (b, _) = TelemetryProgram();
        var program = b.Build();

        var host1 = new FakeScriptHost();
        var host2 = new FakeScriptHost();
        var engine1 = new SimScriptEngine(program, host1, NewRandom(0xAA));
        var engine2 = new SimScriptEngine(program, host2, NewRandom(0xAA));

        for (var f = 0u; f < 20; f++)
        {
            host1.Frame = new LogicFrame(f);
            host2.Frame = new LogicFrame(f);
            engine1.Update();
            engine2.Update();
            Assert.Equal(CrcOf(engine1), CrcOf(engine2));
        }
    }

    // ---- HeadlessSimGame end-to-end: real spawn + real order + exit ----

    private const string Definitions = @"
Weapon ScriptTestGun
  AttackRange = 500
  DamageNugget
    Damage = 10
    Radius = 0.0
    DamageType = SLASH
    DeathType = NORMAL
  End
End

Object ScriptWarrior
  KindOf = INFANTRY CAN_ATTACK
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  WeaponSet
    Conditions = None
    Weapon = PRIMARY ScriptTestGun
  End
End

Object ScriptDummy
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    [Fact]
    public void EndToEnd_SpawnOrderExit_OnHeadlessSimGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xB00);
        game.LoadIniText(Definitions);

        var host = new SimScriptHostAdapter(game, game.CivilianPlayer);
        host.RegisterWaypoint("wpAtk", new Vector3(10, 10, 0));
        host.RegisterWaypoint("wpDef", new Vector3(60, 10, 0));

        var b = new ProgramBuilder();
        var t = b.Counter("ExitTlm");
        var atkSlot = b.Unit("Atk_1");
        var defSlot = b.Unit("Def_1");
        b.AddScript("SpawnA", [True()],
            [new SimScriptAction
            {
                Kind = SimScriptActionKind.CreateNamedOnTeamAtWaypoint,
                NameSlotIndex = atkSlot,
                Name0 = "Atk_1", Name1 = "ScriptWarrior", Name2 = "teamScnAttacker", Name3 = "wpAtk",
            }]);
        b.AddScript("SpawnB", [True()],
            [new SimScriptAction
            {
                Kind = SimScriptActionKind.CreateNamedOnTeamAtWaypoint,
                NameSlotIndex = defSlot,
                Name0 = "Def_1", Name1 = "ScriptDummy", Name2 = "teamScnDefender", Name3 = "wpDef",
            }]);
        b.AddScript("Order",
            [new SimScriptCondition { Kind = SimScriptConditionKind.NamedCreated, NameSlotIndex = atkSlot, SubjectName = "Atk_1" }],
            [new SimScriptAction { Kind = SimScriptActionKind.TeamAttackTeam, Name0 = "teamScnAttacker", Name1 = "teamScnDefender" }]);
        b.AddScript("Arm", [True()], [SetTimer(t, 10)]);
        b.AddScript("Exit", [TimerExpired(t)], [MapExit()]);

        var engine = new SimScriptEngine(
            b.Build(), host, game.GameEngine.SimContext.GameLogicRandom);

        for (var i = 0; i < 15 && !host.MapExitRequested; i++)
        {
            engine.Update();      // reads GameLogic.CurrentFrame (pre-increment)
            game.Step();
        }

        // The spawns are REAL engine objects: named, positioned at their waypoints, alive.
        Assert.True(game.GameLogic.TryGetObjectByName("Atk_1", out var attacker));
        Assert.True(game.GameLogic.TryGetObjectByName("Def_1", out var defender));
        Assert.Equal(new Vector3(10, 10, 0), attacker.Translation);
        Assert.Equal(new Vector3(60, 10, 0), defender.Translation);

        // The team order landed on the S1 weapon path: target set, pointing at Def_1.
        Assert.NotNull(attacker.CurrentWeapon);
        Assert.NotNull(attacker.CurrentWeapon.CurrentTarget);
        Assert.Equal(defender.Id, attacker.CurrentWeapon.CurrentTarget.TargetObjectId);

        // And the telemetry exit fired on schedule.
        Assert.True(host.MapExitRequested);
        Assert.Equal(10u, engine.MapExitFrame.Value);
    }

    // ---- the scenariogen map: compile the real .map, run it natively ----

    [Fact]
    public void ScenariogenMap_CompilesAndRunsNatively()
    {
        var mapPath = Path.Combine("Logic", "Script", "Assets", "job005_spawn_fight.map");
        MapFile mapFile;
        using (var stream = File.OpenRead(mapPath))
        {
            mapFile = MapFile.FromStream(stream);
        }

        // scenariogen (like WorldBuilder) writes the scripts into the PlayerScriptsList
        // chunk; SidesList.Players' per-player script slots stay empty.
        var program = SimScriptCompiler.Compile(mapFile.PlayerScriptsList);

        // Everything this generated map uses is inside the documented subset.
        Assert.Empty(program.UnknownConditionIds);
        Assert.Empty(program.UnknownActionIds);
        Assert.Contains(program.Scripts, s => s.Name == "ScnA_00_Spawn");
        Assert.Contains(program.Scripts, s => s.Name == "Tlm_Exit");

        var host = new FakeScriptHost();
        var engine = new SimScriptEngine(program, host, NewRandom());

        Run(engine, host, 250);

        // Both sides spawned their units on their default singleton teams...
        Assert.Contains("create:Atk_1:GondorFighterHorde:teamScnAttacker:wpAtk", host.Log);
        Assert.Contains("create:Def_1:MordorFighterHorde:teamScnDefender:wpDef", host.Log);

        // ...ordered the mutual attack...
        Assert.Contains("teamattack:teamScnAttacker:teamScnDefender", host.Log);
        Assert.Contains("teamattack:teamScnDefender:teamScnAttacker", host.Log);

        // ...and the exit-frame telemetry read TRUE: the case script rescheduled the shared
        // timer to 100 on frame 0 itself (NAMED_CREATED sees the same-frame spawn, which
        // runs earlier in the walk), expiry exactly 100 frames later — the "~100 reading"
        // the VM telemetry calibrated (TLM_TRUE), not the ~200 default.
        Assert.True(engine.MapExitRequested);
        Assert.Equal(100u, engine.MapExitFrame.Value);
    }
}
