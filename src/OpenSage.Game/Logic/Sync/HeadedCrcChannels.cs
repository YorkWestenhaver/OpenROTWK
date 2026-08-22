// R15 packet 5 (workbench research/design-sim-presentation-bridge.md §2 packet 5): the
// headed game's CRC channel set.
//
// One job, deliberately small: build the SAME three channel sources, in the SAME frozen
// CrcChannel order, that the headless map-v1 scenario builds
// (tools/OpenSage.SimCore.ScenarioDriver/MapScenario.cs, the ctor's SyncChecker construction)
// - Objects (0), LogicRandom (1), and the OracleView group riding the Taint ordinal (6). If
// the two hosts walked different channel sets, comparing their dumps would be meaningless, so
// the set lives in one place per host and both are pinned by test against the same list.
//
// ORDERING PRECONDITION, load-bearing: this must run AFTER Scene3D exists. IGame.GameEngine
// resolves through Scene3D.GameEngine, and the SimContext that owns the logic RNG the
// LogicRandom channel folds is constructed lazily off that engine. Building the channels
// before a scene exists NREs (design doc §4.2 crash #1); building them before LoadMap would
// also capture a GameLogic whose object list Scene3D construction then wipes (§4.2 crash #2).
// Build() states that precondition as an explicit throw rather than letting it surface as a
// null dereference three layers down.
//
// NOT a determinism claim. Attaching a checker to a headed game makes the headed run's state
// COMPARABLE to a headless run's; it does not make the headed run deterministic. The float
// AIUpdate/Locomotor chain every unit on a shipped AotR map moves through is unported
// (design doc §3 blocker 2), so an equality run against map-v1 is a round-3 item that needs
// the L1 map fixes and a deterministic stimulus first.

using System.Collections.Generic;
using OpenSage.Logic.Object;
using OpenSage.SimCore.Rng;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Sync;

internal static class HeadedCrcChannels
{
    /// <summary>
    /// The three sources, in frozen <see cref="CrcChannel"/> walk order. <see cref="SyncChecker"/>
    /// re-validates that ordering itself and throws if it is ever perturbed here.
    /// </summary>
    /// <param name="game">
    /// A game whose <see cref="IGame.Scene3D"/> is already loaded - see the header. Its
    /// <c>GameLogic</c> is the one the match runs on; no second host is constructed.
    /// </param>
    public static IReadOnlyList<ICrcChannelSource> Build(IGame game)
    {
        System.ArgumentNullException.ThrowIfNull(game);

        if (game.Scene3D is null)
        {
            throw new System.InvalidOperationException(
                "HeadedCrcChannels.Build requires a loaded Scene3D: IGame.GameEngine resolves " +
                "through it, and Scene3D construction resets GameLogic. Build the channels " +
                "after the map is loaded, never before.");
        }

        var gameLogic = game.GameLogic;

        // The logic RNG the checker folds is the SimContext-owned stream, reached exactly the
        // way MapScenario reaches it: the counting wrapper's wrapped generator.
        var context = (SimContext)game.GameEngine.SimContext;
        var random = ((CountingSimRandom)context.GameLogicRandom).Random;

        return new ICrcChannelSource[]
        {
            new GameObjectsChannelSource(gameLogic),
            new LogicRandomChannelSource(random),
            new OracleViewChannelSource(gameLogic),
        };
    }

    /// <summary>Convenience: <see cref="Build"/> handed straight to a checker.</summary>
    public static SyncChecker CreateChecker(IGame game) => new(Build(game));
}
