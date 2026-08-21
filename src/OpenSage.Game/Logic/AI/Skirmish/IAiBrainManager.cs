#nullable enable

// S9-01 (R15 L3): the contract every skirmish-AI manager implements.
//
// Managers land in later packets: AiEconomyManager (S9-03), AiBaseManager (S9-06), team and
// attack coordination (S9-08+). They share one shape so the brain's tick is a plain ordered
// walk of the registration list - no manager knows about any other manager, and none of them
// knows about the game.

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// One decision-making subsystem of a <see cref="SkirmishAIBrain"/>.
/// </summary>
public interface IAiBrainManager
{
    /// <summary>
    /// Short stable tag used in trace lines and counters (e.g. "econ", "base"). Keep it stable:
    /// the match report groups evidence on it.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Runs one logic frame's worth of decisions. Contract, in force for every manager:
    /// <list type="bullet">
    ///   <item>read the world ONLY through <see cref="SkirmishAIBrain.World"/>;</item>
    ///   <item>act ONLY by submitting orders through <see cref="SkirmishAIBrain.Orders"/>;</item>
    ///   <item>never mutate a <c>GameObject</c>, a <c>Player</c> or a bank account directly;</item>
    ///   <item>be deterministic in the world snapshot plus the manager's own state (no wall
    ///   clock, no unseeded RNG, no iteration over unordered hash collections).</item>
    /// </list>
    /// </summary>
    void Update(SkirmishAIBrain brain);
}
