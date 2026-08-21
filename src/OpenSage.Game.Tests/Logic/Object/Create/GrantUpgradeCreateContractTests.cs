// Contract tests for the GrantUpgradeCreate port (experiment-round-4 §4.1 DoD item 4: one test
// per INI branch, minimum [create -> observable effect], plus the persisted-gate continuation
// that stands in for the mid-state save/load half - see GrantUpgradeCreate.md for why the raw
// byte-level round trip is a harness N/A for this class).
//
// The observable effect is the upgrade mask: GrantUpgradeCreate's whole job is to put an
// upgrade on the object (Type=OBJECT) or its owner (Type=PLAYER), so "did the module run" is
// "does HasUpgrade report the granted upgrade".

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Create;

public class GrantUpgradeCreateContractTests
{
    private const string ObjectUpgrade = "GrantTestObjectUpgrade";
    private const string PlayerUpgrade = "GrantTestPlayerUpgrade";

    private const string Definitions = @"
Upgrade " + ObjectUpgrade + @"
  Type = OBJECT
End

Upgrade " + PlayerUpgrade + @"
  Type = PLAYER
End

; grants an OBJECT upgrade at create time (GiveOnBuildComplete defaults to No)
Object GrantOnCreateObjectUnit
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GrantUpgradeCreate ModuleTag_Grant
    UpgradeToGrant = " + ObjectUpgrade + @"
  End
End

; grants a PLAYER upgrade at create time (routes to the owner, not the object mask)
Object GrantOnCreatePlayerUnit
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GrantUpgradeCreate ModuleTag_Grant
    UpgradeToGrant = " + PlayerUpgrade + @"
  End
End

; defers the grant to build-complete
Object GrantOnBuildCompleteUnit
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GrantUpgradeCreate ModuleTag_Grant
    UpgradeToGrant = " + ObjectUpgrade + @"
    GiveOnBuildComplete = Yes
  End
End

; ExemptStatus configured: the create-time grant is skipped when the object has UNDER_CONSTRUCTION
Object GrantExemptUnit
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GrantUpgradeCreate ModuleTag_Grant
    UpgradeToGrant = " + ObjectUpgrade + @"
    ExemptStatus = UNDER_CONSTRUCTION
  End
End

; UpgradeToGrant names an upgrade that does not exist -> silent no-op, no crash
Object GrantMissingUpgradeUnit
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GrantUpgradeCreate ModuleTag_Grant
    UpgradeToGrant = ThisUpgradeDoesNotExist
  End
End

; UpgradeToGrant omitted entirely -> silent no-op, no crash
Object GrantNoNameUnit
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GrantUpgradeCreate ModuleTag_Grant
  End
End
";

