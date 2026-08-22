// R15 L1-11 (sweep ratchet), two residual crash classes from the frozen 20-map AotR sweep at
// main 9bde4556, both "an asset reference in an ObjectCreationList path did not resolve":
//
//   ace205b4  CreateObjectOCNugget.Execute / NullReferenceException  (2 maps:
//             "map good lothlorien" and "map sp evil mirkwood"; crash context
//             `frame=5 | object=#569 LorienBuildingTreeSeed1 | module=SlowDeathBehavior`).
//             GameLogic.CreateObject returns null when the ObjectDefinition reference does not
//             resolve ("TODO: Is this ever valid?"), and the nugget then dereferenced that null
//             on its very next line (newGameObject.UpdateTransform).
//
//   80734351  ObjectCreationUpgrade.OnUpgrade / NullReferenceException  (1 map:
//             "map sp good ettenmoors", mapObject #2095 RivendellWell, reached from
//             GrantUpgradeCreate.OnCreate during Scene3D.LoadObjects). The module's
//             UpgradeObject ObjectCreationList reference resolved to null and
//             `_moduleData.UpgradeObject.Value.Nuggets` NRE'd.
//
// Both terminated the process: the first on logic frame 5, the second during map load.
// AotR reaches them because some of its object/OCL definitions live in INI blocks the parser
// currently drops, while the content referencing them still loads (same mechanism as the R15
// HordeContain missing-UnitType class).
//
// Fixed behavior asserted here: the unresolved reference is skipped with one contextual
// warning, whatever DOES resolve is still created, and nothing throws.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.SweepResidual;

public class SweepResidualObjectCreationTests
{
    private const string Definitions = @"
Upgrade Upgrade_SweepWellSpawn
  Type = OBJECT
End

Object SweepSpawnling
  KindOf = INFANTRY SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End

Object SweepTreeSeed
  KindOf = STRUCTURE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

; The lothlorien shape: the nugget names an object template that was never defined.
ObjectCreationList OCL_SweepMissingSpawn
  CreateObject
    ObjectNames = NoSuchSpawnedObject
    Count = 1
  End
End

; Control: the nugget names a template that does resolve.
ObjectCreationList OCL_SweepValidSpawn
  CreateObject
    ObjectNames = SweepSpawnling
    Count = 1
  End
End

; The ettenmoors shape: an ObjectCreationUpgrade with no resolvable UpgradeObject.
Object SweepWellNoOcl
  KindOf = STRUCTURE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ObjectCreationUpgrade ModuleTag_Ocu
    TriggeredBy = Upgrade_SweepWellSpawn
  End
End

; Control: the same module with an OCL that resolves.
Object SweepWellWithOcl
  KindOf = STRUCTURE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ObjectCreationUpgrade ModuleTag_Ocu
    TriggeredBy = Upgrade_SweepWellSpawn
    UpgradeObject = OCL_SweepValidSpawn
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0x0C11);
        game.LoadIniText(Definitions);
        return game;
    }

    [Fact]
    public void CreateObjectNugget_UnresolvedObjectName_CreatesNothing_AndDoesNotThrow()
    {
        var game = NewGame();
        var source = game.SpawnObject("SweepTreeSeed", game.CivilianPlayer, Vector3.Zero);
        var ocl = game.AssetStore.ObjectCreationLists.GetByName("OCL_SweepMissingSpawn");

        // The regression: this used to NRE on newGameObject.UpdateTransform.
        var created = game.GameEngine.ObjectCreationLists.Create(ocl, source, game.GameEngine);

        Assert.Empty(created);
        Assert.Contains(source, game.GameLogic.Objects);
    }

    [Fact]
    public void CreateObjectNugget_ResolvedObjectName_StillCreatesTheObject()
    {
        var game = NewGame();
        var source = game.SpawnObject("SweepTreeSeed", game.CivilianPlayer, Vector3.Zero);
        var ocl = game.AssetStore.ObjectCreationLists.GetByName("OCL_SweepValidSpawn");

        var created = game.GameEngine.ObjectCreationLists.Create(ocl, source, game.GameEngine);

        // Control: the guard must only skip the unresolvable spawn, never a good one.
        Assert.Single(created);
        Assert.Equal("SweepSpawnling", created[0].Definition.Name);
    }

    [Fact]
    public void ObjectCreationUpgrade_WithoutUpgradeObject_AppliesTheUpgrade_AndDoesNotThrow()
    {
        var game = NewGame();
        var well = game.SpawnObject("SweepWellNoOcl", game.CivilianPlayer, Vector3.Zero);
        var upgrade = game.AssetStore.Upgrades.GetByName("Upgrade_SweepWellSpawn");

        // The regression: OnUpgrade used to NRE on _moduleData.UpgradeObject.Value.Nuggets.
        well.Upgrade(upgrade);

        Assert.Contains(well, game.GameLogic.Objects);
    }

    [Fact]
    public void ObjectCreationUpgrade_WithUpgradeObject_StillCreatesTheObject()
    {
        var game = NewGame();
        var well = game.SpawnObject("SweepWellWithOcl", game.CivilianPlayer, Vector3.Zero);
        var upgrade = game.AssetStore.Upgrades.GetByName("Upgrade_SweepWellSpawn");
        var before = game.GameLogic.Objects.Count();

        well.Upgrade(upgrade);

        // Control: the early return must fire only when the OCL is genuinely missing.
        Assert.Equal(before + 1, game.GameLogic.Objects.Count());
    }
}
