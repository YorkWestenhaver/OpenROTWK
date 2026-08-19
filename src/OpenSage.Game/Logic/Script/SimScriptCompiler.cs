// S8 script-engine runtime (subset) — the compile boundary (deliberately NOT [SimState]).
//
// Turns the parsed map script assets (OpenSage.Scripting.Script / ScriptList /
// PlayerScriptsList, produced by the round-3 map parser) into the immutable, float-free
// SimScriptProgram the [SimState] runtime consumes. This file is the ONE place map floats
// touch script data: SET_MILLISECOND_TIMER's seconds argument crosses through the F4 wire
// boundary (Fix64.FromWireFloat over the stored float32 bits) and is quantized to logic
// frames with the GPL rounding (REAL_TO_INT_CEIL of msecs x frames-per-msec == ceil(seconds
// x logic rate)) — no float ever reaches SimScriptEngine.
//
// Identification: BFME2 re-used Generals-era content-type ids (e.g. 496 is GateReady in ZH
// and MAP_EXIT in BFME2), so a compiled kind is keyed on the asset's stored INTERNAL NAME
// whenever the map carries one (action v2+ / condition v4+ — all BFME2/AotR maps do), with
// the ZH numeric id as the documented fallback for name-less Generals-era maps.
//
// Walk order: the emitted Scripts list is the original evaluation order — players ascending,
// per player the top-level scripts in map order, then groups in map order (nested groups
// flattened depth-first), member scripts in map order (ScriptEngine::update). Counter, flag
// and unit-name slots are allocated in first-reference order over that same walk (recorded
// deviation SR-D1: the original allocates lazily at first runtime evaluation; indices never
// affect observable behavior).

using System;
using System.Collections.Generic;
using OpenSage.Data.Map;
using OpenSage.Scripting;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Script;

public static class SimScriptCompiler
{
    public static SimScriptProgram Compile(PlayerScriptsList playerScripts)
    {
        return Compile(playerScripts?.ScriptLists ?? []);
    }

    public static SimScriptProgram Compile(IReadOnlyList<ScriptList> scriptLists)
    {
        var builder = new Builder();

        for (var playerIndex = 0; playerIndex < scriptLists.Count; playerIndex++)
        {
            var list = scriptLists[playerIndex];
            if (list == null)
            {
                continue;
            }

            foreach (var script in list.Scripts)
            {
                builder.AddScript(script, playerIndex, groupIndex: -1);
            }

            foreach (var group in list.ScriptGroups)
            {
                builder.AddGroup(group, playerIndex);
            }
        }

        builder.ResolveScriptTargets();
        return builder.Build();
    }

    private sealed class Builder
    {
        private readonly List<SimScript> _scripts = new();
        private readonly List<SimScriptGroup> _groups = new();
        private readonly List<string> _counterNames = new();
        private readonly List<string> _flagNames = new();
        private readonly List<string> _unitNames = new();
        private readonly List<uint> _unknownConditionIds = new();
        private readonly List<uint> _unknownActionIds = new();

        // Enable/Disable/CallSubroutine targets can name scripts that appear later in the
        // walk, so actions record the name and are patched in a second pass.
        private readonly List<(SimScriptAction Action, string TargetName, int ListIndex, bool IfTrue, int ScriptIndex, int ActionIndex)> _pendingTargets = new();

        public void AddScript(OpenSage.Scripting.Script script, int playerIndex, int groupIndex)
        {
            var orClauses = new List<IReadOnlyList<SimScriptCondition>>();
            foreach (var orCondition in script.OrConditions ?? [])
            {
                var clause = new List<SimScriptCondition>();
                foreach (var condition in orCondition.Conditions ?? [])
                {
                    if (!condition.Enabled)
                    {
                        continue; // WorldBuilder "commented out"
                    }

                    clause.Add(CompileCondition(condition));
                }

                orClauses.Add(clause);
            }

            var compiled = new SimScript
            {
                Name = script.Name ?? string.Empty,
                PlayerIndex = playerIndex,
                GroupIndex = groupIndex,
                InitiallyActive = script.IsActive,
                DeactivateUponSuccess = script.DeactivateUponSuccess,
                IsSubroutine = script.IsSubroutine,
                ActiveInEasy = script.ActiveInEasy,
                ActiveInNormal = script.ActiveInMedium,
                ActiveInHard = script.ActiveInHard,
                EvaluationInterval = new LogicFrameSpan(
                    script.EvaluationInterval * (uint)SimScriptEngine.LogicFramesPerSecond),
                OrClauses = orClauses,
                ActionsIfTrue = CompileActions(script.ActionsIfTrue),
                ActionsIfFalse = CompileActions(script.ActionsIfFalse),
            };

            _scripts.Add(compiled);
        }

