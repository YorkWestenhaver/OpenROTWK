// S8 script-engine runtime (subset) — the COMPILED, IMMUTABLE program.
//
// Behavioral reference (clean-room, semantics only): generals-gpl GeneralsMD
// ScriptEngine.cpp / ScriptActions.cpp / ScriptConditions.cpp. The original evaluates the
// parsed map Script assets directly, lazily rewriting name parameters into table indices
// as it runs. We instead compile the parsed assets ONCE (SimScriptCompiler, the non-SimState
// boundary file) into this immutable Fix64/int-only program, so the [SimState] runtime never
// touches a float and every name is resolved to a deterministic slot index up front.
// Allocation order of the counter/flag/name tables is program walk order, which makes the
// tables a pure function of the map — a recorded deviation (SR-D1 in the design note) from
// the original's lazy first-reference allocation; observable behavior is identical because
// indices never leak into results.
//
// Everything here is configuration, not state: the runtime keeps its mutable state in
// SimScriptEngine and refers back to these tables by index.

using System.Collections.Generic;
using OpenSage.SimCore;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Script;

/// <summary>The condition subset the runtime evaluates (design note §2 lists in/deferred).</summary>
public enum SimScriptConditionKind
{
    /// <summary>Unrecognized condition — evaluates false, counted for diagnostics.</summary>
    Unknown = 0,
    True,               // CONDITION_TRUE
    False,              // CONDITION_FALSE
    Counter,            // COUNTER <name> <cmp> <int>
    Flag,               // FLAG <name> <bool>
    TimerExpired,       // TIMER_EXPIRED <counterName>
    NamedCreated,       // NAMED_CREATED <unit> (GPL: actually "named exists")
    NamedDestroyed,     // NAMED_DESTROYED <unit>
    NamedNotDestroyed,  // NAMED_NOT_DESTROYED <unit>
    TeamDestroyed,      // TEAM_DESTROYED <team>
    PlayerAllDestroyed, // PLAYER_ALL_DESTROYED <player>
}

/// <summary>The action subset the runtime executes.</summary>
public enum SimScriptActionKind
{
    /// <summary>Unrecognized action — recorded no-op, counted for diagnostics.</summary>
    Unknown = 0,
    NoOp,                           // NO_OP
    SetCounter,                     // SET_COUNTER <counter> <value>
    AddCounter,                     // INCREMENT_COUNTER <value> <counter> (GPL parameter order)
    SubCounter,                     // DECREMENT_COUNTER <value> <counter>
    SetFlag,                        // SET_FLAG <flag> <bool>
    SetTimer,                       // SET_TIMER <counter> <frames>
    SetMillisecondTimer,            // SET_MILLISECOND_TIMER <counter> <seconds> (frames pre-quantized at compile)
    PauseTimer,                     // STOP_TIMER <counter>
    RestartTimer,                   // RESTART_TIMER <counter>
    EnableScript,                   // ENABLE_SCRIPT <script-or-group>
    DisableScript,                  // DISABLE_SCRIPT <script-or-group>
    CallSubroutine,                 // CALL_SUBROUTINE <script-or-group>
    CreateNamedOnTeamAtWaypoint,    // CREATE_NAMED_ON_TEAM_AT_WAYPOINT <unit> <objType> <team> <waypoint>
    CreateUnnamedOnTeamAtWaypoint,  // CREATE_UNNAMED_ON_TEAM_AT_WAYPOINT <objType> <team> <waypoint>
    TeamAttackTeam,                 // TEAM_ATTACK_TEAM <attackerTeam> <victimTeam>
    NamedAttackNamed,               // NAMED_ATTACK_NAMED <attacker> <victim>
    TeamTransferToPlayer,           // TEAM_TRANSFER_TO_PLAYER <team> <player>
    MapExit,                        // MAP_EXIT (BFME2-only, content id 496)
}

/// <summary>WorldBuilder comparison operators, in the map file's own encoding.</summary>
public enum SimScriptComparison
{
    LessThan = 0,
    LessEqual = 1,
    Equal = 2,
    GreaterEqual = 3,
    Greater = 4,
    NotEqual = 5,
}

/// <summary>
/// One compiled condition. String parameters are resolved to table indices at compile time;
/// the string is retained for host queries (unit/team/player names live in the world, not in
/// engine tables).
/// </summary>
[SimState]
public sealed class SimScriptCondition
{
    public SimScriptConditionKind Kind { get; init; }

    /// <summary>BFME2 condition flag (v5+): invert the evaluated result.</summary>
    public bool Inverted { get; init; }

    /// <summary>Counter/flag slot for Counter/Flag/TimerExpired; -1 otherwise.</summary>
    public int SlotIndex { get; init; } = -1;

    /// <summary>Unit-name slot (ever-existed bookkeeping) for the Named* conditions; -1 otherwise.</summary>
    public int NameSlotIndex { get; init; } = -1;

    public SimScriptComparison Comparison { get; init; }

