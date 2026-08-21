// FloatUpdate - R12 port. Every frame the retail module (when Enabled) samples the water
// table under the object's X/Y and snaps Z to it (GPL FloatUpdate::update, TheTerrainLogic->
// isUnderwater), then unconditionally applies a small sinusoidal yaw/pitch bob to the
// drawable's instance matrix keyed off the global frame counter - both branches run
// regardless of m_enabled; only the Z-snap is gated.
//
// Both halves of that behavior are out of reach of the current sim seam:
//   - the water table is not a sim seam yet (ISimContext.Terrain / ITerrainLogic has no
//     water-height query; SimLocomotor's SEA_LEVEL z-behavior carries the identical note),
//     and there is no module-facing way to write an existing object's Z position at all -
//     position is still float substrate crossed only at spawn, behind SimContext
//     (IGameLogic.CreateObjectAt), never by a [SimState] module (D-7);
//   - the instance-matrix bob is rendering: ISimContext documents rendering as
//     "deliberately absent, forever" (S8), the same reason LargeGroupAudioUpdate's audio
//     mix has no sim-side home.
// So this is a permanently-parked module in the LargeGroupAudioUpdate shape: it exists so
// authored objects carry a live module instead of a [ParseOnly] hole, with an empty state
// inventory (Enabled is read at construction and then never inspected, matching the
// original's own dead branches under the current seam - there is nothing sim-visible for
// it to gate).
//
// TODO-spec (unverified, both behaviors): re-derive when SimCore grows a water-table query
// and a module-facing Z-position write (Z-snap), and when/if a client-side bob host exists
// to consume frame-keyed rotation requests (the bob). Until then this module has no
// observable per-frame effect.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class FloatUpdate : UpdateModule
{
    public FloatUpdate(GameObject gameObject, ISimContext context, FloatUpdateModuleData data)
        : base(gameObject, context)
    {
        // Both the Z-snap and the bob are unreachable under the current sim seam (see the
        // file header) - Enabled has nothing sim-visible left to gate: nothing to schedule.
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    public override UpdateSleepTime Update() => UpdateSleepTime.Forever;

    // ---- the single walk: no mutable sim state (see the file header). ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }
}

/// <summary>
/// Forces object to remain relative to sea level and allows for use of SEA_LEVEL locomotion
/// rules. Setting <see cref="Enabled"/> to <code>true</code> means "float on water and stay
/// relative to water level" while setting <see cref="Enabled"/> to <code>false</code> means
/// "float on water and bob about".
/// </summary>
[SimDataAudited]
public sealed class FloatUpdateModuleData : UpdateModuleData
{
    internal static FloatUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<FloatUpdateModuleData> FieldParseTable = new IniParseTable<FloatUpdateModuleData>
    {
        { "Enabled", (parser, x) => x.Enabled = parser.ParseBoolean() }
    };

    public bool Enabled { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new FloatUpdate(gameObject, gameEngine.SimContext, this);
    }
}