        public void AddGroup(ScriptGroup group, int playerIndex)
        {
            var groupIndex = _groups.Count;
            _groups.Add(null); // reserve the slot so member scripts can point at it

            var memberIndices = new List<int>();
            foreach (var script in group.Scripts ?? [])
            {
                memberIndices.Add(_scripts.Count);
                AddScript(script, playerIndex, groupIndex);
            }

            _groups[groupIndex] = new SimScriptGroup
            {
                Name = group.Name ?? string.Empty,
                PlayerIndex = playerIndex,
                InitiallyActive = group.IsActive,
                IsSubroutine = group.IsSubroutine,
                ScriptIndices = memberIndices,
            };

            // WorldBuilder folders nest; the original's walk flattens depth-first.
            foreach (var nested in group.Groups ?? [])
            {
                AddGroup(nested, playerIndex);
            }
        }

        // ---- conditions ----

        private SimScriptCondition CompileCondition(ScriptCondition condition)
        {
            var name = condition.InternalName?.Name;
            var raw = (uint)condition.ContentType;
            var kind = name != null ? ConditionKindByName(name) : ConditionKindByZhId(raw);

            var args = condition.Arguments ?? [];

            switch (kind)
            {
                case SimScriptConditionKind.True:
                case SimScriptConditionKind.False:
                    return new SimScriptCondition { Kind = kind, Inverted = condition.IsInverted, RawContentType = raw };

                case SimScriptConditionKind.Counter:
                    return new SimScriptCondition
                    {
                        Kind = kind,
                        Inverted = condition.IsInverted,
                        SlotIndex = Allocate(_counterNames, StringArg(args, 0)),
                        Comparison = (SimScriptComparison)IntArg(args, 1),
                        IntValue = IntArg(args, 2),
                        RawContentType = raw,
                    };

                case SimScriptConditionKind.Flag:
                    return new SimScriptCondition
                    {
                        Kind = kind,
                        Inverted = condition.IsInverted,
                        SlotIndex = Allocate(_flagNames, StringArg(args, 0)),
                        IntValue = IntArg(args, 1),
                        RawContentType = raw,
                    };

                case SimScriptConditionKind.TimerExpired:
                    return new SimScriptCondition
                    {
                        Kind = kind,
                        Inverted = condition.IsInverted,
                        SlotIndex = Allocate(_counterNames, StringArg(args, 0)),
                        RawContentType = raw,
                    };

                case SimScriptConditionKind.NamedCreated:
                case SimScriptConditionKind.NamedDestroyed:
                case SimScriptConditionKind.NamedNotDestroyed:
                {
                    var unit = StringArg(args, 0);
                    return new SimScriptCondition
                    {
                        Kind = kind,
                        Inverted = condition.IsInverted,
                        NameSlotIndex = Allocate(_unitNames, unit),
                        SubjectName = unit,
                        RawContentType = raw,
                    };
                }

                case SimScriptConditionKind.TeamDestroyed:
                case SimScriptConditionKind.PlayerAllDestroyed:
                    return new SimScriptCondition
                    {
                        Kind = kind,
                        Inverted = condition.IsInverted,
                        SubjectName = StringArg(args, 0),
                        RawContentType = raw,
                    };

                default:
                    _unknownConditionIds.Add(raw);
                    return new SimScriptCondition { Kind = SimScriptConditionKind.Unknown, Inverted = condition.IsInverted, RawContentType = raw };
            }
        }

