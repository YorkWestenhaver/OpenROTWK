// TemporarilyDefectUpdate - R13 port (data-derivable per task; no exact GPL sibling for the
// auto-revert mechanic itself - see the full source list and F-TDU-1/F-TDU-2/F-TDU-3 in
// bfme2-workbench/research/modules-r13/specs/TemporarilyDefectUpdateModuleData.md).
//
// F-TDU-1 (audit-rationale correction, load-bearing): `ObjectDefectionHelper.cpp`
// (generals-community GeneralsMD Object/Helper/ObjectDefectionHelper.cpp) only ever models a
// "capture a start frame, schedule an end frame, revert something at expiry" timer applied to
// the COSMETIC `m_undetectedDefector` flag - never to team membership - and GPL's
// `Object::defect()` (Object.cpp:6275-6390) is itself a one-way, PERMANENT team switch with no
// revert path anywhere in its body. This module's actual "switch team, then revert after
// DefectDuration" behavior is grounded instead in this repo's own shipped, licensed
// `data/AgeoftheRing/aotr/data/ini/default/object.ini:250-269` comment (an `InheritableModule`
// attached to every object by default, warning that `DefectDuration` must stay below the
// triggering special power's `ReloadTime` "or all manner of grief will happen with defected
// units") and independently corroborated by
// `DominateEnemySpecialPowerModuleData.PermanentlyConvert` (a parsed opt-out field implying
// "revert after a duration" is the default). Only the TIMER SHAPE is borrowed from
// `ObjectDefectionHelper`; the revert target (Team, not a flag) is data-derived, not GPL-cited.
//
// F-TDU-2 (ISimContext gap, fixed by this packet): no member of ISimContext/IGameLogic resolved
// a Team by id (needed because a Team reference cannot itself live in Xfer state - only its
// uint Id can, via XferUInt). Added `Team FindTeamById(uint id)` to IGameLogic
// (Logic/Object/ISimContext.cs) and its one-line passthrough to
// `_engine.Game.TeamFactory.FindTeamById(id)` in the SimContext adapter (Logic/Object/SimContext.cs),
// following ISimContext's documented "grow one member at a time" policy - the same shape as
// the existing `GetObjectById` passthrough.
//
// F-TDU-3 (scope boundary, NOT touched by this packet): `GameObject.Defect()` is a real, empty
// stub (`// TODO(Port): Implement this.`) with two live callers that both assume a PERMANENT
// switch (`PhysicsBehavior.cs:971` unmanned-vehicle capture, `FlightDeckBehavior.cs:391`
// carrier-capture cascade). Wiring this module's revert logic through `Defect()` would silently
// turn both of those permanent captures into temporary ones - a correctness regression. This
// module therefore gets its own entry point, `StartTemporaryDefect(Team)`, which a future
// `DominateEnemySpecialPower` port (ModuleData already exists, no behavior class yet) is
// expected to call directly. `Defect()`'s body (production cancel, radar-infiltration ping,
// undetected-flag wiring, contain-kick-out cascade - Object.cpp L6275-6390+) remains a separate,
// larger, future task.
//
// Every mutable sim field appears in Xfer exactly once; field order is OUR choice (F9) since
// GPL's own `ObjectDefectionHelper::xfer` persists an entirely different (cosmetic-timer)
// state shape, not this module's.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class TemporarilyDefectUpdate : UpdateModule
{
    private readonly TemporarilyDefectUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>True while the object is currently defected and awaiting its revert.</summary>
    private bool _active;

    /// <summary>
    /// The object's pre-defection team, captured by id (not by reference - a Team reference has
    /// no Xfer-hostable form, F-TDU-2). GPL/engine-idiom analog of
    /// <c>GarrisonContain._originalTeamId</c>; NOT GPL's <c>m_defectionHelper</c> (a different
    /// module, F-TDU-1/F-TDU-3).
    /// </summary>
    private uint _originalTeamId;

    /// <summary>
    /// Frame the defection reverts on (GPL <c>ObjectDefectionHelper::m_defectionDetectionEnd</c>
    /// idiom, applied to team-revert per F-TDU-1). Meaningless while <see cref="_active"/> is
    /// false.
    /// </summary>
    private LogicFrame _revertFrame;

    /// <summary>The frozen contract ctor for ported modules (api-freeze-v1 §3 item 2).</summary>
    internal TemporarilyDefectUpdate(GameObject gameObject, ISimContext context, TemporarilyDefectUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // Passive by default (§1 step 1): per the object.ini comment, this module is a
        // universally-attached, normally-dormant module that only acts once something else
        // (eventually DominateEnemySpecialPower, out of scope here) calls StartTemporaryDefect.
        // Mirrors ObjectDefectionHelper.cpp's own "nothing to do yet, don't tick" dormancy
        // guard, applied at construction since this module's dormant condition is knowable
        // immediately.
        _active = false;
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    /// <summary>
    /// Entry point (reserved name, §4 of the spec): the module's own analog of
    /// <c>ObjectDefectionHelper::startDefectionTimer</c>. Called by whatever module the
    /// object's DominateEnemySpecialPower behavior eventually resolves to - not by anything in
    /// this packet.
    /// </summary>
    internal void StartTemporaryDefect(Team newTeam)
    {
        // GPL Object::defect()'s own "can't defect from my own team, that would be silly" guard
        // (Object.cpp L6287-6288) - a plain sanity check, not part of the permanent-switch
        // machinery F-TDU-3 scopes out.
        if (newTeam == null || newTeam == GameObject.Team)
        {
            return;
        }

        if (!_active)
        {
            // First activation: capture the true original team.
            _originalTeamId = GameObject.Team.Id;
            _active = true;
        }
        // Re-entrancy guard: a second dominate landing before the first reverts does NOT
        // overwrite _originalTeamId - it keeps the true original team captured on the *first*
        // activation, and only refreshes _revertFrame below. Defensive default (not GPL-cited);
        // preserves "eventually return to the real original team" rather than leaving the unit
        // defected to whatever team happened to trigger the second call.

        GameObject.Team = newTeam;
        _revertFrame = Context.CurrentFrame + _data.DefectDuration;

        // Sleep exactly until the revert is due - nothing else in this module has any
        // per-frame effect (unlike ObjectDefectionHelper's own per-frame tick, which exists
        // only to drive its FX flash, a different module this one has no equivalent of).
        SetWakeFrame(UpdateSleepTime.Frames(_data.DefectDuration));
    }

    public override UpdateSleepTime Update()
    {
        if (!_active)
        {
            // Defensive; should not normally be reached since the ctor and the post-revert
            // path already schedule Forever.
            return UpdateSleepTime.Forever;
        }

        var now = Context.CurrentFrame;

        if (now < _revertFrame)
        {
            // Defensive re-guard, same posture as RubbleRiseUpdate's analogous guard - should
            // not normally fire given the exact SetWakeFrame scheduling above, kept explicit
            // rather than assumed.
            return UpdateSleepTime.Frames(_revertFrame - now);
        }

        if (GameObject.IsEffectivelyDead)
        {
            // The object is gone - nothing to revert. Mirrors ObjectDefectionHelper.cpp's
            // dead-object early-out, applied to this module's own state instead of its flag.
            _active = false;
            return UpdateSleepTime.Forever;
        }

        var originalTeam = Context.GameLogic.FindTeamById(_originalTeamId);
        if (originalTeam != null)
        {
            GameObject.Team = originalTeam;
        }
        // If the original team cannot be resolved (e.g. disbanded while the object was
        // defected - no GPL or object.ini guidance either way), the object simply stays on its
        // current (defected) team: a silent no-op revert, not an exception, matching this
        // module's "never throw on a missing lookup" posture.

        _active = false;
        return UpdateSleepTime.Forever;
    }

    // ---- the single walk: save/load + CRC + deep-dump + conformance ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Active", ref _active);
        xfer.XferUInt("OriginalTeamId", ref _originalTeamId, Tolerance.Exact);
        xfer.XferFrame("RevertFrame", ref _revertFrame, Tolerance.Quantum);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// A passive, universally-attached module that, once triggered externally (via
/// <see cref="TemporarilyDefectUpdate.StartTemporaryDefect"/> - currently expected to be
/// DominateEnemySpecialPower, not yet ported), switches the object to a new team for
/// <see cref="DefectDuration"/> and then automatically reverts it to its original team.
/// </summary>
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class TemporarilyDefectUpdateModuleData : UpdateModuleData
{
    internal static TemporarilyDefectUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<TemporarilyDefectUpdateModuleData> FieldParseTable = new IniParseTable<TemporarilyDefectUpdateModuleData>
    {
        { "DefectDuration", (parser, x) => x.DefectDuration = parser.ParseDurationLogicFrames() }
    };

    /// <summary>
    /// How long the defection lasts before this module reverts it (ms in INI, ceil-quantized
    /// at parse, S5). The shipped AotR data's own comment warns this must stay below the
    /// triggering special power's ReloadTime.
    /// </summary>
    public LogicFrameSpan DefectDuration { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new TemporarilyDefectUpdate(gameObject, gameEngine.SimContext, this);
    }
}
