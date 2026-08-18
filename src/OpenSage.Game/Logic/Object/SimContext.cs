// The one ISimContext implementation: an adapter over the (partially migrated) engine.
//
// This file is a float-boundary file, NOT [SimState]: it is where the Fix64 world of ported
// modules meets the float substrate that has not migrated yet (quadtree, Body). Every such
// crossing is localized here and disappears subsystem-by-subsystem per api-freeze-v1 F11.
//
// RNG (seam S3): the context owns THE logic stream a ported module can see - a
// LogicRandom born at the single blessed site (LogicRandom.CreateForSimContext), wrapped in
// the draw-counting CountingSimRandom. It is seeded from the match seed. NOTE (migration):
// the unmigrated float sim still draws from GameLogic.Random (the Mathematics SageRandom
// port of the same generator); the two streams collapse into one when GameLogic itself
// migrates onto the context. Recorded as a pilot finding.

using System.Collections.Generic;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Rng;

namespace OpenSage.Logic.Object;

internal sealed class SimContext : ISimContext
{
    private readonly IGameEngine _engine;
    private readonly CountingSimRandom _gameLogicRandom;

    internal SimContext(IGameEngine engine)
    {
        _engine = engine;
        _gameLogicRandom = new CountingSimRandom(
            LogicRandom.CreateForSimContext(engine.GameLogic.Random.Seed));
        GameLogic = new GameLogicAdapter(engine, CompletedSpecialPowers);
        Partition = new PartitionAdapter(engine);
        Terrain = new TerrainAdapter();
        Players = new PlayerListAdapter(engine);
        Assets = new AssetStoreAdapter();
        Events = new SimEventsAdapter();
    }

    /// <summary>The engine bridge for the BehaviorModule migration ctor. Engine-only.</summary>
    internal IGameEngine Engine => _engine;

    public LogicFrame CurrentFrame => _engine.GameLogic.CurrentFrame;

    public ISimRandom GameLogicRandom => _gameLogicRandom;

    public IGameLogic GameLogic { get; }
    public IPartitionQuery Partition { get; }
    public ITerrainLogic Terrain { get; }
    public IPlayerList Players { get; }
    public IAssetStore Assets { get; }
    public ISimEvents Events { get; }

    /// <summary>
    /// The completed-special-power log (GPL <c>ScriptEngine::m_finishedSpecialPowers</c>).
    /// Lives here only until the script engine ports - see SPCD-1 in
    /// research/die/SpecialPowerCompletionDie.md.
    /// </summary>
    internal CompletedSpecialPowerLog CompletedSpecialPowers { get; } = new();

    private sealed class GameLogicAdapter : IGameLogic
    {
        private readonly IGameEngine _engine;
        private readonly CompletedSpecialPowerLog _completedSpecialPowers;

        public GameLogicAdapter(IGameEngine engine, CompletedSpecialPowerLog completedSpecialPowers)
        {
            _engine = engine;
            _completedSpecialPowers = completedSpecialPowers;
        }

        public GameObject GetObjectById(ObjectId id) => _engine.GameLogic.GetObjectById(id);

        // GameLogic's backing list is indexed by ObjectId, so its iteration is already
        // ascending ObjectId; nulls (destroyed slots) are filtered by the property.
        public IEnumerable<GameObject> ObjectsAscendingId => _engine.GameLogic.Objects;

        public void NotifyOfCompletedSpecialPower(int playerIndex, string specialPowerName, ObjectId sourceObjectId)
            => _completedSpecialPowers.Add(playerIndex, specialPowerName, sourceObjectId);
    }

    private sealed class PartitionAdapter : IPartitionQuery
    {
        private readonly IGameEngine _engine;

        public PartitionAdapter(IGameEngine engine) => _engine = engine;

        public IEnumerable<GameObject> QueryObjectsInRadius(GameObject center, Fix64 radius)
        {
            // Float boundary: the quadtree is unmigrated substrate. The float radius is
            // derived from the quantized Fix64 exactly once, here.
            var results = new List<GameObject>(
                _engine.Quadtree.FindNearby(center, center.Transform, radius.ToFloatForDisplay()));

            // The determinism contract: ascending ObjectId, never spatial-bucket order.
            results.Sort(static (a, b) => a.Id.Index.CompareTo(b.Id.Index));
            return results;
        }
    }

    private sealed class TerrainAdapter : ITerrainLogic
    {
    }

    private sealed class PlayerListAdapter : IPlayerList
    {
        private readonly IGameEngine _engine;

        public PlayerListAdapter(IGameEngine engine) => _engine = engine;

        public int GetPlayerIndex(OpenSage.Logic.Player player)
            => _engine.Game.PlayerManager.GetPlayerIndex(player);
    }

    private sealed class AssetStoreAdapter : IAssetStore
    {
    }

    private sealed class SimEventsAdapter : ISimEvents
    {
        public void FireFXAtObject(string fxListName, ObjectId objectId)
        {
            // Client-side FX dispatch is not wired yet; events are outputs with no
            // determinism obligation (S8), so a no-op is contract-legal.
        }
    }
}

/// <summary>One completed-special-power record (GPL <c>AsciiStringObjectIDPair</c>).</summary>
internal readonly record struct CompletedSpecialPower(int PlayerIndex, string Name, ObjectId SourceObjectId);

/// <summary>
/// The per-match completed-special-power log: GPL's <c>ScriptEngine::m_finishedSpecialPowers</c>
/// (one list per player) flattened into a single append-ordered list, which is equivalent for
/// the two operations the engine performs on it - append, and the scan
/// <c>isSpecialPowerComplete</c> does - and keeps the "append order is sim order" property that
/// makes the log deterministic.
/// <para>
/// NOT sim state under the freeze yet: it is not walked by any Xfer and not persisted, because
/// its real owner (the script engine) has not been ported. Filed as SPCD-1.
/// </para>
/// </summary>
internal sealed class CompletedSpecialPowerLog
{
    private readonly List<CompletedSpecialPower> _entries = new();

    public IReadOnlyList<CompletedSpecialPower> Entries => _entries;

    public void Add(int playerIndex, string specialPowerName, ObjectId sourceObjectId)
        => _entries.Add(new CompletedSpecialPower(playerIndex, specialPowerName, sourceObjectId));

    /// <summary>
    /// GPL <c>ScriptEngine::isSpecialPowerComplete</c>: first matching entry in append order,
    /// where an invalid <paramref name="sourceObjectId"/> matches any source. Removing on match
    /// is the script condition's "consume" mode.
    /// </summary>
    public bool IsComplete(int playerIndex, string specialPowerName, ObjectId sourceObjectId, bool removeFromList)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.PlayerIndex != playerIndex || entry.Name != specialPowerName)
            {
                continue;
            }

            if (sourceObjectId.IsValid && entry.SourceObjectId != sourceObjectId)
            {
                continue;
            }

            if (removeFromList)
            {
                _entries.RemoveAt(i);
            }
            return true;
        }

        return false;
    }
}