    private static readonly Vector3 Origin = new(0, 0, 0);

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x6247A17u);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GrantUpgradeCreate ModuleOf(GameObject gameObject) =>
        gameObject.FindBehavior<GrantUpgradeCreate>();

    private static UpgradeTemplate Upgrade(HeadlessSimGame game, string name) =>
        game.AssetStore.Upgrades.GetByName(name);

    private static GrantUpgradeCreateModuleData DataOf(HeadlessSimGame game, string definitionName) =>
        Assert.IsType<GrantUpgradeCreateModuleData>(
            game.AssetStore.ObjectDefinitions.GetByName(definitionName)
                .Behaviors.Values.Single(b => b.Data is GrantUpgradeCreateModuleData).Data);

    // ---- INI branch: default timing (create), OBJECT-type upgrade ----

    [Fact]
    public void OnCreate_GrantsObjectUpgradeToTheObject()
    {
        var game = NewGame();
        var unit = game.SpawnObject("GrantOnCreateObjectUnit", game.CivilianPlayer, Origin);

        Assert.True(unit.HasUpgrade(Upgrade(game, ObjectUpgrade)));
    }

    // ---- INI branch: PLAYER-type upgrade routes to the owner ----

    [Fact]
    public void OnCreate_GrantsPlayerUpgradeToTheOwner()
    {
        var game = NewGame();
        var unit = game.SpawnObject("GrantOnCreatePlayerUnit", game.CivilianPlayer, Origin);

        var playerUpgrade = Upgrade(game, PlayerUpgrade);
        // The player mask holds it, and the object's HasUpgrade delegates to the owner for PLAYER upgrades.
        Assert.True(game.CivilianPlayer.HasUpgrade(playerUpgrade));
        Assert.True(unit.HasUpgrade(playerUpgrade));
    }

    // ---- INI branch: GiveOnBuildComplete defers the grant ----

    [Fact]
    public void GiveOnBuildComplete_SkipsCreateAndGrantsOnBuildComplete()
    {
        var game = NewGame();
        var unit = game.SpawnObject("GrantOnBuildCompleteUnit", game.CivilianPlayer, Origin);
        var upgrade = Upgrade(game, ObjectUpgrade);

        // Nothing at create time...
        Assert.False(unit.HasUpgrade(upgrade));

        // ...granted when construction finishes.
        unit.FinishConstruction();
        Assert.True(unit.HasUpgrade(upgrade));
    }

    // ---- persisted-gate continuation (item 4 proxy): the base build-complete gate fires once ----

    [Fact]
    public void OnBuildComplete_GateIsConsumedAfterFirstFire()
    {
        var game = NewGame();
        var unit = game.SpawnObject("GrantOnBuildCompleteUnit", game.CivilianPlayer, Origin);
        var upgrade = Upgrade(game, ObjectUpgrade);

        unit.FinishConstruction();
        Assert.True(unit.HasUpgrade(upgrade));

        // Remove the upgrade and hit OnBuildComplete again: the CreateModule gate
        // (_shouldCallOnBuildComplete, the one persisted bit relevant to this class) has been
        // consumed, so it must NOT re-grant.
        unit.RemoveUpgrade(upgrade);
        ModuleOf(unit).OnBuildComplete();
        Assert.False(unit.HasUpgrade(upgrade));
    }

    // ---- INI branch: ExemptStatus gate, both directions ----

    [Fact]
    public void ExemptStatus_SkipsGrantWhenTheObjectHasTheStatus()
    {
        var game = NewGame();
        var unit = game.SpawnObject("GrantExemptUnit", game.CivilianPlayer, Origin);
        var upgrade = Upgrade(game, ObjectUpgrade);

        // Configured-but-absent at spawn (a fresh object has no status bits) -> the grant runs.
        Assert.True(unit.HasUpgrade(upgrade));

        // Now make the object exempt and re-run the create hook: the grant must be skipped.
        unit.RemoveUpgrade(upgrade);
        unit.SetObjectStatus(ObjectStatus.UnderConstruction, true);
        ModuleOf(unit).OnCreate();
        Assert.False(unit.HasUpgrade(upgrade));

        // Clear the exempt status and re-run: the grant resumes.
        unit.SetObjectStatus(ObjectStatus.UnderConstruction, false);
        ModuleOf(unit).OnCreate();
        Assert.True(unit.HasUpgrade(upgrade));
    }

    // ---- default: an unspecified ExemptStatus is None and never masks a real bit ----

    [Fact]
    public void ExemptStatus_DefaultsToNone()
    {
        var game = NewGame();
        Assert.Equal(ObjectStatus.None, DataOf(game, "GrantOnCreateObjectUnit").ExemptStatus);
    }

    // ---- INI branch: a missing upgrade asset is a silent no-op ----

    [Fact]
    public void MissingUpgradeName_IsASilentNoOp()
    {
        var game = NewGame();
        var unit = game.SpawnObject("GrantMissingUpgradeUnit", game.CivilianPlayer, Origin);

        // No crash, and nothing granted (the object mask is empty).
        Assert.False(unit.HasUpgrade(Upgrade(game, ObjectUpgrade)));
    }

    // ---- INI branch: an empty UpgradeToGrant is a silent no-op ----

    [Fact]
    public void EmptyUpgradeName_IsASilentNoOp()
    {
        var game = NewGame();
        var unit = game.SpawnObject("GrantNoNameUnit", game.CivilianPlayer, Origin);

        Assert.False(unit.HasUpgrade(Upgrade(game, ObjectUpgrade)));
    }
}
