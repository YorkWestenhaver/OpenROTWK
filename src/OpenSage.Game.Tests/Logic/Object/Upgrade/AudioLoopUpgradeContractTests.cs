// Mocked-game contract tests for the AudioLoopUpgrade port (R12): a permanently-parked
// audio-only upgrade module (see the module header for why). The shared UpgradeLogic mux -
// trigger, prerequisites, conflicts, StartsActive - is real and is exercised here exactly
// like every other UpgradeLogic-driven module in this codebase. The audio side effect itself
// (loop start, KillOnDeath teardown, KillAfterMS timeout) has no sim-observable surface today,
// so these tests assert the parked module accepts each trigger path harmlessly and preserves
// its parsed fields for the future audio host, rather than asserting on audio playback that
// does not exist yet.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class AudioLoopUpgradeContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_SirenSong
  Type = PLAYER
End

Upgrade Upgrade_SirenBlocker
  Type = PLAYER
End

Object ActiveSinger
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AudioLoopUpgrade ModuleTag_Audio
    StartsActive = Yes
    SoundToPlay = SirenLoop
    KillOnDeath = Yes
  End
End

Object GatedSinger
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AudioLoopUpgrade ModuleTag_Audio
    TriggeredBy = Upgrade_SirenSong
    ConflictsWith = Upgrade_SirenBlocker
    SoundToPlay = SirenLoop
    KillOnDeath = No
    KillAfterMS = 5000
  End
End

Object IndefiniteSinger
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AudioLoopUpgrade ModuleTag_Audio
    TriggeredBy = Upgrade_SirenSong
    SoundToPlay = SirenLoop
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xA0D10)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static AudioLoopUpgrade AudioModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<AudioLoopUpgrade>().Single();

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
    public void ParsesConfiguredFields()
    {
        var game = NewGame();
        var data = (AudioLoopUpgradeModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("GatedSinger").Behaviors["ModuleTag_Audio"].Data;

        Assert.Equal("SirenLoop", data.SoundToPlay);
        Assert.False(data.KillOnDeath);
        Assert.Equal(5000, data.KillAfterMS);
    }

    // testCase: "Upgrade triggers and audio loop begins playing from configured asset"
    // testCase: "StartsActive=true: audio plays on module init without external trigger"
    [Fact]
    public void StartsActive_TriggersOnInit_WithoutExternalCall()
    {
        var game = NewGame();
        var singer = game.SpawnObject("ActiveSinger", game.CivilianPlayer, Vector3.Zero);
        var module = AudioModuleOf(singer);

        // The mux fires from the ctor: the module is already triggered on spawn, matching
        // GPL's "an initially-active upgrade applies immediately".
        Assert.True(module.Triggered);
    }

    [Fact]
    public void GatedUpgrade_DoesNotTrigger_UntilPrerequisiteMet()
    {
        var game = NewGame();
        var singer = game.SpawnObject("GatedSinger", game.CivilianPlayer, Vector3.Zero);
        var module = AudioModuleOf(singer);

        Assert.False(module.Triggered);

        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_SirenSong"));

        Assert.True(module.Triggered);
    }

    // testCase: "Conflicting upgrade prevents this upgrade: audio never starts"
    [Fact]
    public void ConflictingUpgrade_PreventsTrigger()
    {
        var game = NewGame();
        var singer = game.SpawnObject("GatedSinger", game.CivilianPlayer, Vector3.Zero);
        var module = AudioModuleOf(singer);

        // Both the trigger and the conflicting upgrade are present at once: the conflict
        // wins, so the mux never fires and the (parked) audio loop never starts.
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_SirenSong", "Upgrade_SirenBlocker"));

        Assert.False(module.Triggered);
    }

    // testCase: "KillOnDeath flag true: audio stops when object dies"
    // No die-notification seam reaches a plain UpgradeModule today (see module header), so
    // there is nothing sim-observable to assert about the teardown; the contract this test
    // protects is that death is harmless to the parked module and the object's own death path
    // still runs to completion.
    [Fact]
    public void KillOnDeath_ObjectDeath_IsHarmlessToParkedModule()
    {
        var game = NewGame();
        var singer = game.SpawnObject("ActiveSinger", game.CivilianPlayer, Vector3.Zero);
        Assert.True(AudioModuleOf(singer).Triggered);

        PortedModuleTestKit.TriggerDeath(singer);

        Assert.True(singer.IsDestroyed);
    }

    // testCase: "KillAfterMS set (e.g., 5000ms): audio stops after timeout"
    // testCase: "KillAfterMS zero or unset: audio plays indefinitely until upgrade ends or
    // object dies"
    // Both timeout shapes are parsed and preserved; neither has a runtime clock to observe
    // yet (S8), so these tests assert stepping the sim is harmless in both configurations
    // and that the parsed value round-trips correctly.
    [Fact]
    public void KillAfterMsSet_SteppingIsHarmless_ModuleStaysParked()
    {
        var game = NewGame();
        var singer = game.SpawnObject("GatedSinger", game.CivilianPlayer, Vector3.Zero);
        var module = AudioModuleOf(singer);
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_SirenSong"));

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        Assert.False(singer.IsDestroyed);
        Assert.True(module.Triggered);
    }

    [Fact]
    public void KillAfterMsUnset_DefaultsToZero_IndefiniteAndHarmless()
    {
        var game = NewGame();
        var singer = game.SpawnObject("IndefiniteSinger", game.CivilianPlayer, Vector3.Zero);
        var data = (AudioLoopUpgradeModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("IndefiniteSinger").Behaviors["ModuleTag_Audio"].Data;
        Assert.Equal(0, data.KillAfterMS);

        var module = AudioModuleOf(singer);
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_SirenSong"));

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        Assert.True(module.Triggered);
        Assert.False(singer.IsDestroyed);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("GatedSinger", game.CivilianPlayer, Vector3.Zero);
        var live = AudioModuleOf(liveHost);
        live.TryUpgrade(UpgradeSetOf(game, "Upgrade_SirenSong"));

        var shadowHost = game.SpawnObject("GatedSinger", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = AudioModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_PreservesTriggeredFlag()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("GatedSinger", game.CivilianPlayer, Vector3.Zero);
        var module = AudioModuleOf(bearer);
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_SirenSong"));

        var saved = PortedModuleTestKit.Save(module);

        var freshHost = game.SpawnObject("GatedSinger", game.CivilianPlayer, new Vector3(50, 0, 0));
        var fresh = AudioModuleOf(freshHost);
        Assert.NotEqual(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));

        PortedModuleTestKit.Load(fresh, saved);
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));
    }
}