        private static SimScriptConditionKind ConditionKindByName(string name) => name switch
        {
            "CONDITION_TRUE" => SimScriptConditionKind.True,
            "CONDITION_FALSE" => SimScriptConditionKind.False,
            "COUNTER" => SimScriptConditionKind.Counter,
            "FLAG" => SimScriptConditionKind.Flag,
            "TIMER_EXPIRED" => SimScriptConditionKind.TimerExpired,
            "NAMED_CREATED" => SimScriptConditionKind.NamedCreated,
            "NAMED_DESTROYED" => SimScriptConditionKind.NamedDestroyed,
            "NAMED_NOT_DESTROYED" => SimScriptConditionKind.NamedNotDestroyed,
            "TEAM_DESTROYED" => SimScriptConditionKind.TeamDestroyed,
            "PLAYER_ALL_DESTROYED" => SimScriptConditionKind.PlayerAllDestroyed,
            _ => SimScriptConditionKind.Unknown,
        };

        /// <summary>Generals-era numeric fallback (maps too old to store internal names).</summary>
        private static SimScriptConditionKind ConditionKindByZhId(uint id) => id switch
        {
            0 => SimScriptConditionKind.False,
            1 => SimScriptConditionKind.Counter,
            2 => SimScriptConditionKind.Flag,
            3 => SimScriptConditionKind.True,
            4 => SimScriptConditionKind.TimerExpired,
            5 => SimScriptConditionKind.PlayerAllDestroyed,
            8 => SimScriptConditionKind.TeamDestroyed,
            15 => SimScriptConditionKind.NamedDestroyed,
            16 => SimScriptConditionKind.NamedNotDestroyed,
            24 => SimScriptConditionKind.NamedCreated,
            _ => SimScriptConditionKind.Unknown,
        };

        // ---- actions ----

        private IReadOnlyList<SimScriptAction> CompileActions(ScriptAction[] actions)
        {
            var result = new List<SimScriptAction>();
            foreach (var action in actions ?? [])
            {
                if (!action.Enabled)
                {
                    continue;
                }

                result.Add(CompileAction(action, result));
            }

            return result;
        }

        private SimScriptAction CompileAction(ScriptAction action, List<SimScriptAction> owningList)
        {
            var name = action.InternalName?.Name;
            var raw = (uint)action.ContentType;
            var kind = name != null ? ActionKindByName(name) : ActionKindByZhId(raw);

            var args = action.Arguments ?? [];

            switch (kind)
            {
                case SimScriptActionKind.NoOp:
                case SimScriptActionKind.MapExit:
                    return new SimScriptAction { Kind = kind, RawContentType = raw };

                case SimScriptActionKind.SetCounter:
                    return new SimScriptAction
                    {
                        Kind = kind,
                        SlotIndex = Allocate(_counterNames, StringArg(args, 0)),
                        IntValue = IntArg(args, 1),
                        RawContentType = raw,
                    };

                // GPL parameter order: p0 = value, p1 = counter.
                case SimScriptActionKind.AddCounter:
                case SimScriptActionKind.SubCounter:
                    return new SimScriptAction
                    {
                        Kind = kind,
                        SlotIndex = Allocate(_counterNames, StringArg(args, 1)),
                        IntValue = IntArg(args, 0),
                        RawContentType = raw,
                    };

                case SimScriptActionKind.SetFlag:
                    return new SimScriptAction
                    {
                        Kind = kind,
                        SlotIndex = Allocate(_flagNames, StringArg(args, 0)),
                        IntValue = IntArg(args, 1),
                        RawContentType = raw,
                    };

                case SimScriptActionKind.SetTimer:
                    return new SimScriptAction
                    {
                        Kind = kind,
                        SlotIndex = Allocate(_counterNames, StringArg(args, 0)),
                        IntValue = IntArg(args, 1),
                        RawContentType = raw,
                    };

                case SimScriptActionKind.SetMillisecondTimer:
                    return new SimScriptAction
                    {
                        Kind = kind,
                        SlotIndex = Allocate(_counterNames, StringArg(args, 0)),
                        IntValue = QuantizeSecondsToFrames(args, 1),
                        RawContentType = raw,
                    };

                case SimScriptActionKind.PauseTimer:
                case SimScriptActionKind.RestartTimer:
                    return new SimScriptAction
                    {
                        Kind = kind,
                        SlotIndex = Allocate(_counterNames, StringArg(args, 0)),
                        RawContentType = raw,
                    };

                case SimScriptActionKind.EnableScript:
                case SimScriptActionKind.DisableScript:
                case SimScriptActionKind.CallSubroutine:
                {
                    var compiled = new SimScriptAction
                    {
                        Kind = kind,
                        Name0 = StringArg(args, 0),
                        RawContentType = raw,
                    };
                    _pendingTargets.Add((compiled, compiled.Name0, owningList.Count, false, -1, -1));
                    return compiled;
                }

                case SimScriptActionKind.CreateNamedOnTeamAtWaypoint:
                {
                    var unit = StringArg(args, 0);
                    return new SimScriptAction
                    {
                        Kind = kind,
                        NameSlotIndex = Allocate(_unitNames, unit),
                        Name0 = unit,
                        Name1 = StringArg(args, 1),   // object type
                        Name2 = StringArg(args, 2),   // team
                        Name3 = StringArg(args, 3),   // waypoint
                        RawContentType = raw,
                    };
                }

                case SimScriptActionKind.CreateUnnamedOnTeamAtWaypoint:
                    return new SimScriptAction
                    {
                        Kind = kind,
                        Name1 = StringArg(args, 0),   // object type
                        Name2 = StringArg(args, 1),   // team
                        Name3 = StringArg(args, 2),   // waypoint
                        RawContentType = raw,
                    };

                case SimScriptActionKind.TeamAttackTeam:
                case SimScriptActionKind.NamedAttackNamed:
                    return new SimScriptAction
                    {
                        Kind = kind,
                        Name0 = StringArg(args, 0),
                        Name1 = StringArg(args, 1),
                        RawContentType = raw,
                    };

                default:
                    _unknownActionIds.Add(raw);
                    return new SimScriptAction { Kind = SimScriptActionKind.Unknown, RawContentType = raw };
            }
        }

