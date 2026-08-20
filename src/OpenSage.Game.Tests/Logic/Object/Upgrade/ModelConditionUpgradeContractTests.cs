// Mocked-game unit tests for the ModelConditionUpgrade port (api-freeze-v1 §6 fitness
// item 4 shape, same kit StatusBitsUpgradeContractTests established): one test per
// INI-configurable branch, [create -> trigger -> observable effect], plus the shadow-copy
// base test and the mid-state save/load round-trip. Object definitions are parsed from INI
// text through the real parser, so the parse path (ParseEnum/ParseEnumBitArray/
// ParseAttributeEnum/ParseDurationLogicFramesSeconds) is exercised.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class ModelConditionUpgradeContractTests
{
    private const string Definitions = @"
Object SingleFlagBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ModelConditionUpgrade ModuleTag_Condition
    StartsActive = Yes
    ConditionFlag = CAPTURED
  End
End

Object BatchAddBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ModelConditionUpgrade ModuleTag_Condition
    StartsActive = Yes
    AddConditionFlags = CAPTURED GARRISONED
  End
End

Upgrade Upgrade_RemoveOne
  Type = PLAYER
End

Object SelectiveRemovalBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ModelConditionUpgrade ModuleTag_Base
    StartsActive = Yes
    AddConditionFlags = CAPTURED GARRISONED
  End
  Behavior = ModelConditionUpgrade ModuleTag_Remove
    TriggeredBy = Upgrade_RemoveOne
    RemoveConditionFlags = CAPTURED
  End
End

Upgrade Upgrade_RemoveRange
  Type = PLAYER
End

Object RangeRemovalBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ModelConditionUpgrade ModuleTag_Base
    StartsActive = Yes
    AddConditionFlags = DAMAGED REALLYDAMAGED RUBBLE
  End
  Behavior = ModelConditionUpgrade ModuleTag_Range
    TriggeredBy = Upgrade_RemoveRange
    RemoveConditionFlagsInRange = DAMAGED REALLYDAMAGED RUBBLE
  End
End

Upgrade Upgrade_TempFlag
  Type = PLAYER
End

Object TempFlagBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ModelConditionUpgrade ModuleTag_Condition
    TriggeredBy = Upgrade_TempFlag
    AddTempConditionFlag = ModelConditionState:NIGHT
    TempConditionTime = 1.0
  End
End

Upgrade Upgrade_ChainA
  Type = PLAYER
End

Upgrade Upgrade_ChainB
  Type = PLAYER
End

Object ChainedUpgradeBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ModelConditionUpgrade ModuleTag_First
    TriggeredBy = Upgrade_ChainA
    ConditionFlag = CAPTURED
  End
  Behavior = ModelConditionUpgrade ModuleTag_Second
    TriggeredBy = Upgrade_ChainB
    ConditionFlag = GARRISONED
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC12)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static ModelConditionUpgrade ConditionModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<ModelConditionUpgrade>().Single();

    private static UpgradeSet UpgradeSetOf(HeadlessSimGame game, string upgradeName) =>
        new UpgradeSet { game.AssetStore.Upgrades.GetByName(upgradeName) };

    [Fact]
    public void SingleFlagApplication_SetsConditionFlag_OnSpawn()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("SingleFlagBearer", game.CivilianPlayer, Vector3.Zero);

        Assert.True(bearer.ModelConditionFlags.Get(ModelConditionFlag.Captured));
        Assert.False(bearer.ModelConditionFlags.Get(ModelConditionFlag.Garrisoned));
    }

    [Fact]
    public void BatchFlagAddition_AppliesBothFlags_Simultaneously()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("BatchAddBearer", game.CivilianPlayer, Vector3.Zero);

        Assert.True(bearer.ModelConditionFlags.Get(ModelConditionFlag.Captured));
        Assert.True(bearer.ModelConditionFlags.Get(ModelConditionFlag.Garrisoned));
    }

    [Fact]
    public void SelectiveRemoval_RemovesOneFlag_PreservesOthers()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("SelectiveRemovalBearer", game.CivilianPlayer, Vector3.Zero);

        Assert.True(bearer.ModelConditionFlags.Get(ModelConditionFlag.Captured));
        Assert.True(bearer.ModelConditionFlags.Get(ModelConditionFlag.Garrisoned));

        var removeModule = bearer.BehaviorModules.OfType<ModelConditionUpgrade>()
            .Single(m => m.CanUpgrade(UpgradeSetOf(game, "Upgrade_RemoveOne")));
        removeModule.TryUpgrade(UpgradeSetOf(game, "Upgrade_RemoveOne"));

        Assert.False(bearer.ModelConditionFlags.Get(ModelConditionFlag.Captured));
        Assert.True(bearer.ModelConditionFlags.Get(ModelConditionFlag.Garrisoned));
    }

    [Fact]
    public void RangeRemoval_ClearsContiguousFlagBlock()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("RangeRemovalBearer", game.CivilianPlayer, Vector3.Zero);

        Assert.True(bearer.ModelConditionFlags.Get(ModelConditionFlag.Damaged));
        Assert.True(bearer.ModelConditionFlags.Get(ModelConditionFlag.ReallyDamaged));
        Assert.True(bearer.ModelConditionFlags.Get(ModelConditionFlag.Rubble));

        var rangeModule = bearer.BehaviorModules.OfType<ModelConditionUpgrade>()
            .Single(m => m.CanUpgrade(UpgradeSetOf(game, "Upgrade_RemoveRange")));
        rangeModule.TryUpgrade(UpgradeSetOf(game, "Upgrade_RemoveRange"));

        Assert.False(bearer.ModelConditionFlags.Get(ModelConditionFlag.Damaged));
        Assert.False(bearer.ModelConditionFlags.Get(ModelConditionFlag.ReallyDamaged));
        Assert.False(bearer.ModelConditionFlags.Get(ModelConditionFlag.Rubble));
    }

    [Fact]
    public void TempConditionFlag_ExpiresAndIsRemoved_AfterDuration()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("TempFlagBearer", game.CivilianPlayer, Vector3.Zero);
        var module = ConditionModuleOf(bearer);

        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_TempFlag"));

        // Applied immediately on trigger.
        Assert.True(bearer.ModelConditionFlags.Get(ModelConditionFlag.Night));

        // BFME2 runs at 5 Hz (frozen F6); TempConditionTime = 1.0s -> 5 frames from the
        // trigger frame (frame 0), so the module wakes at frame 5. GameLogic.Step() reads
        // the sleepy queue against the pre-increment frame counter (GameLogic.Update()), so
        // the wake actually runs on the 6th Step() call - still up through the 5th.
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        Assert.True(bearer.ModelConditionFlags.Get(ModelConditionFlag.Night));

        // The expiry frame clears it.
        game.Step();
        Assert.False(bearer.ModelConditionFlags.Get(ModelConditionFlag.Night));
    }

    [Fact]
    public void UpgradeChain_AppliesFlagsInOrder_WithoutConflicts()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("ChainedUpgradeBearer", game.CivilianPlayer, Vector3.Zero);

        Assert.False(bearer.ModelConditionFlags.Get(ModelConditionFlag.Captured));
        Assert.False(bearer.ModelConditionFlags.Get(ModelConditionFlag.Garrisoned));

        var modules = bearer.BehaviorModules.OfType<ModelConditionUpgrade>().ToList();
        var first = modules.Single(m => m.CanUpgrade(UpgradeSetOf(game, "Upgrade_ChainA")));
        var second = modules.Single(m => m.CanUpgrade(UpgradeSetOf(game, "Upgrade_ChainB")));

        first.TryUpgrade(UpgradeSetOf(game, "Upgrade_ChainA"));
        Assert.True(bearer.ModelConditionFlags.Get(ModelConditionFlag.Captured));
        Assert.False(bearer.ModelConditionFlags.Get(ModelConditionFlag.Garrisoned));

        second.TryUpgrade(UpgradeSetOf(game, "Upgrade_ChainB"));
        Assert.True(bearer.ModelConditionFlags.Get(ModelConditionFlag.Captured));
        Assert.True(bearer.ModelConditionFlags.Get(ModelConditionFlag.Garrisoned));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();

        var liveHost = game.SpawnObject("TempFlagBearer", game.CivilianPlayer, Vector3.Zero);
        var live = ConditionModuleOf(liveHost);
        live.TryUpgrade(UpgradeSetOf(game, "Upgrade_TempFlag"));

        var shadowHost = game.SpawnObject("TempFlagBearer", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ConditionModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_PreservesTempFlagBookkeeping()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("TempFlagBearer", game.CivilianPlayer, Vector3.Zero);
        var module = ConditionModuleOf(bearer);
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_TempFlag"));

        var saved = PortedModuleTestKit.Save(module);

        var freshHost = game.SpawnObject("TempFlagBearer", game.CivilianPlayer, new Vector3(50, 0, 0));
        var fresh = ConditionModuleOf(freshHost);
        Assert.NotEqual(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));

        PortedModuleTestKit.Load(fresh, saved);
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));
    }
}
