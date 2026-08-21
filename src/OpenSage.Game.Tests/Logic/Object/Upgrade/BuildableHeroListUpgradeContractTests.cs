// Mocked-game unit tests for the BuildableHeroListUpgrade port (R13): one test per
// INI-configurable branch, [create -> trigger -> observable Triggered flag], plus the
// StartsActive branch, the shadow-copy base test, and the mid-state save/load round-trip
// (both boolean values of the one xfered field). This module is a pure marker (see file
// header on the module) so the only observable is the shared upgrade-mux Triggered flag
// itself. Object definitions are parsed from INI text through the real parser.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class BuildableHeroListUpgradeContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_RingHero
  Type = PLAYER
End

Upgrade Upgrade_RingHeroConflict
  Type = PLAYER
End

Object PlainHeroHall
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = BuildableHeroListUpgrade ModuleTag_HeroList
    TriggeredBy = Upgrade_RingHero
  End
End

Object ConflictedHeroHall
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = BuildableHeroListUpgrade ModuleTag_HeroList
    TriggeredBy = Upgrade_RingHero
    ConflictsWith = Upgrade_RingHeroConflict
  End
End

Object StartsActiveHeroHall
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = BuildableHeroListUpgrade ModuleTag_HeroList
    TriggeredBy = Upgrade_RingHero
    StartsActive = Yes
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xBEE1)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static BuildableHeroListUpgrade HeroListModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<BuildableHeroListUpgrade>().Single();

    private static UpgradeSet UpgradeSetOf(HeadlessSimGame game, params string[] upgradeNames)
    {
        var set = new UpgradeSet();
        foreach (var name in upgradeNames)
        {
            set.Add(game.AssetStore.Upgrades.GetByName(name));
        }

        return set;
    }

    private static GameObject SpawnAndSettle(HeadlessSimGame game, string templateName)
    {
        var obj = game.SpawnObject(templateName, game.CivilianPlayer, Vector3.Zero);

        // Corpus-wide convention: assert post-spawn state only after two Step()s, matching
        // the "first sleepy Update runs on the second Step()" caveat - this module has no
        // Update() and is never enqueued in the sleepy-update queue, but other spawn-time
        // bookkeeping on the object may not be settled until the second Step().
        game.Step();
        game.Step();

        return obj;
    }

    [Fact]
    public void ParseRoundTrip_TriggeredByOnly_ProducesModuleWithNoOtherFieldsSet()
    {
        var game = NewGame();
        var horde = game.SpawnObject("PlainHeroHall", game.CivilianPlayer, Vector3.Zero);
        var module = HeroListModuleOf(horde);

        // Regression guard: confirms removing [ParseOnly] doesn't change parse behavior for
        // the zero-module-field case - the module exists, is untriggered, and only the
        // inherited TriggeredBy field drove construction.
        Assert.False(module.Triggered);
    }

    [Fact]
    public void NotTriggered_WhenPrerequisiteNotGranted()
    {
        var game = NewGame();
        var hall = SpawnAndSettle(game, "PlainHeroHall");
        var module = HeroListModuleOf(hall);

        Assert.False(module.Triggered);
    }

    [Fact]
    public void Triggered_WhenPrerequisiteGranted()
    {
        var game = NewGame();
        var hall = SpawnAndSettle(game, "PlainHeroHall");
        var module = HeroListModuleOf(hall);

        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_RingHero"));

        Assert.True(module.Triggered);
    }

    [Fact]
    public void TryUpgrade_AfterAlreadyTriggered_IsNoOp()
    {
        var game = NewGame();
        var hall = SpawnAndSettle(game, "PlainHeroHall");
        var module = HeroListModuleOf(hall);

        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_RingHero"));
        Assert.True(module.Triggered);

        // Already triggered: UpgradeLogic.CanUpgrade short-circuits, TryUpgrade is a no-op.
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_RingHero"));
        Assert.True(module.Triggered);
    }

    [Fact]
    public void ConflictingUpgrade_BlocksTriggering()
    {
        var game = NewGame();
        var hall = SpawnAndSettle(game, "ConflictedHeroHall");
        var module = HeroListModuleOf(hall);

        // Both the trigger and the conflicting upgrade are present at once: base
        // UpgradeLogic.CanUpgrade rejects when the conflict set overlaps, regardless of the
        // trigger set also overlapping.
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_RingHero", "Upgrade_RingHeroConflict"));

        Assert.False(module.Triggered);
    }

    [Fact]
    public void StartsActive_TriggersWithoutExplicitTryUpgrade()
    {
        var game = NewGame();
        var hall = SpawnAndSettle(game, "StartsActiveHeroHall");
        var module = HeroListModuleOf(hall);

        Assert.True(module.Triggered);
    }

    [Fact]
    public void MultipleInstances_MaintainIndependentTriggeredStates()
    {
        var game = NewGame();
        var triggeredHall = game.SpawnObject("PlainHeroHall", game.CivilianPlayer, Vector3.Zero);
        var untriggeredHall = game.SpawnObject("PlainHeroHall", game.CivilianPlayer, new Vector3(10, 0, 0));

        var triggeredModule = HeroListModuleOf(triggeredHall);
        var untriggeredModule = HeroListModuleOf(untriggeredHall);

        triggeredModule.TryUpgrade(UpgradeSetOf(game, "Upgrade_RingHero"));

        Assert.True(triggeredModule.Triggered);
        Assert.False(untriggeredModule.Triggered);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();

        var liveHost = game.SpawnObject("PlainHeroHall", game.CivilianPlayer, Vector3.Zero);
        var live = HeroListModuleOf(liveHost);
        live.TryUpgrade(UpgradeSetOf(game, "Upgrade_RingHero"));

        var shadowHost = game.SpawnObject("PlainHeroHall", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = HeroListModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_PreservesTriggeredFlag_Triggered()
    {
        var game = NewGame();
        var hall = game.SpawnObject("PlainHeroHall", game.CivilianPlayer, Vector3.Zero);
        var module = HeroListModuleOf(hall);
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_RingHero"));

        var saved = PortedModuleTestKit.Save(module);

        // A fresh instance starts untriggered; loading the saved state must flip it back to
        // triggered so its CRC matches the source.
        var freshHost = game.SpawnObject("PlainHeroHall", game.CivilianPlayer, new Vector3(50, 0, 0));
        var fresh = HeroListModuleOf(freshHost);
        Assert.NotEqual(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));

        PortedModuleTestKit.Load(fresh, saved);
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_PreservesTriggeredFlag_Untriggered()
    {
        var game = NewGame();
        var hall = game.SpawnObject("PlainHeroHall", game.CivilianPlayer, Vector3.Zero);
        var module = HeroListModuleOf(hall);
        // Left untriggered deliberately - covers the other boolean value of the one xfered
        // field (mirrors the triggered case above).

        var saved = PortedModuleTestKit.Save(module);

        var otherHost = game.SpawnObject("PlainHeroHall", game.CivilianPlayer, new Vector3(50, 0, 0));
        var other = HeroListModuleOf(otherHost);
        other.TryUpgrade(UpgradeSetOf(game, "Upgrade_RingHero"));
        Assert.NotEqual(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(other));

        PortedModuleTestKit.Load(other, saved);
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(other));
    }
}
