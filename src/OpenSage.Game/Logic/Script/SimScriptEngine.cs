// S8 script-engine runtime (subset) — the deterministic per-frame evaluator.
//
// Behavioral reference (clean-room, semantics only — no code transcribed):
// generals-gpl GeneralsMD ScriptEngine.cpp:
//   ScriptEngine::update           — timer decrement pass, then per-player script walk
//   ScriptEngine::executeScripts   — skip subroutines at the top level
//   ScriptEngine::executeScript    — active gate, difficulty gate, periodic-eval gate,
//                                    conditions -> true/false action lists, one-shot deactivate
//   ScriptEngine::evaluateConditions — OR of AND-lists, short-circuit after first false AND term
//   ScriptEngine::evaluateCounter/Flag/Timer, setCounter/addCounter/subCounter/setFlag,
//   setTimer/pauseTimer/restartTimer, enableScript/disableScript/callSubroutine
// ScriptConditions.cpp: evaluateNamedCreated (= exists), evaluateNamedUnitDestroyed
//   (live -> isEffectivelyDead, gone -> didUnitExist), evaluateNamedUnitExists.
// ScriptActions.cpp: createUnitOnTeamAt duplicate-name guard, doAttack, doNamedAttack.
//
// Determinism: evaluation order is FIXED — players ascending, scripts in compiled program
// order (top-level then groups, map order); timers decrement in slot order before any script
// runs; all state is int/bool/LogicFrame; the only RNG draw is the GPL load-time stagger for
// periodically-evaluated scripts (0..2 s, drawn in program order at Reset).
//
// All mutable state lives in the field inventory below and appears in Xfer exactly once,
// declaration order (F9 — our order, never retail's).

using System;
using System.Collections.Generic;
using OpenSage.SimCore;
using OpenSage.SimCore.Rng;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Script;

public enum SimScriptDifficulty
{
    Easy = 0,
    Normal = 1,
    Hard = 2,
}

[SimState]
public sealed class SimScriptEngine
{
    // GPL LOGICFRAMES_PER_SECOND at the F6 tick rate.
    internal const int LogicFramesPerSecond = 5;

    // CALL_SUBROUTINE re-entrancy cap (the original has none and would overflow the stack
    // on a self-calling subroutine; a data error, not a behavior we reproduce).
    private const int MaxSubroutineDepth = 32;

    private struct CounterState
    {
        public int Value;
        public bool IsCountdownTimer;
    }

    private struct ScriptState
    {
        public bool Active;
        public LogicFrame NextEvalFrame;
    }

    private readonly SimScriptProgram _program;
    private readonly ISimScriptHost _host;

    // ---- mutable sim state (the Xfer inventory, declaration order) ----
    private readonly List<CounterState> _counters = new();
    private readonly List<bool> _flags = new();
    private readonly List<ScriptState> _scriptStates = new();
    private readonly List<bool> _groupActive = new();
    private readonly List<bool> _unitEverExisted = new();
    private bool _mapExitRequested;
    private LogicFrame _mapExitFrame;
    private int _unknownActionsExecuted;
    private int _unknownConditionsEvaluated;
    // ---- end Xfer inventory ----

    private uint _programFingerprint;
    private int _subroutineDepth;

    public SimScriptDifficulty Difficulty { get; set; } = SimScriptDifficulty.Normal;

