// L4 victory/defeat lane (VD-3) — the engine side of the victory seam.
//
// Behavioral reference (clean-room, semantics only — no code transcribed):
// generals-gpl GeneralsMD VictoryConditions.cpp (cachePlayerPtrs, areAllies),
// Team.cpp's hasAnyBuildings / hasAnyUnits / hasAnyObjects liveness sweeps, and
// Player.cpp killPlayer. Design: workbench research/design-victory-defeat.md
// §1.4 (the predicates), §1.6 (cachePlayerPtrs), §2 (the BFME2 ObjectFilter
// substitution), §2.3 (three landed ObjectFilter defects and what this adapter does
// about each), §4 (this split), §6.1 (the null-filter fallback), §6.3 (alliances).
//
// This type is deliberately NOT [SimState]: it is the one place engine types are
// allowed to touch the victory lane. VictoryConditionsCore (the [SimState] core) sees
// nothing but ints, LogicFrame and bools, through IVictoryWorld.

using System;
using System.Collections.Generic;
using OpenSage.Logic.Object;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Victory;

/// <summary>
/// The world the victory core reads, as seen by this engine: the object sweep runs over
/// <c>GameLogic.Objects</c>, the class test is BFME2's two <c>GameData</c> ObjectFilters, and
/// the slot -&gt; <see cref="Player"/> mapping is the cached pool built by
/// <see cref="CachePlayers"/> at match start (design-victory-defeat.md §1.6, §4).
/// </summary>
/// <remarks>
/// Determinism: every read is a pure function of sim state. The sweep walks
/// <c>GameLogic.Objects</c>, which is index-ordered by <c>ObjectId</c> by construction
/// (<c>GameLogic._objects</c> is a list indexed by <c>ObjectId.Index</c> with the holes
/// skipped), so no sort is needed and the iteration order is the same on every peer. The one
/// mutation this adapter performs — <see cref="OnPlayerEliminated"/> —
/// runs through <see cref="Player.KillPlayer"/>, which destroys in that same ascending order.
/// </remarks>
public sealed class VictoryWorldAdapter : IVictoryWorld
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly IVictoryWorldSource _source;

    /// <summary>The cached victory pool: slot index -&gt; player. Fixed at match start; CRC contract.</summary>
    private readonly List<Player> _pool = new();

    private int _localSlot = -1;
    private bool _cached;

    /// <summary>Production constructor: reads the live game.</summary>
    public VictoryWorldAdapter(IGame game)
        : this(new GameVictoryWorldSource(game ?? throw new ArgumentNullException(nameof(game))))
    {
    }

    /// <summary>
    /// Test constructor. The seam exists so the adapter's own semantics — pool filtering, the
    /// filter sweep, the alliance tiers — are provable without standing up a full game; the
    /// production path always goes through <see cref="GameVictoryWorldSource"/>.
    /// </summary>
    internal VictoryWorldAdapter(IVictoryWorldSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    // ---- IVictoryWorld ----

    public LogicFrame CurrentFrame => _source.CurrentFrame;

    public int PlayerCount => _pool.Count;

    /// <summary>
    /// The local player's index in the cached pool, or <c>-1</c> when no local player was cached
    /// (observer, or a host with no seated local player). Feed this straight to
    /// <c>VictoryConditionsCore.Reset</c> — its <c>-1</c> branch is GPL's cachePlayerPtrs tail.
    /// </summary>
    public int LocalSlot => _localSlot;

    /// <summary>The player occupying a pool slot, or <c>null</c> for an out-of-pool index.</summary>
    public Player PlayerAt(int slot) => InPool(slot) ? _pool[slot] : null;

    /// <summary>The slot a player occupies, or <c>-1</c> if that player was excluded from the pool.</summary>
    public int SlotOf(Player player) => player == null ? -1 : _pool.IndexOf(player);

    public bool HasAnyVictoryStructures(int slot) => HasAny(slot, VictoryObjectClass.Structures);

    public bool HasAnyVictoryUnits(int slot) => HasAny(slot, VictoryObjectClass.Units);

    public bool HasAnyVictoryObjects(int slot) => HasAny(slot, VictoryObjectClass.Any);

    /// <summary>
    /// GPL <c>areAllies</c> (VictoryConditions.cpp:68-76), §6.3: <b>mutual</b> only, and only
    /// between two distinct slots. Two tiers, in priority order — map-authored
    /// <see cref="Player.Allies"/> membership first (this is what is populated today, by
    /// <c>PlayerManager.CreatePlayers</c>), then the player-to-player relationship override
    /// table. The one-sided case is deliberately NOT an alliance; that is GPL's rule.
    /// </summary>
    public bool AreAllies(int slotA, int slotB)
    {
        if (slotA == slotB)
        {
            return false;
        }

        var a = PlayerAt(slotA);
        var b = PlayerAt(slotB);
        if (a == null || b == null)
        {
            return false;
        }

        // Tier 1: mutual map-authored alliance.
        if (a.Allies != null && b.Allies != null && a.Allies.Contains(b) && b.Allies.Contains(a))
        {
            return true;
        }

        // Tier 2: the mutual relationship override table. TODO: nothing populates it yet —
        // PlayerManager.OnNewGame still ends with "TODO: Setup player relationships"
        // (PlayerManager.cs), so this tier is dormant and every non-tier-1 pair reads Neutral.
        // When lobby/script alliance wiring lands it becomes live with no change here.
        return a.GetRelationship(b.DefaultTeam) == RelationshipType.Allies
            && b.GetRelationship(a.DefaultTeam) == RelationshipType.Allies;
    }

    /// <summary>
    /// GPL's <c>killPlayer()</c> hook (§1.5). The core fires this exactly once per slot, at the
    /// latch; the destruction order and the "mark dead before destroying" ordering both live in
    /// <see cref="Player.KillPlayer"/>.
    /// </summary>
    public void OnPlayerEliminated(int slot)
    {
        var player = PlayerAt(slot);
        if (player == null)
        {
            return;
        }

        Logger.Info($"Victory: player slot {slot} ('{player.Name}') eliminated at frame {CurrentFrame.Value}.");
        player.KillPlayer();
    }

    // ---- cachePlayerPtrs (GPL :308-340, design §1.6) ----

    /// <summary>
    /// Builds the victory pool and resolves <see cref="LocalSlot"/>. GPL's four exclusions are
    /// applied in GPL's order: the neutral player, a player with no template at all, the
    /// civilian player, and any observer. Call once at match start, before
    /// <c>VictoryConditionsCore.Reset(adapter.LocalSlot)</c>.
    /// </summary>
    /// <remarks>
    /// The civilian test uses <c>PlayerManager.GetCivilianPlayer()</c> rather than re-resolving
    /// a "FactionCivilian" template by name, because AotR's civilian template is not named
    /// that (§1.6).
    /// </remarks>
    public void CachePlayers()
    {
        _pool.Clear();
        _localSlot = -1;

        var neutral = _source.NeutralPlayer;
        var civilian = _source.CivilianPlayer;
        var local = _source.LocalPlayer;

        foreach (var player in _source.AllPlayers)
        {
            if (player == null
                || ReferenceEquals(player, neutral)
                || player.Template == null
                || ReferenceEquals(player, civilian)
                || player.IsPlayerObserver)
            {
                continue;
            }

            if (ReferenceEquals(player, local))
            {
                _localSlot = _pool.Count;
            }

            _pool.Add(player);
        }

        _cached = true;
        LogFilterState();
    }

    private bool InPool(int slot) => slot >= 0 && slot < _pool.Count;

    // ---- the sweep (§2, §2.3, §6.1) ----

    private enum VictoryObjectClass
    {
        Structures,
        Units,

        /// <summary>The both-flags branch: the union of the two filters (§6.1).</summary>
        Any,
    }

    private bool HasAny(int slot, VictoryObjectClass objectClass)
    {
        var owner = PlayerAt(slot);
        if (owner == null)
        {
            return false;
        }

        var structures = _source.StructureFilter;
        var units = _source.UnitFilter;

        // §6.1 fallback. When the filter this branch needs is missing — the state on disk today,
        // because a bare '-' token in AotR's gamedata.ini aborts the whole GameData block (§2.3a)
        // — GPL's own filter-free hasAnyObjects() stands in: any live object counts, minus
        // PROJECTILE / INERT / MINE. Falling back rather than answering "no objects" is what
        // keeps a partially-loaded install from instantly declaring everyone eliminated.
        var useFallback = objectClass switch
        {
            VictoryObjectClass.Structures => structures == null,
            VictoryObjectClass.Units => units == null,
            _ => structures == null || units == null,
        };

        foreach (var gameObject in _source.Objects)
        {
            if (gameObject == null || !ReferenceEquals(gameObject.Owner, owner) || !IsLiveForVictory(gameObject))
            {
                continue;
            }

            if (useFallback)
            {
                if (IsFallbackVictoryObject(gameObject))
                {
                    return true;
                }

                continue;
            }

            var matched = objectClass switch
            {
                VictoryObjectClass.Structures => MatchesVictoryFilter(structures, gameObject),
                VictoryObjectClass.Units => MatchesVictoryFilter(units, gameObject),
                _ => MatchesVictoryFilter(structures, gameObject) || MatchesVictoryFilter(units, gameObject),
            };

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The liveness gate, and the single choke point for it. This is <b>exactly</b>
    /// <c>SimScriptHostAdapter</c>'s predicate (<c>!IsEffectivelyDead &amp;&amp; !IsDestroyed</c>),
    /// which is itself GPL's universal gate from Team.cpp — the three hasAnyX variants differ
    /// only in which KindOfs they exclude on top of it, never in the gate. AotR's own data says
    /// the same thing in a comment directly above the two filters: dead and destroyed are always
    /// ignored (§2.1).
    /// </summary>
    /// <remarks>
    /// VD-7 widens <b>this method and only this method</b> to treat a revive-pending object as
    /// live (§6.2). The script host's predicates stay GPL-exact on purpose — that divergence is
    /// deliberate and is proved on one shared fixture there.
    /// </remarks>
    private static bool IsLiveForVictory(GameObject gameObject) =>
        !gameObject.IsEffectivelyDead && !gameObject.IsDestroyed;

    /// <summary>
    /// GPL <c>Team::hasAnyObjects</c>'s KindOf skips, used only on the §6.1 fallback path.
    /// </summary>
    private static bool IsFallbackVictoryObject(GameObject gameObject)
    {
        var kindOf = gameObject.Definition?.KindOf;
        if (kindOf == null)
        {
            return false;
        }

        return !kindOf.Get(ObjectKinds.Projectile)
            && !kindOf.Get(ObjectKinds.Inert)
            && !kindOf.Get(ObjectKinds.Mine);
    }

    /// <summary>
    /// The BFME2 class test: <c>ObjectFilter.Matches</c>, plus the two things it does not do that
    /// this lane needs (§2.3). Both additions are local to the victory sweep on purpose —
    /// <c>ObjectFilter.Matches</c> has many other callers and changing it is a behaviour change
    /// outside this lane's mandate.
    /// <list type="bullet">
    /// <item><b>ANY (§2.3b).</b> <c>Matches</c> honours <c>ALL</c> but not <c>ANY</c>, so
    /// <c>VictoryConditionUnitObjectFilter = ANY -DOZER …</c> — which sets no Include bits —
    /// would match nothing and units would never count.</item>
    /// <item><b>ExcludeThings (§2.3c).</b> Non-KindOf tokens (Inn, Outpost, SignalFire,
    /// CaptureFlag, MordorWorker …) are parsed into <c>ExcludeThings</c> and never read by
    /// <c>Matches</c>, so inns and outposts would count as victory structures. Compared here by
    /// object-definition name, case-insensitively. <c>IncludeThings</c> stays unhandled —
    /// neither victory filter uses it.</item>
    /// </list>
    /// </summary>
    private static bool MatchesVictoryFilter(ObjectFilter filter, GameObject gameObject)
    {
        if (filter == null)
        {
            return false;
        }

        var kindOf = gameObject.Definition?.KindOf;
        if (kindOf == null)
        {
            return false;
        }

        // Checked before the ANY short-circuit below: an exclusion always wins, exactly as it
        // does inside Matches.
        if (filter.Exclude.Intersects(kindOf))
        {
            return false;
        }

        if (IsExcludedByThingName(filter, gameObject))
        {
            return false;
        }

        return filter.Matches(gameObject) || filter.Rules.Get(ObjectFilterRule.Any);
    }

    private static bool IsExcludedByThingName(ObjectFilter filter, GameObject gameObject)
    {
        var excludeThings = filter.ExcludeThings;
        if (excludeThings == null || excludeThings.Count == 0)
        {
            return false;
        }

        var name = gameObject.Definition?.Name;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        for (var i = 0; i < excludeThings.Count; i++)
        {
            if (string.Equals(excludeThings[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ---- match-start logging (§6.1) ----

    /// <summary>
    /// One line per filter at match start, never per frame: the parsed rule / include / exclude
    /// / exclude-thing counts, so an A1-G3 partial load shows up in the log instead of silently
    /// degrading the match. A null filter additionally emits exactly one WARN naming the
    /// fallback that will be used in its place.
    /// </summary>
    private void LogFilterState()
    {
        LogOneFilter("VictoryConditionStructureObjectFilter", _source.StructureFilter);
        LogOneFilter("VictoryConditionUnitObjectFilter", _source.UnitFilter);
    }

    private static void LogOneFilter(string key, ObjectFilter filter)
    {
        if (filter == null)
        {
            Logger.Warn(
                $"Victory: GameData.{key} is null - falling back to GPL's filter-free " +
                "any-live-object sweep (minus PROJECTILE/INERT/MINE). Elimination will be more " +
                "permissive than retail. This is the expected symptom of a partially-loaded " +
                "gamedata.ini.");
            return;
        }

        Logger.Info(
            $"Victory: GameData.{key} parsed - {filter.Rules.NumBitsSet} rule(s), " +
            $"{filter.Include.NumBitsSet} include KindOf(s), {filter.Exclude.NumBitsSet} exclude KindOf(s), " +
            $"{filter.IncludeThings?.Count ?? 0} include thing(s), {filter.ExcludeThings?.Count ?? 0} exclude thing(s).");
    }

    /// <summary>Diagnostic: has <see cref="CachePlayers"/> run yet?</summary>
    public bool IsCached => _cached;
}

/// <summary>
/// The engine reads <see cref="VictoryWorldAdapter"/> makes, behind one seam so the adapter's own
/// semantics are testable without a full game. Production is
/// <see cref="GameVictoryWorldSource"/>; nothing else implements this outside tests.
/// </summary>
internal interface IVictoryWorldSource
{
    LogicFrame CurrentFrame { get; }

    IReadOnlyList<Player> AllPlayers { get; }

    Player NeutralPlayer { get; }

    Player CivilianPlayer { get; }

    /// <summary>The seated local player, or <c>null</c> (observer / headless host).</summary>
    Player LocalPlayer { get; }

    /// <summary>The live object list, ascending <c>ObjectId</c>.</summary>
    IEnumerable<GameObject> Objects { get; }

    /// <summary><c>GameData.VictoryConditionStructureObjectFilter</c>; <c>null</c> when unparsed.</summary>
    ObjectFilter StructureFilter { get; }

    /// <summary><c>GameData.VictoryConditionUnitObjectFilter</c>; <c>null</c> when unparsed.</summary>
    ObjectFilter UnitFilter { get; }
}

/// <summary>The production source: the live <see cref="IGame"/>.</summary>
internal sealed class GameVictoryWorldSource : IVictoryWorldSource
{
    private readonly IGame _game;

    internal GameVictoryWorldSource(IGame game)
    {
        _game = game;
    }

    public LogicFrame CurrentFrame => _game.GameLogic.CurrentFrame;

    public IReadOnlyList<Player> AllPlayers => _game.PlayerManager.Players;

    public Player NeutralPlayer => _game.PlayerManager.Players.Count > 0 ? _game.PlayerManager.NeutralPlayer : null;

    public Player CivilianPlayer => _game.PlayerManager.Players.Count > 1 ? _game.PlayerManager.GetCivilianPlayer() : null;

    public Player LocalPlayer => _game.PlayerManager.LocalPlayer;

    public IEnumerable<GameObject> Objects => _game.GameLogic.Objects;

    public ObjectFilter StructureFilter =>
        _game.AssetStore?.GameData?.Current?.VictoryConditionStructureObjectFilter;

    public ObjectFilter UnitFilter =>
        _game.AssetStore?.GameData?.Current?.VictoryConditionUnitObjectFilter;
}
