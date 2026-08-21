// Mocked-game contract tests for the EntEnragedUpdate port (R13,
// modules-r13/specs/EntEnragedUpdateModuleData.md §3): the periodic scan-based trigger proxy
// (dead ally + hated enemy both within ScanDistance -> Enraged), the EnragedTime/
// TimeUntilCanRageAgain cooldown cycle, the EnragedOnBuffFX/EnragedOffBuffFX edge-fire
// idempotency, and the shared shadow-copy base test in both phases. Object definitions are
// parsed from INI text through the real parser, so the EnragedTime/TimeUntilCanRageAgain
// quantizing S5 parse is on the tested path.
//
// Sleepy-update caveat (spec §3): a module that calls SetWakeFrame(UpdateSleepTime.None) in its
// constructor has its first real Update() execute on the SECOND HeadlessSimGame.Step() call,
// not the first. Every case below steps well past that offset plus a full ScanCadence (5
// frames) window before asserting "triggered"/"not yet triggered".

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class EntEnragedUpdateContractTests
{
    // EnragedTime 1000 ms -> 5 frames, TimeUntilCanRageAgain 2000 ms -> 10 frames at the
    // frozen 5 Hz (F6).
    private const string Definitions = @"
Object Enrager
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EntEnragedUpdate ModuleTag_Enraged
    HatedObjectFilter = ANY +INFANTRY ENEMIES
    FriendlyDeadFilter = ANY +INFANTRY
    ScanDistance = 50
    EnragedTime = 1000
    TimeUntilCanRageAgain = 2000
  End
End

Object NarrowScanEnrager
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EntEnragedUpdate ModuleTag_Enraged
    HatedObjectFilter = ANY +INFANTRY ENEMIES
    FriendlyDeadFilter = ANY +INFANTRY
    ScanDistance = 20
    EnragedTime = 1000
    TimeUntilCanRageAgain = 2000
  End
End

Object NoScanEnrager
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EntEnragedUpdate ModuleTag_Enraged
    HatedObjectFilter = ANY +INFANTRY ENEMIES
    FriendlyDeadFilter = ANY +INFANTRY
    EnragedTime = 1000
    TimeUntilCanRageAgain = 2000
  End
End

Object ZeroCooldownEnrager
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EntEnragedUpdate ModuleTag_Enraged
    HatedObjectFilter = ANY +INFANTRY ENEMIES
    FriendlyDeadFilter = ANY +INFANTRY
    ScanDistance = 50
    EnragedTime = 1000
    TimeUntilCanRageAgain = 0
  End
End

Object FXEnrager
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EntEnragedUpdate ModuleTag_Enraged
    HatedObjectFilter = ANY +INFANTRY ENEMIES
    FriendlyDeadFilter = ANY +INFANTRY
    ScanDistance = 50
    EnragedTime = 1000
    TimeUntilCanRageAgain = 2000
    EnragedOnBuffFX = FX_EnrageOn
    EnragedOffBuffFX = FX_EnrageOff
  End
End

Object Grunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xE2A6E)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static void StepFrames(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    private static EntEnragedUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<EntEnragedUpdate>().Single();

    private static bool EnragedFlag(GameObject obj) =>
        obj.ModelConditionFlags.Get(ModelConditionFlag.WeaponsetEnraged);

    private static void MakeEnemies(Player a, Player b)
    {
        a.AddEnemy(b);
        b.AddEnemy(a);
    }

    /// <summary>Spawns a Grunt owned by the enrager's own player and kills it in place: the
    /// FriendlyDeadFilter-matching "dead ally" half of the trigger.</summary>
    private static GameObject SpawnDeadAlly(HeadlessSimGame game, Player owner, Vector3 position)
    {
        var (ally, _) = PortedModuleTestKit.SpawnAndKill(game, "Grunt", owner, position);
        return ally;
    }

    [Fact]
    public void Create_NoTrigger_WhenNoHatedEnemyNearby()
    {
        var game = NewGame();
        var enrager = game.SpawnObject("Enrager", game.CivilianPlayer, Vector3.Zero);
        SpawnDeadAlly(game, game.CivilianPlayer, new Vector3(10, 0, 0));
        // No enemy anywhere in range.

        StepFrames(game, 8);

        Assert.False(EnragedFlag(enrager));
    }

    [Fact]
    public void Create_NoTrigger_WhenNoDeadAllyNearby()
    {
        var game = NewGame();
        var enrager = game.SpawnObject("Enrager", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);
        // No dead ally anywhere.

        StepFrames(game, 8);

        Assert.False(EnragedFlag(enrager));
    }

    [Fact]
    public void BothConditionsWithinRange_TriggersEnraged()
    {
        var game = NewGame();
        var enrager = game.SpawnObject("Enrager", game.CivilianPlayer, Vector3.Zero);
        SpawnDeadAlly(game, game.CivilianPlayer, new Vector3(10, 0, 0));
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(-10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 8);

        Assert.True(EnragedFlag(enrager));
    }

    [Fact]
    public void OutsideScanDistance_DoesNotTrigger()
    {
        var game = NewGame();
        var enrager = game.SpawnObject("NarrowScanEnrager", game.CivilianPlayer, Vector3.Zero);
        SpawnDeadAlly(game, game.CivilianPlayer, new Vector3(30, 0, 0));
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(-30, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 8);

        Assert.False(EnragedFlag(enrager));
    }

    [Fact]
    public void ZeroScanDistance_NeverTriggers_MatchesShippedAotRDefault()
    {
        var game = NewGame();
        var enrager = game.SpawnObject("NoScanEnrager", game.CivilianPlayer, Vector3.Zero);
        SpawnDeadAlly(game, game.CivilianPlayer, new Vector3(1, 0, 0));
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(-1, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 8);

        Assert.False(EnragedFlag(enrager));
    }

    [Fact]
    public void EnragedTime_Elapses_ClearsFlagAndStartsCooldown()
    {
        var game = NewGame();
        var enrager = game.SpawnObject("Enrager", game.CivilianPlayer, Vector3.Zero);
        SpawnDeadAlly(game, game.CivilianPlayer, new Vector3(10, 0, 0));
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(-10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 8);
        Assert.True(EnragedFlag(enrager));

        // Past EnragedTime (5 frames): the buff expires and the cooldown starts.
        StepFrames(game, 5);
        Assert.False(EnragedFlag(enrager));

        // Trigger conditions are still held (the dead ally stays dead, the enemy stays put).
        // Well past ScanCadence, but still inside the 10-frame cooldown: the gate holds.
        StepFrames(game, 8);
        Assert.False(EnragedFlag(enrager));
    }

    [Fact]
    public void CooldownElapses_AllowsRetrigger()
    {
        var game = NewGame();
        var enrager = game.SpawnObject("Enrager", game.CivilianPlayer, Vector3.Zero);
        SpawnDeadAlly(game, game.CivilianPlayer, new Vector3(10, 0, 0));
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(-10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 8);
        Assert.True(EnragedFlag(enrager));

        StepFrames(game, 5); // past EnragedTime -> Idle, cooldown starts (10 frames)
        Assert.False(EnragedFlag(enrager));

        // Step well past the 10-frame cooldown, conditions still held: the trigger is
        // edge-checked every scan, not one-shot-forever.
        StepFrames(game, 15);
        Assert.True(EnragedFlag(enrager));
    }

    [Fact]
    public void ZeroCooldown_AllowsImmediateRetrigger()
    {
        var game = NewGame();
        var enrager = game.SpawnObject("ZeroCooldownEnrager", game.CivilianPlayer, Vector3.Zero);
        SpawnDeadAlly(game, game.CivilianPlayer, new Vector3(10, 0, 0));
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(-10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 8);
        Assert.True(EnragedFlag(enrager));

        StepFrames(game, 5); // past EnragedTime -> Idle
        Assert.False(EnragedFlag(enrager));

        // TimeUntilCanRageAgain = 0: the very next scan with conditions still held re-triggers,
        // with no extra gap (entsinfantry.ini:1068's own inline comment: "always enrage if you
        // 'should'").
        StepFrames(game, 5);
        Assert.True(EnragedFlag(enrager));
    }

    [Fact]
    public void EnragedOnBuffFX_FiresOnceOnTrigger_NotEverySubsequentScan()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);
        var enrager = game.SpawnObject("FXEnrager", game.CivilianPlayer, Vector3.Zero);
        SpawnDeadAlly(game, game.CivilianPlayer, new Vector3(10, 0, 0));
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(-10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 8);
        Assert.True(EnragedFlag(enrager));
        Assert.Single(recorder.ParticleSystems, p => p.ParticleSystemName == "FX_EnrageOn");

        // Still inside EnragedTime, conditions still held, more scans happen: idempotent while
        // already Enraged (mirrors AttributeModifierAuraUpdate/OneRingPenaltyUpdate).
        StepFrames(game, 3);
        Assert.Single(recorder.ParticleSystems, p => p.ParticleSystemName == "FX_EnrageOn");
    }

    [Fact]
    public void EnragedOffBuffFX_FiresOnceOnExpiry()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);
        var enrager = game.SpawnObject("FXEnrager", game.CivilianPlayer, Vector3.Zero);
        SpawnDeadAlly(game, game.CivilianPlayer, new Vector3(10, 0, 0));
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(-10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 8);
        Assert.True(EnragedFlag(enrager));

        StepFrames(game, 5); // past EnragedTime
        Assert.False(EnragedFlag(enrager));

        Assert.Single(recorder.ParticleSystems, p => p.ParticleSystemName == "FX_EnrageOn");
        Assert.Single(recorder.ParticleSystems, p => p.ParticleSystemName == "FX_EnrageOff");
        Assert.Equal(2, recorder.ParticleSystems.Count);
    }

    [Fact]
    public void UnsetFXFields_NoCrashNoEventFired()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);
        var enrager = game.SpawnObject("Enrager", game.CivilianPlayer, Vector3.Zero);
        SpawnDeadAlly(game, game.CivilianPlayer, new Vector3(10, 0, 0));
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(-10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 8);
        Assert.True(EnragedFlag(enrager));

        StepFrames(game, 5); // full trigger/expiry cycle
        Assert.False(EnragedFlag(enrager));

        Assert.Empty(recorder.ParticleSystems);
    }

    [Fact]
    public void Xfer_SaveLoadRoundTrip_MidEnragedPhase()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("Enrager", game.CivilianPlayer, Vector3.Zero);
        SpawnDeadAlly(game, game.CivilianPlayer, new Vector3(10, 0, 0));
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(-10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 8);
        var live = ModuleOf(liveHost);
        Assert.True(EnragedFlag(liveHost)); // mid-EnragedTime

        var shadowHost = game.SpawnObject("Enrager", game.CivilianPlayer, new Vector3(400, 400, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidCooldownPhase()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("Enrager", game.CivilianPlayer, Vector3.Zero);
        SpawnDeadAlly(game, game.CivilianPlayer, new Vector3(10, 0, 0));
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(-10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 8);
        Assert.True(EnragedFlag(liveHost));

        StepFrames(game, 5); // past EnragedTime -> Idle, cooldown active (non-Enraged phase)
        Assert.False(EnragedFlag(liveHost));

        var live = ModuleOf(liveHost);
        var shadowHost = game.SpawnObject("Enrager", game.CivilianPlayer, new Vector3(400, 400, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