    public SimScriptEngine(SimScriptProgram program, ISimScriptHost host, ISimRandom random)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(random);
        _program = program;
        _host = host;
        _programFingerprint = ComputeProgramFingerprint(program);
        Reset(random);
    }

    public SimScriptProgram Program => _program;

    /// <summary>True once a MAP_EXIT action ran; <see cref="MapExitFrame"/> is the frame it ran on.</summary>
    public bool MapExitRequested => _mapExitRequested;

    public LogicFrame MapExitFrame => _mapExitFrame;

    /// <summary>Diagnostics: compiled-Unknown actions that were reached at runtime.</summary>
    public int UnknownActionsExecuted => _unknownActionsExecuted;

    public int UnknownConditionsEvaluated => _unknownConditionsEvaluated;

    /// <summary>
    /// Rebuilds run state from the program: counters/flags zeroed (GPL tables start zeroed),
    /// script/group actives from map flags, and the GPL load-time stagger for scripts with a
    /// periodic evaluation interval — a uniform 0..2 s frame offset drawn in program order
    /// (ScriptEngine::checkConditionsForTeamNames).
    /// </summary>
    private void Reset(ISimRandom random)
    {
        _counters.Clear();
        for (var i = 0; i < _program.CounterNames.Count; i++)
        {
            _counters.Add(default);
        }

        _flags.Clear();
        for (var i = 0; i < _program.FlagNames.Count; i++)
        {
            _flags.Add(false);
        }

        _scriptStates.Clear();
        foreach (var script in _program.Scripts)
        {
            var state = new ScriptState { Active = script.InitiallyActive, NextEvalFrame = LogicFrame.Zero };
            if (script.EvaluationInterval.Value > 0)
            {
                state.NextEvalFrame = new LogicFrame((uint)random.Next(0, 2 * LogicFramesPerSecond));
            }
            _scriptStates.Add(state);
        }

        _groupActive.Clear();
        foreach (var group in _program.Groups)
        {
            _groupActive.Add(group.InitiallyActive);
        }

        _unitEverExisted.Clear();
        for (var i = 0; i < _program.UnitNames.Count; i++)
        {
            _unitEverExisted.Add(false);
        }

        _mapExitRequested = false;
        _mapExitFrame = LogicFrame.Zero;
        _unknownActionsExecuted = 0;
        _unknownConditionsEvaluated = 0;
    }

    /// <summary>
    /// One 5 Hz script pass (GPL ScriptEngine::update subset): decrement countdown timers,
    /// then walk every player's scripts in the compiled order. Call once per logic frame,
    /// before module updates (the original runs its script engine at the top of
    /// GameLogic::update).
    /// </summary>
    public void Update()
    {
        // Countdown timers tick before any script evaluates; they stop at -1
        // (GPL: "Counters go to -1 and stop").
        for (var i = 0; i < _counters.Count; i++)
        {
            var counter = _counters[i];
            if (counter.IsCountdownTimer && counter.Value >= 0)
            {
                counter.Value--;
                _counters[i] = counter;
            }
        }

        // The compiled Scripts list is already in the original's walk order: players
        // ascending, top-level scripts then groups. Top-level subroutines never self-run;
        // group members run only while their group is active and not a subroutine group.
        for (var i = 0; i < _program.Scripts.Count; i++)
        {
            var script = _program.Scripts[i];
            if (script.IsSubroutine)
            {
                continue;
            }

            if (script.GroupIndex >= 0)
            {
                var group = _program.Groups[script.GroupIndex];
                if (group.IsSubroutine || !_groupActive[script.GroupIndex])
                {
                    continue;
                }
            }

            ExecuteScript(i);
        }
    }

    // ---- script execution (GPL executeScript, non-team-conditions path) ----

    private void ExecuteScript(int scriptIndex)
    {
        var script = _program.Scripts[scriptIndex];
        var state = _scriptStates[scriptIndex];

        if (!state.Active)
        {
            return;
        }

        switch (Difficulty)
        {
            case SimScriptDifficulty.Easy when !script.ActiveInEasy:
            case SimScriptDifficulty.Normal when !script.ActiveInNormal:
            case SimScriptDifficulty.Hard when !script.ActiveInHard:
                return;
        }

        var now = _host.CurrentFrame;
        if (now < state.NextEvalFrame)
        {
            return;
        }

        if (script.EvaluationInterval.Value > 0)
        {
            state.NextEvalFrame = now + script.EvaluationInterval;
            _scriptStates[scriptIndex] = state;
        }

        if (EvaluateConditions(script))
        {
            ExecuteActions(script.ActionsIfTrue);

            if (script.DeactivateUponSuccess)
            {
                state = _scriptStates[scriptIndex];   // actions may have toggled us
                state.Active = false;
                _scriptStates[scriptIndex] = state;
            }
        }
        else if (script.ActionsIfFalse.Count > 0)
        {
            ExecuteActions(script.ActionsIfFalse);

            // GPL deactivates a one-shot after running its FALSE actions too.
            if (script.DeactivateUponSuccess)
            {
                state = _scriptStates[scriptIndex];
                state.Active = false;
                _scriptStates[scriptIndex] = state;
            }
        }
    }

    /// <summary>OR over the clause list; AND with short-circuit inside a clause (GPL shape).</summary>
    private bool EvaluateConditions(SimScript script)
    {
        foreach (var clause in script.OrClauses)
        {
            if (clause.Count == 0)
            {
                continue; // GPL: an empty AND list falls through to the next OR clause.
            }

            var andTerm = true;
            foreach (var condition in clause)
            {
                if (!EvaluateCondition(condition))
                {
                    andTerm = false;
                    break;
                }
            }

            if (andTerm)
            {
                return true;
            }
        }

        return false;
    }

    private bool EvaluateCondition(SimScriptCondition condition)
    {
        var result = condition.Kind switch
        {
            SimScriptConditionKind.True => true,
            SimScriptConditionKind.False => false,
            SimScriptConditionKind.Counter => EvaluateCounter(condition),
            SimScriptConditionKind.Flag => _flags[condition.SlotIndex] == (condition.IntValue != 0),
            SimScriptConditionKind.TimerExpired => EvaluateTimer(condition),
            SimScriptConditionKind.NamedCreated => NamedUnitInWorld(condition),
            SimScriptConditionKind.NamedDestroyed => EvaluateNamedDestroyed(condition),
            SimScriptConditionKind.NamedNotDestroyed => EvaluateNamedExistsAlive(condition),
            SimScriptConditionKind.TeamDestroyed => _host.IsTeamDestroyed(condition.SubjectName),
            SimScriptConditionKind.PlayerAllDestroyed => _host.IsPlayerAllDestroyed(condition.SubjectName),
            _ => EvaluateUnknown(),
        };

        return result != condition.Inverted;
    }

    private bool EvaluateUnknown()
    {
        _unknownConditionsEvaluated++;
        return false;
    }

    private bool EvaluateCounter(SimScriptCondition condition)
    {
        var value = _counters[condition.SlotIndex].Value;
        return condition.Comparison switch
        {
            SimScriptComparison.LessThan => value < condition.IntValue,
            SimScriptComparison.LessEqual => value <= condition.IntValue,
            SimScriptComparison.Equal => value == condition.IntValue,
            SimScriptComparison.GreaterEqual => value >= condition.IntValue,
            SimScriptComparison.Greater => value > condition.IntValue,
            SimScriptComparison.NotEqual => value != condition.IntValue,
            _ => false,
        };
    }

    private bool EvaluateTimer(SimScriptCondition condition)
    {
        var counter = _counters[condition.SlotIndex];
        if (!counter.IsCountdownTimer)
        {
            return false; // Timer hasn't been started yet (GPL).
        }

        return counter.Value < 1; // Timers decrement down to -1 (GPL).
    }

    /// <summary>NAMED_CREATED is "named exists" in the original (its own TODO says so).</summary>
    private bool NamedUnitInWorld(SimScriptCondition condition)
    {
        var exists = _host.TryGetNamedUnit(condition.SubjectName, out _);
        if (exists && condition.NameSlotIndex >= 0)
        {
            _unitEverExisted[condition.NameSlotIndex] = true;
        }

        return exists;
    }

    private bool EvaluateNamedDestroyed(SimScriptCondition condition)
    {
        if (_host.TryGetNamedUnit(condition.SubjectName, out var alive))
        {
            if (condition.NameSlotIndex >= 0)
            {
                _unitEverExisted[condition.NameSlotIndex] = true;
            }

            return !alive;
        }

        // Gone from the world: destroyed only if it ever existed (GPL didUnitExist).
        return condition.NameSlotIndex >= 0 && _unitEverExisted[condition.NameSlotIndex];
    }

    private bool EvaluateNamedExistsAlive(SimScriptCondition condition)
    {
        if (_host.TryGetNamedUnit(condition.SubjectName, out var alive))
        {
            if (condition.NameSlotIndex >= 0)
            {
                _unitEverExisted[condition.NameSlotIndex] = true;
            }

            return alive;
        }

        return false;
    }

    // ---- actions ----

    private void ExecuteActions(IReadOnlyList<SimScriptAction> actions)
    {
        foreach (var action in actions)
        {
            ExecuteAction(action);
        }
    }

    private void ExecuteAction(SimScriptAction action)
    {
        switch (action.Kind)
        {
            case SimScriptActionKind.NoOp:
                break;

            case SimScriptActionKind.SetCounter:
                SetCounterValue(action.SlotIndex, action.IntValue);
                break;

            case SimScriptActionKind.AddCounter:
                SetCounterValue(action.SlotIndex, _counters[action.SlotIndex].Value + action.IntValue);
                break;

            case SimScriptActionKind.SubCounter:
                SetCounterValue(action.SlotIndex, _counters[action.SlotIndex].Value - action.IntValue);
                break;

            case SimScriptActionKind.SetFlag:
                _flags[action.SlotIndex] = action.IntValue != 0;
                break;

            case SimScriptActionKind.SetTimer:
            case SimScriptActionKind.SetMillisecondTimer:
                {
                    // IntValue is frames for both: SET_TIMER is authored in frames; the msec
                    // variant was quantized ceil(seconds x rate) at compile.
                    var counter = _counters[action.SlotIndex];
                    counter.Value = action.IntValue;
                    counter.IsCountdownTimer = true;
                    _counters[action.SlotIndex] = counter;
                    break;
                }

            case SimScriptActionKind.PauseTimer:
                {
                    var counter = _counters[action.SlotIndex];
                    counter.IsCountdownTimer = false;
                    _counters[action.SlotIndex] = counter;
                    break;
                }

            case SimScriptActionKind.RestartTimer:
                {
                    var counter = _counters[action.SlotIndex];
                    if (counter.Value > 0)
                    {
                        counter.IsCountdownTimer = true;
                        _counters[action.SlotIndex] = counter;
                    }
                    break;
                }

            case SimScriptActionKind.EnableScript:
                SetScriptOrGroupActive(action, true);
                break;

            case SimScriptActionKind.DisableScript:
                SetScriptOrGroupActive(action, false);
                break;

            case SimScriptActionKind.CallSubroutine:
                CallSubroutine(action);
                break;

            case SimScriptActionKind.CreateNamedOnTeamAtWaypoint:
                CreateUnit(action, action.Name0);
                break;

            case SimScriptActionKind.CreateUnnamedOnTeamAtWaypoint:
                CreateUnit(action, null);
                break;

            case SimScriptActionKind.TeamAttackTeam:
                _host.TeamAttackTeam(action.Name0, action.Name1);
                break;

            case SimScriptActionKind.NamedAttackNamed:
                _host.NamedAttackNamed(action.Name0, action.Name1);
                break;

            case SimScriptActionKind.TeamTransferToPlayer:
                _host.TeamTransferToPlayer(action.Name0, action.Name1);
                break;

            case SimScriptActionKind.MapExit:
                if (!_mapExitRequested)
                {
                    _mapExitRequested = true;
                    _mapExitFrame = _host.CurrentFrame;
                }
                _host.RequestMapExit();
                break;

            default:
                _unknownActionsExecuted++;
                break;
        }
    }

    private void SetCounterValue(int slot, int value)
    {
        var counter = _counters[slot];
        counter.Value = value;
        _counters[slot] = counter;
    }

    /// <summary>GPL enableScript/disableScript touch BOTH a matching group and a matching script.</summary>
    private void SetScriptOrGroupActive(SimScriptAction action, bool active)
    {
        if (action.TargetGroupIndex >= 0)
        {
            _groupActive[action.TargetGroupIndex] = active;
        }

        if (action.TargetScriptIndex >= 0)
        {
            var state = _scriptStates[action.TargetScriptIndex];
            state.Active = active;
            _scriptStates[action.TargetScriptIndex] = state;
        }
    }

    private void CallSubroutine(SimScriptAction action)
    {
        if (_subroutineDepth >= MaxSubroutineDepth)
        {
            return; // data error; the original would overflow the stack here
        }

        _subroutineDepth++;
        try
        {
            if (action.TargetGroupIndex >= 0)
            {
                var group = _program.Groups[action.TargetGroupIndex];
                if (group.IsSubroutine && _groupActive[action.TargetGroupIndex])
                {
                    // GPL executeScripts over the group's members: subroutine MEMBERS are
                    // skipped, everything else runs through the normal gates.
                    foreach (var memberIndex in group.ScriptIndices)
                    {
                        if (!_program.Scripts[memberIndex].IsSubroutine)
                        {
                            ExecuteScript(memberIndex);
                        }
                    }
                }
            }
            else if (action.TargetScriptIndex >= 0)
            {
                if (_program.Scripts[action.TargetScriptIndex].IsSubroutine)
                {
                    ExecuteScript(action.TargetScriptIndex);
                }
            }
        }
        finally
        {
            _subroutineDepth--;
        }
    }

    private void CreateUnit(SimScriptAction action, string unitName)
    {
        // Duplicate-name guard: a LIVE unit of that name blocks the create (GPL:
        // pOldObj && !isEffectivelyDead -> fail).
        if (!string.IsNullOrEmpty(unitName) &&
            _host.TryGetNamedUnit(unitName, out var alive) && alive)
        {
            return;
        }

        var created = _host.CreateUnitOnTeamAtWaypoint(unitName, action.Name1, action.Name2, action.Name3);
        if (created && action.NameSlotIndex >= 0)
        {
            _unitEverExisted[action.NameSlotIndex] = true;
        }
    }

    // ---- persistence / checksum (single walk, declaration order, all four visitors) ----

    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);

        // Program identity guard: loading state saved against a different program is a
        // data error the CRC/loader must catch, not silently absorb.
        var fingerprint = _programFingerprint;
        xfer.XferUInt("ProgramFingerprint", ref fingerprint);
        if (xfer.Mode == XferMode.Load && fingerprint != _programFingerprint)
        {
            throw new InvalidOperationException(
                "Script-engine state was saved against a different compiled program.");
        }

        xfer.XferList("Counters", _counters, static (IXfer x, ref CounterState item) =>
        {
            x.XferInt("Value", ref item.Value);
            x.XferBool("IsCountdownTimer", ref item.IsCountdownTimer);
        });

        xfer.XferList("Flags", _flags, static (IXfer x, ref bool item) =>
        {
            x.XferBool("Value", ref item);
        });

        xfer.XferList("Scripts", _scriptStates, static (IXfer x, ref ScriptState item) =>
        {
            x.XferBool("Active", ref item.Active);
            x.XferFrame("NextEvalFrame", ref item.NextEvalFrame);
        });

        xfer.XferList("Groups", _groupActive, static (IXfer x, ref bool item) =>
        {
            x.XferBool("Active", ref item);
        });

        xfer.XferList("UnitEverExisted", _unitEverExisted, static (IXfer x, ref bool item) =>
        {
            x.XferBool("Value", ref item);
        });

        xfer.XferBool("MapExitRequested", ref _mapExitRequested);
        xfer.XferFrame("MapExitFrame", ref _mapExitFrame);
        xfer.XferInt("UnknownActionsExecuted", ref _unknownActionsExecuted);
        xfer.XferInt("UnknownConditionsEvaluated", ref _unknownConditionsEvaluated);
    }

    /// <summary>FNV-1a over the program's table names and shape — stable across processes.</summary>
    private static uint ComputeProgramFingerprint(SimScriptProgram program)
    {
        var hash = 2166136261u;

        void AddByte(byte b)
        {
            hash = (hash ^ b) * 16777619u;
        }

        void AddInt(int value)
        {
            AddByte((byte)value);
            AddByte((byte)(value >> 8));
            AddByte((byte)(value >> 16));
            AddByte((byte)(value >> 24));
        }

        void AddString(string s)
        {
            AddInt(s?.Length ?? -1);
            if (s != null)
            {
                foreach (var c in s)
                {
                    AddByte((byte)c);
                    AddByte((byte)(c >> 8));
                }
            }
        }

        AddInt(program.Scripts.Count);
        foreach (var script in program.Scripts)
        {
            AddString(script.Name);
        }

        AddInt(program.Groups.Count);
        foreach (var group in program.Groups)
        {
            AddString(group.Name);
        }

        AddInt(program.CounterNames.Count);
        foreach (var name in program.CounterNames)
        {
            AddString(name);
        }

        AddInt(program.FlagNames.Count);
        foreach (var name in program.FlagNames)
        {
            AddString(name);
        }

        AddInt(program.UnitNames.Count);
        foreach (var name in program.UnitNames)
        {
            AddString(name);
        }

        return hash;
    }

    // ---- test/diagnostic accessors (name lookups are linear; tables are tiny) ----

    public int GetCounterValue(string counterName)
    {
        var slot = IndexOf(_program.CounterNames, counterName);
        return slot >= 0 ? _counters[slot].Value : 0;
    }

    public bool IsTimerRunning(string counterName)
    {
        var slot = IndexOf(_program.CounterNames, counterName);
        return slot >= 0 && _counters[slot].IsCountdownTimer;
    }

    public bool GetFlagValue(string flagName)
    {
        var slot = IndexOf(_program.FlagNames, flagName);
        return slot >= 0 && _flags[slot];
    }

    public bool IsScriptActive(string scriptName)
    {
        for (var i = 0; i < _program.Scripts.Count; i++)
        {
            if (string.Equals(_program.Scripts[i].Name, scriptName, StringComparison.Ordinal))
            {
                return _scriptStates[i].Active;
            }
        }

        return false;
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