        private static SimScriptActionKind ActionKindByName(string name) => name switch
        {
            "NO_OP" => SimScriptActionKind.NoOp,
            "SET_COUNTER" => SimScriptActionKind.SetCounter,
            "INCREMENT_COUNTER" => SimScriptActionKind.AddCounter,
            "DECREMENT_COUNTER" => SimScriptActionKind.SubCounter,
            "SET_FLAG" => SimScriptActionKind.SetFlag,
            "SET_TIMER" => SimScriptActionKind.SetTimer,
            "SET_MILLISECOND_TIMER" => SimScriptActionKind.SetMillisecondTimer,
            "STOP_TIMER" => SimScriptActionKind.PauseTimer,
            "RESTART_TIMER" => SimScriptActionKind.RestartTimer,
            "ENABLE_SCRIPT" => SimScriptActionKind.EnableScript,
            "DISABLE_SCRIPT" => SimScriptActionKind.DisableScript,
            "CALL_SUBROUTINE" => SimScriptActionKind.CallSubroutine,
            "CREATE_NAMED_ON_TEAM_AT_WAYPOINT" => SimScriptActionKind.CreateNamedOnTeamAtWaypoint,
            "CREATE_UNNAMED_ON_TEAM_AT_WAYPOINT" => SimScriptActionKind.CreateUnnamedOnTeamAtWaypoint,
            "TEAM_ATTACK_TEAM" => SimScriptActionKind.TeamAttackTeam,
            "NAMED_ATTACK_NAMED" => SimScriptActionKind.NamedAttackNamed,
            "MAP_EXIT" => SimScriptActionKind.MapExit,
            _ => SimScriptActionKind.Unknown,
        };

        /// <summary>
        /// Generals-era numeric fallback. NOTE: deliberately NO entry for MAP_EXIT — its
        /// BFME2 id (496) collides with ZH GateReady, which is exactly why internal names
        /// are the primary key.
        /// </summary>
        private static SimScriptActionKind ActionKindByZhId(uint id) => id switch
        {
            1 => SimScriptActionKind.SetFlag,
            2 => SimScriptActionKind.SetCounter,
            5 => SimScriptActionKind.NoOp,
            6 => SimScriptActionKind.SetTimer,
            8 => SimScriptActionKind.EnableScript,
            9 => SimScriptActionKind.DisableScript,
            10 => SimScriptActionKind.CallSubroutine,
            15 => SimScriptActionKind.AddCounter,
            16 => SimScriptActionKind.SubCounter,
            20 => SimScriptActionKind.SetMillisecondTimer,
            33 => SimScriptActionKind.TeamAttackTeam,
            39 => SimScriptActionKind.NamedAttackNamed,
            40 => SimScriptActionKind.CreateNamedOnTeamAtWaypoint,
            41 => SimScriptActionKind.CreateUnnamedOnTeamAtWaypoint,
            _ => SimScriptActionKind.Unknown,
        };

