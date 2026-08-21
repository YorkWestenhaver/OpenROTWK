// CivilianSpawnCollide - generic filtered-delete-on-collide module (R13 port).
//
// GPL reference: none. The audit's repo-wide grep for CivilianSpawnCollide/SpawnCollide over
// generals-gpl + generals-community returns empty - this is a BFME-only module with no
// Generals/ZH ancestor to translate from. Behavior below is data-derivation from the module's
// single field (DeleteObjectFilter) against the already-landed, frozen ICollideModule contract
// (CollideModule.cs:5-42), following the already-landed sibling Collide modules
// (SquishCollide/UnitCrateCollide) for engine idiom - see
// bfme2-workbench/research/modules-r13/specs/CivilianSpawnCollideModuleData.md §0/§1 for the
// full rationale.
//
// FINDINGS (behavior-fact gaps / seam constraints, filed not invented):
//   F-CSC-1 The module's name suggests an authoring context (likely paired at the data level
//     with a spawner module that creates transient civilian-crowd objects this collide module
//     then cleans up on contact), but nothing in the single field or the OnCollide contract
//     encodes "civilian" as a behavior - that context lives entirely in which ObjectFilter
//     content the INI author supplies (e.g. +CIVILIAN), not in code this port writes. This
//     class implements exactly the generic filtered-delete-on-collide behavior, no
//     civilian-specific branch.
//   F-CSC-2 (required non-additive fix) The stub previously extended BehaviorModuleData despite
//     living in Collide/ and being census-tagged Behavior. Every actual sibling in Collide/
//     extends CollideModuleData (CollideModule.cs:34-37: ModuleKinds => ModuleKinds.Collide),
//     which every landed sibling in this directory uses; the spec additionally claimed this was
//     required for GameObject's OnCollide dispatch loop to find the module by ModuleKinds tag,
//     but reading GameObject.OnCollide (GameObject.cs:1069-1077) shows dispatch there actually
//     iterates every _behaviorModules entry and type-checks `is ICollideModule`, independent of
//     ModuleKinds - so the spec's claimed dispatch-breakage mechanism does not match the current
//     code (noted per instructions: trust the code, note the drift). The base-class fix is still
//     made (matches every sibling's shape, and ModuleKinds does gate template-inheritance
//     dedup/override in ObjectDefinition.AddModuleData, ObjectDefinition.cs:1364-1379), it's
//     just not the reason OnCollide dispatch would have been "invisible" as originally claimed.
//
// Test-idiom note: HeadlessSimGame.Step() (Logic/Sim/HeadlessSimGame.cs:156-160) runs
// GameLogic.Update() + DeleteDestroyed() only - it does not call PartitionCellManager.Update()
// (that call lives in the real Game.cs:871 loop, never wired into the headless harness). So
// contract tests here call GameObject.OnCollide(other) directly, exactly the idiom every landed
// sibling Collide contract-test file already uses (UnitCrateCollideContractTests.cs:136,
// SabotageSupplyCenterCrateCollideContractTests.cs:150-158's explicit note on this), rather than
// driving collision detection through Step() as an earlier draft of the spec's test plan
// described.

using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

public sealed class CivilianSpawnCollide : CollideModule
{
    private readonly CivilianSpawnCollideModuleData _data;

    public CivilianSpawnCollide(GameObject gameObject, ISimContext context, CivilianSpawnCollideModuleData data)
        : base(gameObject, context)
    {
        _data = data;
    }

    public override void OnCollide(GameObject other, in System.Numerics.Vector3 location, in System.Numerics.Vector3 normal)
    {
        // location/normal unused - DeleteObjectFilter is a pure identity test on `other`, no
        // geometry involved (this module does zero Fix64/placement work, see F-CSC-2's SimState
        // note in the spec).
        if (other is null || other.IsDestroyed)
        {
            // A collision pair can fire OnCollide on both participants in the same
            // PartitionCellManager.Update() pass (PartitionCellManager.cs:126-127), so `other`
            // may already be mid-destruction from the reciprocal call by the time this side
            // runs.
            return;
        }

        if (_data.DeleteObjectFilter != null && _data.DeleteObjectFilter.Matches(other))
        {
            Context.GameLogic.DestroyObject(other);
        }
    }

    // No own mutable state: the module holds only a readonly reference to its ModuleData
    // (nothing changes across ticks, no counter, no gate to persist) - the version-1-and-
    // base-only Load shape below matches every no-own-field sibling's shape exactly
    // (SquishCollide.cs:12-19, UnitCrateCollide.cs:219-224).
    //
    // The module is nevertheless a ported module, so it carries the contract Xfer walk
    // (BehaviorModule.cs:70-78: the base throws ModuleNotPortedException) with a bare
    // version stamp and HasSimXfer => true - the same zero-mutable-state shape the landed
    // sibling UpgradeSoundSelectorClientBehavior uses. No [SimState] marker here: per
    // F-UCC-1 in UnitCrateCollide.cs, Collide/ still carries the float Vector3 OnCollide
    // signature and is out of SimState scope until that seam migrates to Fix64.
    internal override bool HasSimXfer => true;

    public override void Xfer(SimCore.Sync.IXfer xfer)
    {
        xfer.XferVersion(1);
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

[AddedIn(SageGame.Bfme)]
public sealed class CivilianSpawnCollideModuleData : CollideModuleData
{
    internal static CivilianSpawnCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<CivilianSpawnCollideModuleData> FieldParseTable = new IniParseTable<CivilianSpawnCollideModuleData>
    {
        { "DeleteObjectFilter", (parser, x) => x.DeleteObjectFilter = ObjectFilter.Parse(parser) },
    };

    public ObjectFilter DeleteObjectFilter { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new CivilianSpawnCollide(gameObject, gameEngine.SimContext, this);
    }
}
