// Mocked-game unit tests for the CitadelSlaughterHordeContain port (R12): one test per
// task-packet test case, [create -> TryEnterHorde -> observable effect], plus the shadow-copy
// base test and the mid-state save/load round-trip. Object definitions are parsed from INI
// text through the real parser.
//
// "Cargo carried by the horde" (the dropped-ring interpretation the module header documents)
// is modeled by pointing a spawned object's ParentHorde at the entering horde directly - the
// same link HordeContainBehavior.Unpack sets on a horde's own members.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Contain;

public class CitadelSlaughterHordeContainContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_RingHero
  Type = OBJECT
End

Upgrade Upgrade_FortressRingHero
  Type = OBJECT
End

Object Citadel
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = CitadelSlaughterHordeContain ModuleTag_Slaughter
    PassengerFilter = +INFANTRY +CRATE
    AllowAlliesInside = No
    AllowEnemiesInside = No
    AllowNeutralInside = No
    AllowOwnPlayerInsideOverride = No
    StatusForRingEntry = HOLDING_THE_RING
    UpgradeForRingEntry = Upgrade_RingHero Upgrade_FortressRingHero
    ObjectToDestroyForRingEntry = +CRATE
    FXForRingEntry = FX_OneRingFlare
  End
End

Object CitadelAlliesAllowed
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = CitadelSlaughterHordeContain ModuleTag_Slaughter
    PassengerFilter = +INFANTRY +CRATE
    AllowAlliesInside = Yes
    AllowEnemiesInside = No
    AllowNeutralInside = No
    AllowOwnPlayerInsideOverride = No
    StatusForRingEntry = HOLDING_THE_RING
    UpgradeForRingEntry = Upgrade_RingHero
    ObjectToDestroyForRingEntry = +CRATE
    FXForRingEntry = FX_OneRingFlare
  End
End

Object CitadelOwnerOverride
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = CitadelSlaughterHordeContain ModuleTag_Slaughter
    PassengerFilter = -INFANTRY
    AllowAlliesInside = No
    AllowEnemiesInside = No
    AllowNeutralInside = No
    AllowOwnPlayerInsideOverride = Yes
    StatusForRingEntry = HOLDING_THE_RING
    UpgradeForRingEntry = Upgrade_RingHero
    ObjectToDestroyForRingEntry = +CRATE
    FXForRingEntry = FX_OneRingFlare
  End
End

