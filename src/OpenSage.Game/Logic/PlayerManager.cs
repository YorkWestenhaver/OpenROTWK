using System;
using System.Collections.Generic;
using System.Linq;
using OpenSage.Content;
using OpenSage.Logic.AI.Skirmish;
using OpenSage.Utilities.Extensions;

namespace OpenSage.Logic;

public sealed class PlayerManager : IPersistableObject
{
    private readonly IGame _game;

    public IReadOnlyList<Player> Players => _players;
    private Player[] _players;

    public Player LocalPlayer { get; private set; }

    internal PlayerManager(IGame game)
    {
        _game = game;
        _players = Array.Empty<Player>();
    }

    internal void OnNewGame(Data.Map.Player[] mapPlayers, GameType gameType)
    {
        _players = CreatePlayers(mapPlayers, gameType).ToArray();

        LocalPlayer = null;

        foreach (var player in _players)
        {
            if (player.IsHuman)
            {
                LocalPlayer = player;
                break;
            }
        }

        if (LocalPlayer == null && _players.Length > 2)
        {
            // TODO: Probably not the right way to do it.
            LocalPlayer = _players[2];
        }

        // TODO: Setup player relationships.

        // S9-01: give every skirmish-AI player a strategic brain. No-op for a single-player
        // match (Player.FromMapData only creates SkirmishAIPlayer shells when the game type is
        // not SinglePlayer) and for a match with no AI slots.
        //
        // Difficulty is the default until the launcher plumbs --ai-difficulty through (L1-04);
        // pass it here when it exists rather than reading a flag from inside the AI.
        SkirmishAIBrains.AttachTo(_game, _players);
    }

    // This needs to operate on the entire player list, because players have references to each other
    // (allies and enemies).
    private IEnumerable<Player> CreatePlayers(Data.Map.Player[] mapPlayers, GameType gameType)
    {
        var players = new Dictionary<string, Player>();
        var allies = new Dictionary<string, string[]>();
        var enemies = new Dictionary<string, string[]>();

        var id = 0u;
        foreach (var mapPlayer in mapPlayers)
        {
            var player = Player.FromMapData(id++, mapPlayer, _game, gameType != GameType.SinglePlayer);
            players[player.Name] = player;
            allies[player.Name] =
                mapPlayer.Allies?.Split(' ')
                .Where(s => !string.IsNullOrEmpty(s)).ToArray() ?? []; // Neutral has a player name of "", so it's important not to add empty strings
            enemies[player.Name] =
                mapPlayer.Enemies?.Split(' ')
                .Where(s => !string.IsNullOrEmpty(s)).ToArray() ?? []; // Neutral has a player name of "", so it's important not to add empty strings
        }

        foreach (var (name, player) in players)
        {
            player.Allies = allies[name].Select(ally => players[ally]).ToSet();
            player.Enemies = enemies[name].Select(enemy => players[enemy]).ToSet();
        }

        return players.Values;
    }

    public Player GetPlayerByName(string name)
    {
        return Array.Find(_players, x => x.Name == name);
    }

    public Player GetPlayerByIndex(uint index)
    {
        return _players[(int)index];
    }

    public int GetPlayerIndex(Player player)
    {
        return Array.IndexOf(_players, player);
    }

    // TODO: Is this right?
    public Player GetCivilianPlayer() => _players[1];

    /// <summary>
    /// Returns the "neutral" player. There is always a player that is "neutral"
    /// with respect to all other players. This is so that everything can be
    /// associated with a non-null player, to simplify the universe.
    /// </summary>
    public Player NeutralPlayer => _players[0];

    internal void LogicTick()
    {
        // Two passes, both strictly ascending player index (S9-01).
        //
        // Pass 1 is the existing per-player tick. Pass 2 runs the skirmish AI brains, and it is
        // separate on purpose: a brain reads the world through IAiWorldView, so every brain in
        // a frame must see a world where all players have already ticked. Interleaving the two
        // would make player 0's AI observe a pre-tick player 3 while player 3's AI observed a
        // post-tick player 0 - a difference that survives into the orders they emit.
        //
        // Ascending index is written out as an indexed loop rather than left to array order:
        // it is the AI's turn order, so it is stated, not inherited.
        for (var i = 0; i < _players.Length; i++)
        {
            _players[i].LogicTick();
        }

        for (var i = 0; i < _players.Length; i++)
        {
            _players[i].SkirmishAIBrain?.Update();
        }
    }

    public void Persist(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.PersistArrayWithUInt32Length(
            _players,
            static (StatePersister persister, ref Player item) =>
            {
                persister.PersistObjectValue(item);
            });
    }
}
