#nullable enable

// S9-01 (R15 L3): the skirmish AI brain - one per AI player, ticked once per logic frame.
//
// Rulings this implements:
//   S9-R15-A  the strategic brain runs on the LIVE legacy runtime (not SimCore), so it is
//             ticked from the player tick and writes through the legacy order pipe.
//   S9-R15-B  it is native C# tuned by SkirmishAIData, not a script interpreter. Part I of
//             design-aiupdate.md (per-object AIUpdate rework) is explicitly deferred - nothing
//             here reaches into an object's AIUpdate.
//
// The brain itself holds no game knowledge at all. It owns three seams (world view, order
// sink, trace) and a registration list of managers, and its whole job is to walk that list in
// a fixed order every frame. All actual AI behaviour arrives as managers in later packets.
//
// NOT this class's job (deliberately, so later packets do not fight over it): choosing what to
// build (S9-06), what to spend (S9-03), emitting selection/command pairs (S9-04), rate
// limiting orders (S9-04), or persisting anything. The brain is NOT saved to .sav - the
// savegame shells SkirmishAIPlayer/AIPlayer stay exactly as they are, untouched by this
// packet, because their layout is pinned by the retail .sav format.

using System;
using System.Collections.Generic;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// Per-player strategic AI. Created for AI-controlled players at match start, ticked once per
/// logic frame in ascending player index, and discarded with the match.
/// </summary>
public sealed class SkirmishAIBrain
{
    /// <summary>
    /// Default heartbeat cadence in logic frames. 30 frames = ~1 second at the SAGE logic rate,
    /// which keeps a 1800-frame R1 gate run at ~60 heartbeat lines per player: enough to prove
    /// liveness and to see money move, few enough not to drown the match log.
    /// </summary>
    public const uint DefaultHeartbeatInterval = 30;

    private readonly List<IAiBrainManager> _managers = new();

    /// <summary>The only permitted read of the world.</summary>
    public IAiWorldView World { get; }

    /// <summary>The only permitted write to the world.</summary>
    public IAiOrderSink Orders { get; }

    /// <summary>Evidence channel: heartbeats, manager lines, counters.</summary>
    public AiTrace Trace { get; }

    /// <summary>Player index this brain plays for.</summary>
    public int PlayerIndex { get; }

    /// <summary>Heartbeat cadence in logic frames. A heartbeat is emitted when frame % interval == 0.</summary>
    public uint HeartbeatInterval { get; }

    /// <summary>Number of times <see cref="Update"/> has run.</summary>
    public uint TicksRun { get; private set; }

    /// <summary>Registered managers, in the order they will be updated.</summary>
    public IReadOnlyList<IAiBrainManager> Managers => _managers;

    public SkirmishAIBrain(
        IAiWorldView world,
        IAiOrderSink orders,
        AiTrace? trace = null,
        uint heartbeatInterval = DefaultHeartbeatInterval)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(orders);

        if (heartbeatInterval == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heartbeatInterval),
                "Heartbeat interval must be at least 1 frame; 0 would divide by zero every tick.");
        }

        World = world;
        Orders = orders;
        PlayerIndex = world.PlayerIndex;
        Trace = trace ?? new AiTrace(world.PlayerIndex);
        HeartbeatInterval = heartbeatInterval;
    }

    // ---- manager registration (APPEND-ONLY region; shared with S9-03/S9-06/S9-08/S9-09) ----
    //
    // Later packets add their manager by calling RegisterManager from the brain factory. Update
    // order IS registration order and it is part of the AI's determinism: two brains that
    // registered the same managers in the same order make the same decisions from the same
    // snapshot. Never reorder an existing registration to fix a bug - fix the manager.

    /// <summary>
    /// Appends a manager to the tick list. Duplicate instances are rejected: registering the
    /// same manager twice would double every order it emits.
    /// </summary>
    public void RegisterManager(IAiBrainManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        if (_managers.Contains(manager))
        {
            throw new InvalidOperationException(
                $"Manager '{manager.Name}' is already registered on the brain for player {PlayerIndex}.");
        }

        _managers.Add(manager);
    }

    /// <summary>Finds a registered manager of the given type, or null. Convenience for tests and reports.</summary>
    public T? GetManager<T>() where T : class, IAiBrainManager
    {
        for (var i = 0; i < _managers.Count; i++)
        {
            if (_managers[i] is T match)
            {
                return match;
            }
        }

        return null;
    }

    // ---- tick ----

    /// <summary>
    /// Runs one logic frame: heartbeat first (so a manager that throws still leaves evidence of
    /// where the match got to), then every manager in registration order.
    /// </summary>
    /// <remarks>
    /// Manager exceptions are deliberately NOT swallowed. A caught-and-logged exception would
    /// turn a broken manager into an AI that quietly does nothing, which is exactly the failure
    /// the dr-0039 guard exists to catch; the R1 gate wants it loud.
    /// </remarks>
    public void Update()
    {
        var frame = World.CurrentFrame;

        if (frame % HeartbeatInterval == 0)
        {
            Trace.Heartbeat(
                frame,
                World.Money,
                World.OwnObjects.Count,
                World.EnemyObjects.Count,
                _managers.Count);
        }

        for (var i = 0; i < _managers.Count; i++)
        {
            _managers[i].Update(this);
        }

        TicksRun++;
    }
}