    /// <summary>Comparison operand (Counter), or the expected value 0/1 (Flag).</summary>
    public int IntValue { get; init; }

    /// <summary>Unit/team/player name for the world-querying conditions; null otherwise.</summary>
    public string SubjectName { get; init; }

    /// <summary>The raw map content-type id, kept for diagnostics on Unknown.</summary>
    public uint RawContentType { get; init; }
}

/// <summary>One compiled action.</summary>
[SimState]
public sealed class SimScriptAction
{
    public SimScriptActionKind Kind { get; init; }

    /// <summary>Counter/flag slot for the state actions; -1 otherwise.</summary>
    public int SlotIndex { get; init; } = -1;

    /// <summary>Unit-name slot for CreateNamed; -1 otherwise.</summary>
    public int NameSlotIndex { get; init; } = -1;

    /// <summary>
    /// SetCounter/Add/Sub value, SetTimer frames, or the PRE-QUANTIZED frame count for
    /// SetMillisecondTimer (ceil(seconds x logic rate), computed at compile through the F4
    /// wire-float boundary — no float reaches the runtime).
    /// </summary>
    public int IntValue { get; init; }

    /// <summary>First name argument (unit/team/script name), per-kind meaning.</summary>
    public string Name0 { get; init; }

    /// <summary>Second name argument (object type / victim), per-kind meaning.</summary>
    public string Name1 { get; init; }

    /// <summary>Third name argument (team), per-kind meaning.</summary>
    public string Name2 { get; init; }

    /// <summary>Fourth name argument (waypoint), per-kind meaning.</summary>
    public string Name3 { get; init; }

    /// <summary>Script/group index target for Enable/Disable/CallSubroutine; -1 = unresolved.</summary>
    public int TargetScriptIndex { get; init; } = -1;

    public int TargetGroupIndex { get; init; } = -1;

    public uint RawContentType { get; init; }
}

/// <summary>One compiled script. AND-lists inside an OR-list, exactly the map shape.</summary>
[SimState]
public sealed class SimScript
{
    public string Name { get; init; }
    public int PlayerIndex { get; init; }

    /// <summary>-1 for a top-level script, else index into <see cref="SimScriptProgram.Groups"/>.</summary>
    public int GroupIndex { get; init; } = -1;

    public bool InitiallyActive { get; init; }
    public bool DeactivateUponSuccess { get; init; }
    public bool IsSubroutine { get; init; }
    public bool ActiveInEasy { get; init; }
    public bool ActiveInNormal { get; init; }
    public bool ActiveInHard { get; init; }

    /// <summary>GPL delayEvalSeconds x logic rate; zero = evaluate every frame.</summary>
    public LogicFrameSpan EvaluationInterval { get; init; }

    /// <summary>Outer list OR'ed; each inner array AND'ed with short-circuit (GPL shape).</summary>
    public IReadOnlyList<IReadOnlyList<SimScriptCondition>> OrClauses { get; init; }

    public IReadOnlyList<SimScriptAction> ActionsIfTrue { get; init; }
    public IReadOnlyList<SimScriptAction> ActionsIfFalse { get; init; }
}

/// <summary>A WorldBuilder script folder: an activation gate around its member scripts.</summary>
[SimState]
public sealed class SimScriptGroup
{
    public string Name { get; init; }
    public int PlayerIndex { get; init; }
    public bool InitiallyActive { get; init; }
    public bool IsSubroutine { get; init; }

    /// <summary>Member scripts as indices into <see cref="SimScriptProgram.Scripts"/>, in map order.</summary>
    public IReadOnlyList<int> ScriptIndices { get; init; }
}

/// <summary>
/// The whole compiled program. <see cref="Scripts"/> holds every script of every player in
/// THE evaluation order: players ascending, per player top-level scripts in map order, then
/// groups in map order (nested groups flattened depth-first), scripts inside each group in
/// map order — the original's exact walk (ScriptEngine::update → executeScripts).
/// </summary>
[SimState]
public sealed class SimScriptProgram
{
    public static readonly SimScriptProgram Empty = new()
    {
        Scripts = [],
        Groups = [],
        CounterNames = [],
        FlagNames = [],
        UnitNames = [],
        UnknownConditionIds = [],
        UnknownActionIds = [],
    };

    public IReadOnlyList<SimScript> Scripts { get; init; }
    public IReadOnlyList<SimScriptGroup> Groups { get; init; }

    /// <summary>Counter/timer table names, slot order (timers ARE counters, GPL).</summary>
    public IReadOnlyList<string> CounterNames { get; init; }

    public IReadOnlyList<string> FlagNames { get; init; }

    /// <summary>Unit-name slots for ever-existed bookkeeping (Named* conditions + creates).</summary>
    public IReadOnlyList<string> UnitNames { get; init; }

    /// <summary>Raw content ids the compiler could not map — surfaced for diagnostics.</summary>
    public IReadOnlyList<uint> UnknownConditionIds { get; init; }
    public IReadOnlyList<uint> UnknownActionIds { get; init; }
}
