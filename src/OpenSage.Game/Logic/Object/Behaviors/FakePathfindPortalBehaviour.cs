// R15 L5-P9 — partial port of FakePathfindPortalBehaviour (castle/wall gates), consuming the
// L5-P2 port spec (bfme2-workbench/research/modules-r13/specs/FakePathfindPortalBehaviourModuleData.md).
//
// SCOPE — only the half of the module the spec marks GROUNDED is implemented here:
//
//   GROUNDED (spec §3 Claim 2): the unit-category filter itself. "May this unit use the gate?"
//   is answered from two predicates that already exist on main:
//     * enemy test    — GameObject.GetRelationship (GameObject.cs:1920) resolving through the
//                       querying unit's Team/Player, yielding RelationshipType.Enemies;
//     * owner-category test — Player.AIPlayer (Player.cs:88), populated by Player.FromMapData
//                       (Player.cs:852-856) as null (human) / SkirmishAIPlayer (skirmish AI) /
//                       base AIPlayer (non-skirmish, i.e. scripted/campaign or civilian-side AI).
//     Both AllowEnemies and AllowNonSkirmishAIUnits are therefore *decidable* today; that is
//     what <see cref="FakePathfindPortalBehaviour.IsUnitAllowedThrough"/> below decides.
//
//   HELD (spec §3 Claim 1, blackboard [L5-P2 #1]): the consumption side — making the gate cell
//   fractionally/conditionally passable in the deterministic pathfinder. SimPathfindGrid models
//   passability as a discrete SimPathfindCellType with a binary obstacle overlay
//   (SimPathfindGrid.cs:124-130 / :225-239) and only StampObstacle/RemoveObstacle mutators
//   (:145-174) driven by a static footprint rectangle (SimPathfindEngineHost.cs:110,152); there
//   is no per-query, per-owner or fractional passability primitive to plug this filter into, and
//   GateOpenAndCloseBehavior.PercentOpenForPathing is an animation-timeline fraction that toggles
//   physical colliders and never touches the grid. Designing that primitive is pathfinder-
//   subsystem work (spec §4) and is deliberately NOT invented here — this module therefore
//   registers no grid override and has no Update(); nothing in the sim queries it yet.
//   When the primitive lands, its query path calls IsUnitAllowedThrough and nothing else in this
//   file should need to change.
//
// Both INI fields were already parsed before this packet (spec §1, no Stage A delta); the
// British keyword spelling matches the INI token exactly (BehaviorModule.cs:118) and is not
// renamed. Corpus: 14+ AotR instances, always alongside GateOpenAndCloseBehavior + AIGateUpdate
// on wall/castle gates (e.g. data/AgeoftheRing/aotr/data/ini/object/.../helmsdeepbuildings.ini:181),
// both fields authored No.

#nullable enable

using OpenSage.Data.Ini;
using OpenSage.Logic.AI;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme2)]
public sealed class FakePathfindPortalBehaviour : BehaviorModule
{
    private readonly FakePathfindPortalBehaviourModuleData _moduleData;

    internal FakePathfindPortalBehaviour(GameObject gameObject, IGameEngine gameEngine, FakePathfindPortalBehaviourModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _moduleData = moduleData;
    }

    public bool AllowEnemies => _moduleData.AllowEnemies;

    public bool AllowNonSkirmishAIUnits => _moduleData.AllowNonSkirmishAIUnits;

    /// <summary>
    /// The GROUNDED half of the module: whether <paramref name="unit"/> belongs to a unit
    /// category this gate lets through. Pure — no sim state is mutated and the pathfind grid is
    /// not consulted (see the file header: the grid side is HELD).
    /// </summary>
    public bool IsUnitAllowedThrough(GameObject? unit)
    {
        if (unit == null)
        {
            return false;
        }

        return IsUnitAllowed(
            GameObject.GetRelationship(unit),
            unit.Owner,
            _moduleData.AllowEnemies,
            _moduleData.AllowNonSkirmishAIUnits);
    }

    /// <summary>
    /// Decision core, factored out so it can be exercised without a gate object: a unit is
    /// refused if it is an enemy of the gate and <c>AllowEnemies</c> is off, or if its owner is a
    /// non-skirmish AI player and <c>AllowNonSkirmishAIUnits</c> is off. Everything else passes.
    /// </summary>
    internal static bool IsUnitAllowed(
        RelationshipType relationshipToGate,
        Player? unitOwner,
        bool allowEnemies,
        bool allowNonSkirmishAIUnits)
    {
        if (!allowEnemies && relationshipToGate == RelationshipType.Enemies)
        {
            return false;
        }

        if (!allowNonSkirmishAIUnits && IsNonSkirmishAIOwned(unitOwner))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Spec §3 Claim 2: AIPlayer null => human, SkirmishAIPlayer => skirmish AI, plain AIPlayer
    /// => non-skirmish (scripted/campaign or civilian-side) AI.
    /// </summary>
    internal static bool IsNonSkirmishAIOwned(Player? owner) =>
        owner?.AIPlayer is not null and not SkirmishAIPlayer;
}

[AddedIn(SageGame.Bfme2)]
public sealed class FakePathfindPortalBehaviourModuleData : BehaviorModuleData
{
    internal static FakePathfindPortalBehaviourModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<FakePathfindPortalBehaviourModuleData> FieldParseTable = new IniParseTable<FakePathfindPortalBehaviourModuleData>
    {
        { "AllowEnemies", (parser, x) => x.AllowEnemies = parser.ParseBoolean() },
        { "AllowNonSkirmishAIUnits", (parser, x) => x.AllowNonSkirmishAIUnits = parser.ParseBoolean() },
    };

    public bool AllowEnemies { get; private set; }
    public bool AllowNonSkirmishAIUnits { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new FakePathfindPortalBehaviour(gameObject, gameEngine, this);
    }
}
