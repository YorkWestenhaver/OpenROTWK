#nullable enable

// S9-01 (R15 L3): brain construction and attachment.
//
// One place decides WHICH players get a strategic brain and WHICH managers it runs, so later
// packets add behaviour by editing the registration block below rather than by touching
// PlayerManager or Player.

using System.Collections.Generic;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// Creates <see cref="SkirmishAIBrain"/>s and attaches them to the AI players of a new match.
/// </summary>
public static class SkirmishAIBrains
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Attaches a brain to every skirmish-AI player in <paramref name="players"/> and returns
    /// how many were attached.
    /// </summary>
    /// <remarks>
    /// The predicate is <c>Player.AIPlayer is SkirmishAIPlayer</c>, which is exactly the set
    /// Player.FromMapData already computes for a skirmish match (not human, has a faction
    /// template, not FactionObserver, not FactionCivilian). Reusing it means the brain can
    /// never disagree with the engine about who is an AI - and this packet does not have to
    /// touch the .sav-pinned SkirmishAIPlayer/AIPlayer shells to find out.
    /// </remarks>
    public static int AttachTo(IGame game, IReadOnlyList<Player> players, Difficulty difficulty = Difficulty.Normal)
    {
        var attached = 0;

        // Ascending index: brains are created in the same order they will be ticked.
        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];

            if (player.AIPlayer is not SkirmishAIPlayer)
            {
                continue;
            }

            player.SkirmishAIBrain = Create(game, player, difficulty);
            attached++;
        }

        Logger.Info($"[AI] attached {attached} skirmish AI brain(s) at difficulty {difficulty}");

        return attached;
    }

    /// <summary>
    /// Builds one brain over the live game, with its managers registered in tick order.
    /// </summary>
    public static SkirmishAIBrain Create(IGame game, Player player, Difficulty difficulty)
    {
        var world = new LiveAiWorldView(game, player, difficulty);
        var orders = new LegacyOrderSink(game);
        var brain = new SkirmishAIBrain(world, orders);

        RegisterManagers(brain);

        return brain;
    }

    /// <summary>
    /// The manager tick order for a skirmish brain.
    /// </summary>
    /// <remarks>
    /// APPEND-ONLY REGION (shared with S9-03 economy, S9-06 base, S9-08/S9-09 team + attack).
    /// Registration order is tick order and tick order is part of the AI's determinism: append
    /// your manager at the end, and never reorder existing entries to fix a bug in one of them.
    /// Empty in S9-01 by design - the spine ships with no behaviour so that the wave's other
    /// packets can each land exactly one manager without conflicting here.
    /// </remarks>
    private static void RegisterManagers(SkirmishAIBrain brain)
    {
        // (S9-03) brain.RegisterManager(new AiEconomyManager());
        // (S9-06) brain.RegisterManager(new AiBaseManager());
    }
}
