// Mocked-game contract tests for the DoCommandUpgrade port (R12): one test per INI-configurable
// branch (StartsActive/TriggeredBy/RequiresAllTriggers/ConflictsWith/ActiveDuringConstruction),
// the Permanent-flag reaction to a (currently test-only, see the module's OnUpgradeRemoved doc)
// upgrade removal, and the shadow-copy base test. Object definitions are parsed from INI text
// through the real parser, so the parse path (GetUpgradeCommandButtonName/
// RemoveUpgradeCommandButtonName = ParseAssetReference) is exercised.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class DoCommandUpgradeContractTests
{
    private const string Definitions = @"
Object ActiveCommandBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DoCommandUpgrade ModuleTag_Command
    StartsActive = Yes
    GetUpgradeCommandButtonName = Command_DoSomething
    RemoveUpgradeCommandButtonName = Command_UndoSomething
  End
End

Upgrade Upgrade_UnlockCommand
  Type = PLAYER
End

Upgrade Upgrade_ConflictingCommand
  Type = PLAYER
End

Upgrade Upgrade_SecondTrigger
  Type = PLAYER
End

Object GatedCommandBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DoCommandUpgrade ModuleTag_Command
    TriggeredBy = Upgrade_UnlockCommand
    GetUpgradeCommandButtonName = Command_DoSomething
    RemoveUpgradeCommandButtonName = Command_UndoSomething
  End
End

Object AllTriggersCommandBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DoCommandUpgrade ModuleTag_Command
    TriggeredBy = Upgrade_UnlockCommand Upgrade_SecondTrigger
    RequiresAllTriggers = Yes
    GetUpgradeCommandButtonName = Command_DoSomething
  End
End

Object ConflictedCommandBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DoCommandUpgrade ModuleTag_Command
    TriggeredBy = Upgrade_UnlockCommand
    ConflictsWith = Upgrade_ConflictingCommand
    GetUpgradeCommandButtonName = Command_DoSomething
  End
End

Object PermanentCommandBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DoCommandUpgrade ModuleTag_Command
    TriggeredBy = Upgrade_UnlockCommand
    Permanent = Yes
    GetUpgradeCommandButtonName = Command_DoSomething
  End
End

Object TemporaryCommandBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DoCommandUpgrade ModuleTag_Command
    TriggeredBy = Upgrade_UnlockCommand
    Permanent = No
    GetUpgradeCommandButtonName = Command_DoSomething
  End
End

Object ConstructionGatedCommandBearer
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DoCommandUpgrade ModuleTag_Command
    StartsActive = Yes
    GetUpgradeCommandButtonName = Command_DoSomething
  End
End

Object ActiveDuringConstructionCommandBearer
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DoCommandUpgrade ModuleTag_Command
    StartsActive = Yes
    ActiveDuringConstruction = Yes
    GetUpgradeCommandButtonName = Command_DoSomething
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xD0C0)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static DoCommandUpgrade ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<DoCommandUpgrade>().Single();

    private static UpgradeSet UpgradeSetOf(HeadlessSimGame game, params string[] upgradeNames)
    {
        var set = new UpgradeSet();
        foreach (var name in upgradeNames)
        {
            set.Add(game.AssetStore.Upgrades.GetByName(name));
        }
        return set;
    }

    [Fact]
    public void StartsActive_TriggersOnSpawn_AndExposesTheCommandButton()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("ActiveCommandBearer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(bearer);

        Assert.True(module.Triggered);
        Assert.True(module.IsCommandAvailable);
        Assert.Equal("Command_DoSomething", module.ActiveCommandButtonName);
    }

    [Fact]
    public void UpgradeGated_CommandUnavailableUntilTriggered_ThenAvailable()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("GatedCommandBearer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(bearer);

        // Not triggered yet: the command is unavailable, and OnUpgrade has not fired.
        Assert.False(module.Triggered);
        Assert.False(module.IsCommandAvailable);
        Assert.Null(module.ActiveCommandButtonName);

        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_UnlockCommand"));

        // Triggered: OnUpgrade fired once, the command is now available.
        Assert.True(module.Triggered);
        Assert.True(module.IsCommandAvailable);
        Assert.Equal("Command_DoSomething", module.ActiveCommandButtonName);
    }

    [Fact]
    public void SecondUpgradeAttempt_IsIdempotent_OnUpgradeFiresOnlyOnce()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("GatedCommandBearer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(bearer);

        var upgrades = UpgradeSetOf(game, "Upgrade_UnlockCommand");
        module.TryUpgrade(upgrades);
        Assert.True(module.Triggered);

        // A second identical attempt is a no-op (CanUpgrade gates on _triggered), so OnUpgrade
        // does not re-fire - nothing to observe differently, but the flag stays exactly set.
        module.TryUpgrade(upgrades);

        Assert.True(module.Triggered);
        Assert.True(module.IsCommandAvailable);
    }

    [Fact]
    public void RequiresAllTriggers_NeedsEveryPrerequisiteUpgrade()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("AllTriggersCommandBearer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(bearer);

        // Only one of the two required triggers: still gated.
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_UnlockCommand"));
        Assert.False(module.Triggered);

        // Both required triggers present: now it fires.
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_UnlockCommand", "Upgrade_SecondTrigger"));
        Assert.True(module.Triggered);
    }

    [Fact]
    public void ConflictsWith_BlocksActivation_WhileTheConflictingUpgradeIsPresent()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("ConflictedCommandBearer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(bearer);

        // The trigger is present, but so is the conflicting upgrade: blocked.
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_UnlockCommand", "Upgrade_ConflictingCommand"));
        Assert.False(module.Triggered);

        // Conflict gone: now it fires.
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_UnlockCommand"));
        Assert.True(module.Triggered);
    }

    [Fact]
    public void Permanent_Yes_KeepsTheModuleActive_WhenTheTriggerIsRemoved()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("PermanentCommandBearer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(bearer);

        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_UnlockCommand"));
        Assert.True(module.Triggered);

        module.OnUpgradeRemoved();

        Assert.True(module.Triggered);
        Assert.True(module.IsCommandAvailable);
    }

    [Fact]
    public void Permanent_No_DeactivatesTheModule_WhenTheTriggerIsRemoved()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("TemporaryCommandBearer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(bearer);

        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_UnlockCommand"));
        Assert.True(module.Triggered);

        module.OnUpgradeRemoved();

        Assert.False(module.Triggered);
        Assert.False(module.IsCommandAvailable);
    }

    [Fact]
    public void UnderConstruction_WithoutActiveDuringConstruction_CommandIsUnavailable()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("ConstructionGatedCommandBearer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(bearer);

        Assert.True(module.Triggered);
        Assert.True(module.IsCommandAvailable);

        bearer.ModelConditionFlags.Set(ModelConditionFlag.ActivelyBeingConstructed, true);

        // Triggered still holds, but the derived availability defers to construction state.
        Assert.True(module.Triggered);
        Assert.False(module.IsCommandAvailable);
        Assert.Null(module.ActiveCommandButtonName);
    }

    [Fact]
    public void ActiveDuringConstruction_Yes_KeepsTheCommandAvailable_WhileUnderConstruction()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("ActiveDuringConstructionCommandBearer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(bearer);

        bearer.ModelConditionFlags.Set(ModelConditionFlag.ActivelyBeingConstructed, true);

        Assert.True(module.Triggered);
        Assert.True(module.IsCommandAvailable);
        Assert.Equal("Command_DoSomething", module.ActiveCommandButtonName);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();

        var liveHost = game.SpawnObject("GatedCommandBearer", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(liveHost);
        live.TryUpgrade(UpgradeSetOf(game, "Upgrade_UnlockCommand"));

        var shadowHost = game.SpawnObject("GatedCommandBearer", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_PreservesTriggeredFlag()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("GatedCommandBearer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(bearer);
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_UnlockCommand"));

        var saved = PortedModuleTestKit.Save(module);

        var freshHost = game.SpawnObject("GatedCommandBearer", game.CivilianPlayer, new Vector3(50, 0, 0));
        var fresh = ModuleOf(freshHost);
        Assert.NotEqual(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));

        PortedModuleTestKit.Load(fresh, saved);
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));
    }
}
