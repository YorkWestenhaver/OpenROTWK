// SpawnUnitBehavior - R13 port to the frozen module contract (api-freeze-v1 §6).
//
// Grounding: no GPL C++ sibling exists for BFME's SpawnUnitBehavior (Generals/ZH have no such
// class). This port is grounded entirely in EA's own design comment
// (data/AgeoftheRing/aotr/data/ini/object/goodfaction/structures/elven/elvenfortress.ini:1134-1139,
// ModuleTag_allowMeToBuildDrakeFromCitadel): "This behavior allows me to queue this unit up for
// the citadel to build. Otherwise I would need a hidden command button. The production behavior
// checks to see if there is actually a commandset button setup to build this unit. This bypasses
// that." — plus the one active data usage
// (data/AgeoftheRing/aotr/data/ini/object/goodfaction/goodfactionsubobjects.ini:7766-7785, a
// SpawnUnitBehavior sitting alongside a ProductionUpdate on the same object). See
// modules-r13/specs/SpawnUnitBehaviorModuleData.md for the full audit trail (never Ghidra/
// game.dat material - clean-room wall respected).
//
// What this port delivers: the module's own, directly-evidenced job - hand UnitName to the
// sibling ProductionUpdate as a legal production request, gated by SpawnOnce, mirroring the
// exact BuildCost-withdraw-then-QueueProduction mechanic OrderProcessor.CreateUnit already uses
// for every other unit build (src/OpenSage.Game/Logic/Orders/OrderProcessor.cs, case
// OrderType.CreateUnit - reusing a landed mechanic, not inventing a new one).
//
// F-SPAWN-1 (parsed, not modeled): UnitCommand names a retail CommandButton that this codebase
// never validates against (OrderProcessor.CreateUnit performs no CommandSet check to bypass in
// the first place, and this engine's ControlBar does not consult per-object modules when
// building its button list). Reconstructing retail's button-enumeration algorithm would require
// Ghidra/game.dat material, off-limits under the clean-room wall, and is a ControlBar-wide
// change out of this ModuleData-scoped task regardless. UnitCommand is parsed and stored for
// authoring round-trip fidelity only - mirrors the EmpUpdateModuleData.StartColor/EndColor
// "PARSED, NOT MODELED" idiom (F-EMP-5). Not a determinism hazard: nothing in the sim reads it
// back.
//
// MUTABLE SIM STATE INVENTORY: exactly one bit, _hasSpawned - whether the one-shot SpawnOnce
// slot has already been consumed. Meaningless (never read) when SpawnOnce is false. Persisted
// via Load() below.
//
// Contract-shape note: CreateModule is still on the legacy (GameObject, IGameEngine) constructor
// with a StatePersister Load and no IXfer contract surface (see GrantUpgradeCreate, the landed
// sibling this packet points at). Promoting the shared CreateModule base to the ISimContext
// contract is a batch-wide change out of scope here - same, already-filed scope boundary
// GrantUpgradeCreate.cs carries at its own header, inherited rather than re-filed.

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class SpawnUnitBehavior : CreateModule
{
    private readonly SpawnUnitBehaviorModuleData _data;

    // ---- mutable sim state: the whole inventory ----
    // Whether the one-shot spawn slot (SpawnOnce) has already been consumed. Meaningless
    // (never read) when SpawnOnce is false.
    private bool _hasSpawned;

    public SpawnUnitBehavior(GameObject gameObject, IGameEngine gameEngine, SpawnUnitBehaviorModuleData data)
        : base(gameObject, gameEngine)
    {
        _data = data;
    }

    /// <summary>
    /// True while this object still legally offers UnitName as a production entry: the
    /// referenced unit resolved, and (SpawnOnce implies) the one-shot slot hasn't fired yet.
    /// </summary>
    public bool CanSpawnUnit => _data.UnitName?.Value != null && !(_data.SpawnOnce && _hasSpawned);

    /// <summary>
    /// Queues UnitName for production on the sibling ProductionUpdate, mirroring
    /// OrderProcessor.CreateUnit's own withdraw-then-queue mechanic exactly (BuildCost off the
    /// owner's bank account, then ProductionUpdate.QueueProduction) - the same, already-landed
    /// path every other unit build goes through, reused rather than reinvented. Returns false
    /// (no-op, no partial effect) when the unit definition didn't resolve, the one-shot slot is
    /// already spent, or the object has no ProductionUpdate sibling to hand the job to.
    /// </summary>
    public bool TryQueueUnit()
    {
        if (!CanSpawnUnit)
        {
            return false;
        }

        var productionUpdate = GameObject.ProductionUpdate;
        if (productionUpdate == null)
        {
            return false;
        }

        var unitDefinition = _data.UnitName.Value;
        // playSound: false - sim lane; audio is a client concern and BankAccount's sound path
        // dereferences the (headless-absent) audio system. Same posture as the landed
        // SabotageSupplyCenterCrateCollide withdrawal.
        GameObject.Owner.BankAccount.Withdraw((uint)unitDefinition.BuildCost, playSound: false);
        productionUpdate.QueueProduction(unitDefinition);

        if (_data.SpawnOnce)
        {
            _hasSpawned = true;
        }

        return true;
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistBoolean(ref _hasSpawned);
    }
}

/// <summary>
/// Registers <see cref="UnitName"/> as a legally queueable production entry on the owning object
/// (via the sibling <see cref="ProductionUpdate"/>), without requiring an authored
/// CommandButton/CommandSet entry that references it. See F-SPAWN-1 for the scope boundary
/// around <see cref="UnitCommand"/>.
/// </summary>
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class SpawnUnitBehaviorModuleData : BehaviorModuleData
{
    internal static SpawnUnitBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<SpawnUnitBehaviorModuleData> FieldParseTable = new IniParseTable<SpawnUnitBehaviorModuleData>
        {
            { "UnitName", (parser, x) => x.UnitName = parser.ParseObjectReference() },
            { "UnitCommand", (parser, x) => x.UnitCommand = parser.ParseAssetReference() },
            { "SpawnOnce", (parser, x) => x.SpawnOnce = parser.ParseBoolean() }
        };

    /// <summary>Object definition to queue for production. Null (INI token NONE, or omitted) is a silent no-op.</summary>
    public LazyAssetReference<ObjectDefinition>? UnitName { get; private set; }

    /// <summary>
    /// Names a retail CommandButton. Parsed and stored for authoring round-trip fidelity only -
    /// not consumed by the runtime module. See F-SPAWN-1.
    /// </summary>
    public string UnitCommand { get; private set; }

    /// <summary>When set, the unit can only be queued once through this module.</summary>
    public bool SpawnOnce { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
        => new SpawnUnitBehavior(gameObject, gameEngine, this);
}