Object RingHorde
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object DroppedRing
  KindOf = CRATE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC17ADE1)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static CitadelSlaughterHordeContain ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<CitadelSlaughterHordeContain>().Single();

    private static UpgradeTemplate UpgradeByName(HeadlessSimGame game, string name) =>
        game.AssetStore.Upgrades.GetByName(name);

    [Fact]
    public void HordeWithDroppedRing_AppliesRingStatusUpgradesAndDestroysRing()
    {
        var game = NewGame();
        var citadel = game.SpawnObject("Citadel", game.CivilianPlayer, Vector3.Zero);
        var horde = game.SpawnObject("RingHorde", game.CivilianPlayer, new Vector3(10, 0, 0));
        var ring = game.SpawnObject("DroppedRing", game.CivilianPlayer, new Vector3(10, 0, 0));
        ring.ParentHorde = horde;

        var module = ModuleOf(citadel);
        var entered = module.TryEnterHorde(horde);

        Assert.True(entered);
        Assert.True(horde.TestStatus(ObjectStatus.HoldingTheRing));
        Assert.True(horde.HasUpgrade(UpgradeByName(game, "Upgrade_RingHero")));
        Assert.True(horde.HasUpgrade(UpgradeByName(game, "Upgrade_FortressRingHero")));
        Assert.True(ring.IsDestroyed);
        // The horde itself survives to carry its new Ring status/upgrades.
        Assert.False(horde.IsDestroyed);
    }

    [Fact]
    public void HordeWithoutRing_OnlyStandardSlaughterProcessingOccurs()
    {
        var game = NewGame();
        var citadel = game.SpawnObject("Citadel", game.CivilianPlayer, Vector3.Zero);
        var horde = game.SpawnObject("RingHorde", game.CivilianPlayer, new Vector3(10, 0, 0));

        var module = ModuleOf(citadel);
        var entered = module.TryEnterHorde(horde);

        Assert.True(entered);
        Assert.False(horde.TestStatus(ObjectStatus.HoldingTheRing));
        Assert.False(horde.HasUpgrade(UpgradeByName(game, "Upgrade_RingHero")));
        // Standard slaughter/loot processing consumes the horde.
        Assert.True(horde.IsDestroyed);
    }

    [Fact]
    public void OwnerHordeWithOverride_EntersDespitePassengerFilterRestriction()
    {
        var game = NewGame();
        var citadel = game.SpawnObject("CitadelOwnerOverride", game.CivilianPlayer, Vector3.Zero);
        var horde = game.SpawnObject("RingHorde", game.CivilianPlayer, new Vector3(10, 0, 0));

        var module = ModuleOf(citadel);

        // PassengerFilter on CitadelOwnerOverride excludes INFANTRY outright, and
        // AllowAlliesInside is No - only AllowOwnPlayerInsideOverride lets this succeed.
        Assert.True(module.CanEnter(horde));
    }

    [Fact]
    public void MultipleUpgrades_AllGrantedAndPersistOnHorde()
    {
        var game = NewGame();
        var citadel = game.SpawnObject("Citadel", game.CivilianPlayer, Vector3.Zero);
        var horde = game.SpawnObject("RingHorde", game.CivilianPlayer, new Vector3(10, 0, 0));
        var ring = game.SpawnObject("DroppedRing", game.CivilianPlayer, new Vector3(10, 0, 0));
        ring.ParentHorde = horde;

        ModuleOf(citadel).TryEnterHorde(horde);

        Assert.True(horde.HasUpgrade(UpgradeByName(game, "Upgrade_RingHero")));
        Assert.True(horde.HasUpgrade(UpgradeByName(game, "Upgrade_FortressRingHero")));
        Assert.False(horde.IsDestroyed);
    }

    [Fact]
    public void RingAlreadyDestroyed_FilterMatchesNothing_FallsBackToStandardSlaughter()
    {
        var game = NewGame();
        var citadel = game.SpawnObject("Citadel", game.CivilianPlayer, Vector3.Zero);
        var horde = game.SpawnObject("RingHorde", game.CivilianPlayer, new Vector3(10, 0, 0));
        var ring = game.SpawnObject("DroppedRing", game.CivilianPlayer, new Vector3(10, 0, 0));
        ring.ParentHorde = horde;
        // The ring is destroyed before the horde enters: it drops out of ObjectsAscendingId,
        // so ObjectToDestroyForRingEntry matches nothing.
        game.GameLogic.DestroyObject(ring);
        game.GameLogic.DeleteDestroyed();

        var module = ModuleOf(citadel);
        module.TryEnterHorde(horde);

        Assert.False(horde.TestStatus(ObjectStatus.HoldingTheRing));
        Assert.False(horde.HasUpgrade(UpgradeByName(game, "Upgrade_RingHero")));
        Assert.True(horde.IsDestroyed);
    }

    [Fact]
    public void RingDestroyedSameFrameNotYetReaped_StillVisibleInObjectsAscendingId_ExcludedByIsDestroyedCheck()
    {
        // ISimContext.cs documents that DestroyObject marks an object destroyed immediately
        // but it stays visible (IsDestroyed == true) in ObjectsAscendingId until end-of-frame
        // reaping (DeleteDestroyed). This exercises exactly that same-frame window - unlike
        // RingAlreadyDestroyed_FilterMatchesNothing_FallsBackToStandardSlaughter above, this
        // test deliberately does NOT call DeleteDestroyed(), so the ring object is still
        // present in ObjectsAscendingId (with IsDestroyed true) when TryEnterHorde scans it.
        // FindRingEntryMatches must exclude it by IsDestroyed, not merely by list membership,
        // or a ring destroyed earlier in the same frame would still grant ring-entry status/
        // upgrades on a ghost object.
        var game = NewGame();
        var citadel = game.SpawnObject("Citadel", game.CivilianPlayer, Vector3.Zero);
        var horde = game.SpawnObject("RingHorde", game.CivilianPlayer, new Vector3(10, 0, 0));
        var ring = game.SpawnObject("DroppedRing", game.CivilianPlayer, new Vector3(10, 0, 0));
        ring.ParentHorde = horde;

        // Destroyed this same frame by an earlier module/combat - NOT yet reaped.
        game.GameLogic.DestroyObject(ring);

        var module = ModuleOf(citadel);
        var entered = module.TryEnterHorde(horde);

        Assert.True(entered);
        Assert.False(horde.TestStatus(ObjectStatus.HoldingTheRing));
        Assert.False(horde.HasUpgrade(UpgradeByName(game, "Upgrade_RingHero")));
        Assert.False(horde.HasUpgrade(UpgradeByName(game, "Upgrade_FortressRingHero")));
        // Falls back to standard slaughter processing since no live ring object matched.
        Assert.True(horde.IsDestroyed);
    }

    [Fact]
    public void AlliedHorde_RejectedWhenNoOverrideAndAlliesDisallowed_UnlessOwnedByCitadelOwner()
    {
        var game = NewGame();
        var citadel = game.SpawnObject("Citadel", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(citadel);

        var alliedPlayer = game.PlayerManager.NeutralPlayer;
        game.CivilianPlayer.AddAlly(alliedPlayer);
        alliedPlayer.AddAlly(game.CivilianPlayer);

        var alliedHorde = game.SpawnObject("RingHorde", alliedPlayer, new Vector3(10, 0, 0));
        var ownerHorde = game.SpawnObject("RingHorde", game.CivilianPlayer, new Vector3(20, 0, 0));

        // AllowOwnPlayerInsideOverride = No and AllowAlliesInside = No: an allied (but not
        // owner-player) horde is rejected...
        Assert.False(module.CanEnter(alliedHorde));

        // ...but the citadel owner's own horde still enters (same-player path is never gated
        // by AllowAlliesInside).
        Assert.True(module.CanEnter(ownerHorde));
    }

    [Fact]
    public void AlliedHorde_EntersWhenAllowAlliesInsideIsYes()
    {
        var game = NewGame();
        var citadel = game.SpawnObject("CitadelAlliesAllowed", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(citadel);

        var alliedPlayer = game.PlayerManager.NeutralPlayer;
        game.CivilianPlayer.AddAlly(alliedPlayer);
        alliedPlayer.AddAlly(game.CivilianPlayer);

        var alliedHorde = game.SpawnObject("RingHorde", alliedPlayer, new Vector3(10, 0, 0));

        Assert.True(module.CanEnter(alliedHorde));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();

        var liveHost = game.SpawnObject("Citadel", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(liveHost);
        var liveHorde = game.SpawnObject("RingHorde", game.CivilianPlayer, new Vector3(10, 0, 0));
        live.TryEnterHorde(liveHorde);

        var shadowHost = game.SpawnObject("Citadel", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_PreservesTriggeredFlag()
    {
        var game = NewGame();
        var citadel = game.SpawnObject("Citadel", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(citadel);

        var saved = PortedModuleTestKit.Save(module);

        var freshHost = game.SpawnObject("Citadel", game.CivilianPlayer, new Vector3(50, 0, 0));
        var fresh = ModuleOf(freshHost);

        PortedModuleTestKit.Load(fresh, saved);
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));
    }
}
