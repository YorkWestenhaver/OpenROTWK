// Mocked-game unit tests for the FXListDie port (experiment-round-4 §4.1, DoD item 4): one
// test per INI-configurable branch, each [create -> trigger death -> observable effect] via
// the batch's shared death-trigger helper, plus the shadow-copy base test taken mid-behavior
// and a save/load continuation. Object definitions are parsed from INI text through the real
// parser, so the real parse table (including the mux child table) is on the tested path.
//
// The observable effect of this class is an ISimEvents request, so the tests install the
// recording sink and assert on what the sim asked the client to do.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class FXListDieContractTests
{
    private const string Definitions = @"
FXList FX_Death
End

FXList FX_OtherDeath
End

Upgrade Upgrade_DeathTrigger
  Type = PLAYER
End

Upgrade Upgrade_NoCorpse
  Type = PLAYER
End

Object Corpse
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FXListDie ModuleTag_Die
    DeathFX = FX_Death
  End
End

Object UnorientedCorpse
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FXListDie ModuleTag_Die
    DeathFX = FX_Death
    OrientToObject = No
  End
End

Object BurnOnlyCorpse
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FXListDie ModuleTag_Die
    DeathTypes = NONE +BURNED
    DeathFX = FX_Death
  End
End

Object SilentCorpse
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FXListDie ModuleTag_Die
  End
End

Object UpgradeGatedCorpse
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FXListDie ModuleTag_Die
    StartsActive = No
    TriggeredBy = Upgrade_DeathTrigger
    DeathFX = FX_Death
  End
End

Object ConflictedCorpse
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FXListDie ModuleTag_Die
    ConflictsWith = Upgrade_NoCorpse
    DeathFX = FX_Death
  End
End

Object TwoFXCorpse
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FXListDie ModuleTag_Die
    DeathTypes = NONE +BURNED
    DeathFX = FX_Death
  End
  Behavior = FXListDie ModuleTag_Die2
    DeathTypes = ALL -BURNED
    DeathFX = FX_OtherDeath
  End
End

Object Killer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xD1E)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static FXListDie DieModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<FXListDie>().First();

    // ---- branch: the default block (StartsActive defaulted on, OrientToObject defaulted on)

    [Fact]
    public void Death_FiresTheDeathFX_OrientedToTheObject_WithTheKillerAsSource()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);
        var killer = game.SpawnObject("Killer", game.CivilianPlayer, new Vector3(5, 0, 0));

        var (corpse, _) = PortedModuleTestKit.SpawnAndKill(
            game, "Corpse", game.CivilianPlayer, Vector3.Zero, source: killer);

        var fx = Assert.Single(recorder.Events);
        Assert.Equal("FX_Death", fx.FXListName);
        Assert.Equal(corpse.Id, fx.ObjectId);
        Assert.Equal(killer.Id, fx.SourceObjectId);
        Assert.Equal(FXOrientation.ToObject, fx.Orientation);
    }

    [Fact]
    public void SubLethalDamage_FiresNothing()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);
        var corpse = game.SpawnObject("Corpse", game.CivilianPlayer, Vector3.Zero);

        var result = PortedModuleTestKit.ApplyDamage(corpse, 40f);

        Assert.False(result.Died);
        Assert.Empty(recorder.Events);
    }

    // ---- branch: OrientToObject = No

    [Fact]
    public void OrientToObjectNo_FiresThePositionOnlyRequest()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);

        var (corpse, _) = PortedModuleTestKit.SpawnAndKill(
            game, "UnorientedCorpse", game.CivilianPlayer, new Vector3(3, 4, 0));

        var fx = Assert.Single(recorder.Events);
        Assert.Equal(FXOrientation.PositionOnly, fx.Orientation);
        Assert.Equal(corpse.Id, fx.ObjectId);
        Assert.Equal(ObjectId.Invalid, fx.SourceObjectId);
    }

    // ---- branch: DeathTypes filter (the base's applicability gate, reached through this class)

    [Fact]
    public void DeathTypesFilter_FiresOnTheListedDeath_AndNotOnOthers()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);

        PortedModuleTestKit.SpawnAndKill(
            game, "BurnOnlyCorpse", game.CivilianPlayer, Vector3.Zero, DeathType.Normal);
        Assert.Empty(recorder.Events);

        PortedModuleTestKit.SpawnAndKill(
            game, "BurnOnlyCorpse", game.CivilianPlayer, new Vector3(10, 0, 0), DeathType.Burned);
        Assert.Single(recorder.Events);
    }

    [Fact]
    public void TwoModulesOnOneObject_OnlyTheMatchingDeathTypeFires()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);

        PortedModuleTestKit.SpawnAndKill(
            game, "TwoFXCorpse", game.CivilianPlayer, Vector3.Zero, DeathType.Burned);

        Assert.Equal("FX_Death", Assert.Single(recorder.Events).FXListName);
    }

    // ---- branch: no DeathFX named

    [Fact]
    public void NoDeathFX_FiresNothing_ButStillDies()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);

        var (_, result) = PortedModuleTestKit.SpawnAndKill(
            game, "SilentCorpse", game.CivilianPlayer, Vector3.Zero);

        Assert.True(result.Died);
        Assert.Empty(recorder.Events);
    }

    // ---- branch: StartsActive = No + TriggeredBy

    [Fact]
    public void UpgradeGated_SilentUntilTriggered_ThenFires()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);

        PortedModuleTestKit.SpawnAndKill(
            game, "UpgradeGatedCorpse", game.CivilianPlayer, Vector3.Zero);
        Assert.Empty(recorder.Events);

        var triggered = game.SpawnObject("UpgradeGatedCorpse", game.CivilianPlayer, new Vector3(10, 0, 0));
        DieModuleOf(triggered).TryUpgrade(new UpgradeSet
        {
            game.AssetStore.Upgrades.GetByName("Upgrade_DeathTrigger"),
        });

        PortedModuleTestKit.TriggerDeath(triggered);
        Assert.Single(recorder.Events);
    }

    // ---- branch: ConflictsWith, re-checked at death time

    [Fact]
    public void ConflictingUpgradeCompletedAfterBirth_SuppressesTheFX()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);

        // Same definition, same player: the only difference is that the second object
        // completes the conflicting upgrade before it dies.
        var plain = game.SpawnObject("ConflictedCorpse", game.CivilianPlayer, Vector3.Zero);
        PortedModuleTestKit.TriggerDeath(plain);
        Assert.Single(recorder.Events);

        var conflicted = game.SpawnObject("ConflictedCorpse", game.CivilianPlayer, new Vector3(10, 0, 0));
        conflicted.Upgrade(game.AssetStore.Upgrades.GetByName("Upgrade_NoCorpse"));

        PortedModuleTestKit.TriggerDeath(conflicted);
        Assert.Single(recorder.Events);   // still one: the second death was suppressed
    }

    // ---- the walk

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();

        // "Mid-behavior" for a mux-gated Die module means the triggered flag has actually
        // moved: the live module starts inactive and is upgraded, the shadow never is. If
        // the flag were missing from the walk the two would still compare equal, so the
        // shadow is deliberately in the OTHER state before the load.
        var liveHost = game.SpawnObject("UpgradeGatedCorpse", game.CivilianPlayer, Vector3.Zero);
        var live = DieModuleOf(liveHost);
        DieModuleOf(liveHost).TryUpgrade(new UpgradeSet
        {
            game.AssetStore.Upgrades.GetByName("Upgrade_DeathTrigger"),
        });

        var shadowHost = game.SpawnObject("UpgradeGatedCorpse", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = DieModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void TriggeredFlag_IsInTheWalk_SoTheCrcSeparatesTheTwoStates()
    {
        var game = NewGame();
        var inactive = DieModuleOf(game.SpawnObject("UpgradeGatedCorpse", game.CivilianPlayer, Vector3.Zero));
        var activeHost = game.SpawnObject("UpgradeGatedCorpse", game.CivilianPlayer, new Vector3(10, 0, 0));
        DieModuleOf(activeHost).TryUpgrade(new UpgradeSet
        {
            game.AssetStore.Upgrades.GetByName("Upgrade_DeathTrigger"),
        });

        Assert.NotEqual(
            PortedModuleTestKit.LiveCrc(inactive),
            PortedModuleTestKit.LiveCrc(DieModuleOf(activeHost)));
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        // The behavior under save/load is "does this object still make its FX when it dies?".
        // Game A never round-trips; game B saves and reloads the module between the upgrade
        // and the death. A load that lost the triggered flag makes B silent.
        Assert.Equal(RunScenario(roundTrip: false), RunScenario(roundTrip: true));
    }

    private static int RunScenario(bool roundTrip)
    {
        var game = NewGame(seed: 0xFEED);
        var recorder = RecordingSimEvents.InstallOn(game);

        var host = game.SpawnObject("UpgradeGatedCorpse", game.CivilianPlayer, Vector3.Zero);
        var module = DieModuleOf(host);
        module.TryUpgrade(new UpgradeSet
        {
            game.AssetStore.Upgrades.GetByName("Upgrade_DeathTrigger"),
        });

        game.Step();

        if (roundTrip)
        {
            var state = PortedModuleTestKit.Save(module);
            PortedModuleTestKit.Load(module, state);
        }

        game.Step();
        PortedModuleTestKit.TriggerDeath(host);
        game.Step();

        return recorder.Events.Count;
    }
}