        // ---- target patching (Enable/Disable/CallSubroutine) ----

        public void ResolveScriptTargets()
        {
            foreach (var pending in _pendingTargets)
            {
                var scriptIndex = FindScriptByName(pending.TargetName);
                var groupIndex = FindGroupByName(pending.TargetName);

                // SimScriptAction is init-only; rebuild the action with targets resolved and
                // swap it in place wherever it lives. Actions are reference-compared here, so
                // scan all lists (cheap: compile-time only).
                var resolved = new SimScriptAction
                {
                    Kind = pending.Action.Kind,
                    Name0 = pending.Action.Name0,
                    RawContentType = pending.Action.RawContentType,
                    TargetScriptIndex = scriptIndex,
                    TargetGroupIndex = groupIndex,
                };

                ReplaceAction(pending.Action, resolved);
            }

            _pendingTargets.Clear();
        }

        private void ReplaceAction(SimScriptAction oldAction, SimScriptAction newAction)
        {
            for (var s = 0; s < _scripts.Count; s++)
            {
                var script = _scripts[s];
                var replacedTrue = ReplaceIn(script.ActionsIfTrue, oldAction, newAction);
                var replacedFalse = ReplaceIn(script.ActionsIfFalse, oldAction, newAction);
                if (replacedTrue || replacedFalse)
                {
                    return;
                }
            }
        }

        private static bool ReplaceIn(IReadOnlyList<SimScriptAction> list, SimScriptAction oldAction, SimScriptAction newAction)
        {
            if (list is List<SimScriptAction> mutable)
            {
                for (var i = 0; i < mutable.Count; i++)
                {
                    if (ReferenceEquals(mutable[i], oldAction))
                    {
                        mutable[i] = newAction;
                        return true;
                    }
                }
            }

            return false;
        }

        private int FindScriptByName(string name)
        {
            for (var i = 0; i < _scripts.Count; i++)
            {
                if (string.Equals(_scripts[i].Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private int FindGroupByName(string name)
        {
            for (var i = 0; i < _groups.Count; i++)
            {
                if (string.Equals(_groups[i]?.Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        // ---- helpers ----

        private static int Allocate(List<string> table, string name)
        {
            name ??= string.Empty;
            for (var i = 0; i < table.Count; i++)
            {
                if (string.Equals(table[i], name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            table.Add(name);
            return table.Count - 1;
        }

        private static string StringArg(ScriptArgument[] args, int index) =>
            index < args.Length ? args[index].StringValue : null;

        private static int IntArg(ScriptArgument[] args, int index) =>
            index < args.Length ? args[index].IntValue ?? 0 : 0;

        /// <summary>
        /// GPL setTimer(millisecondTimer=true): frames = REAL_TO_INT_CEIL(seconds x 1000 x
        /// LOGICFRAMES_PER_MSEC) == ceil(seconds x logic rate). The float32 crosses the F4
        /// wire boundary bit-exactly; ceil runs in Fix64.
        /// </summary>
        private static int QuantizeSecondsToFrames(ScriptArgument[] args, int index)
        {
            if (index >= args.Length)
            {
                return 0;
            }

            var floatValue = args[index].FloatValue ?? 0f;
            var seconds = Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(floatValue));
            var rate = Fix64.FromRaw((long)SimScriptEngine.LogicFramesPerSecond << 32);
            var frames = Fix64.Ceiling(seconds * rate);
            return (int)(frames.RawValue >> 32);
        }

        public SimScriptProgram Build() => new()
        {
            Scripts = _scripts,
            Groups = _groups,
            CounterNames = _counterNames,
            FlagNames = _flagNames,
            UnitNames = _unitNames,
            UnknownConditionIds = _unknownConditionIds,
            UnknownActionIds = _unknownActionIds,
        };
    }
}
