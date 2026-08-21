// Mocked-game contract tests for the RousingSpeechUpdate port (R13,
// research/modules-r13/specs/RousingSpeechUpdateModuleData.md): the SpecialPowerTemplate +
// StartAbilityRange trigger gate, the timed BonusRadius/ObjectFilter/RequiredConditions scan
// (grounded on AttributeModifierAuraUpdate, spec §0.1), LeaderFX/FollowerFX event firing, the
// Reading-A RequiredConditions posture (gates candidates, not the speech-giver), and the
// shared-base Xfer save/load round trip.
//
// Sleepy-update caveat (spec §3): a freshly spawned object's module does not receive its first
// real Update() tick on the frame it is constructed (idle, SetWakeFrame(Forever) until
// triggered). After a successful InitiateIntentToDoSpecialPower call, the module's first real
// Update() tick lands one logic-frame-span later - every test that triggers the speech and then
// wants to observe a scan/FX/grant effect calls game.Step() at least once before asserting.
//
// Frame arithmetic (live Theoden data, retyped per spec §1): UpdateInterval = 100ms = 1 logic
// frame at 5 Hz; SpeechDuration = 2500ms = 13 frames (2500 / 200 = 12.5, ceil -> 13).

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class RousingSpeechUpdateContractTests
{
    private const string Definitions = @"
ModifierList RohanCharge
  Category = LEADERSHIP
  Modifier = ARMOR 25%
End

Object Theoden
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RousingSpeechUpdate ModuleTag_Speech
    SpecialPowerTemplate = SpecialAbilityRousingSpeech
    StartAbilityRange = 8.0
    UpdateInterval = 100
    ApproachRequiresLOS = No
    ModifierName = RohanCharge
    ObjectFilter = ANY +CAVALRY -STRUCTURE
    BonusRadius = 250
    SpeechDuration = 2500
    LeaderFX = FX_TheodenSpeechFX
    FollowerFX = FX_TheodenFollowerFX
    CreateWave = Yes
    WaveWidth = 50
  End
End

Object ZeroRangeTheoden
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RousingSpeechUpdate ModuleTag_Speech
    SpecialPowerTemplate = SpecialAbilityRousingSpeech
    StartAbilityRange = 0.0
    UpdateInterval = 100
    ModifierName = RohanCharge
    ObjectFilter = ANY +CAVALRY -STRUCTURE
    BonusRadius = 250
    SpeechDuration = 2500
  End
End

Object MountedGatedTheoden
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RousingSpeechUpdate ModuleTag_Speech
    SpecialPowerTemplate = SpecialAbilityRousingSpeech
    StartAbilityRange = 8.0
    UpdateInterval = 100
    RequiredConditions = MOUNTED
    ModifierName = RohanCharge
    ObjectFilter = ANY +CAVALRY -STRUCTURE
    BonusRadius = 250
    SpeechDuration = 2500
  End
End

Object CavalryAlly
  KindOf = INFANTRY CAVALRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object InfantryAlly
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object Rider
  KindOf = INFANTRY
  IsTrainable = Yes
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x707E00E5)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static void Step(HeadlessSimGame game, int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            game.Step();
        }
    }

    private static RousingSpeechUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<RousingSpeechUpdate>().Single();

    private static void MakeEnemies(Player a, Player b)
    {
        a.AddEnemy(b);
        b.AddEnemy(a);
    }

    [Fact]
    public void InitiateIntentToDoSpecialPower_WrongTemplateName_NoOp()
    {
        var game = NewGame();
        var leader = game.SpawnObject("Theoden", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(leader);

        // Confirms Update() never ran before any trigger call (sleepy-update caveat).
        Step(game);

        Assert.False(module.InitiateIntentToDoSpecialPower("WrongName", leader));
        Assert.False(module.IsActive);
        Assert.Empty(module.GrantedTargets);

        // A subsequent correctly-named call still succeeds: no state corruption from the
        // rejected attempt.
        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialAbilityRousingSpeech", leader));
    }

    [Fact]
    public void InitiateIntentToDoSpecialPower_OutOfStartAbilityRange_NoOp()
    {
        var game = NewGame();
        var leader = game.SpawnObject("Theoden", game.CivilianPlayer, Vector3.Zero);
        // 20 units away, StartAbilityRange = 8.0: out of range.
        var farTrigger = game.SpawnObject("Rider", game.CivilianPlayer, new Vector3(20, 0, 0));
        var module = ModuleOf(leader);

        Assert.False(module.InitiateIntentToDoSpecialPower("SpecialAbilityRousingSpeech", farTrigger));
        Assert.False(module.IsActive);

        var nearTrigger = game.SpawnObject("Rider", game.CivilianPlayer, new Vector3(5, 0, 0));
        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialAbilityRousingSpeech", nearTrigger));
    }

    [Fact]
    public void ZeroStartAbilityRange_SkipsRangeGate()
    {
        var game = NewGame();
        var leader = game.SpawnObject("ZeroRangeTheoden", game.CivilianPlayer, Vector3.Zero);
        // Far outside any plausible range: StartAbilityRange = 0 means the gate is skipped.
        var farTrigger = game.SpawnObject("Rider", game.CivilianPlayer, new Vector3(10000, 0, 0));
        var module = ModuleOf(leader);

        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialAbilityRousingSpeech", farTrigger));
    }

    [Fact]
    public void FullCycle_GrantsModifierToEligibleCavalryWithinRadius_RevokesAtSpeechEnd()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);

        var leader = game.SpawnObject("Theoden", game.CivilianPlayer, Vector3.Zero);
        var trigger = game.SpawnObject("Rider", game.CivilianPlayer, new Vector3(5, 0, 0));
        var cavalryAlly = game.SpawnObject("CavalryAlly", game.CivilianPlayer, new Vector3(10, 0, 0));
        var infantryAlly = game.SpawnObject("InfantryAlly", game.CivilianPlayer, new Vector3(10, 0, 0));
        var enemyCavalry = game.SpawnObject("CavalryAlly", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var module = ModuleOf(leader);
        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialAbilityRousingSpeech", trigger));

        // LeaderFX fires immediately at the trigger call, before any scan.
        Assert.Contains(recorder.Events, e => e.FXListName == "FX_TheodenSpeechFX" && e.ObjectId == leader.Id);

        Step(game); // First real Update() tick: first scan.

        Assert.True(cavalryAlly.HasAttributeModifier("RohanCharge"));
        Assert.False(infantryAlly.HasAttributeModifier("RohanCharge"));
        Assert.False(enemyCavalry.HasAttributeModifier("RohanCharge"));

        Assert.Single(recorder.Events, e => e.FXListName == "FX_TheodenFollowerFX" && e.ObjectId == cavalryAlly.Id);

        // A few more frames (module re-scans every frame per UpdateInterval == 1): no
        // additional FollowerFX for the already-granted target.
        Step(game, 3);
        Assert.Single(recorder.Events, e => e.FXListName == "FX_TheodenFollowerFX" && e.ObjectId == cavalryAlly.Id);
        Assert.True(cavalryAlly.HasAttributeModifier("RohanCharge"));

        // Frame 13 total since trigger: SpeechDuration elapses, every granted target is revoked.
        Step(game, 9);
        Assert.False(cavalryAlly.HasAttributeModifier("RohanCharge"));
        Assert.False(module.IsActive);

        // Module is idle again: a fresh trigger succeeds (full cycle closed).
        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialAbilityRousingSpeech", trigger));
    }

    [Fact]
    public void CandidateLeavesRadiusMidSpeech_RevokedBeforeSpeechEnds()
    {
        var game = NewGame();
        var leader = game.SpawnObject("Theoden", game.CivilianPlayer, Vector3.Zero);
        var trigger = game.SpawnObject("Rider", game.CivilianPlayer, new Vector3(5, 0, 0));
        var cavalryAlly = game.SpawnObject("CavalryAlly", game.CivilianPlayer, new Vector3(10, 0, 0));

        var module = ModuleOf(leader);
        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialAbilityRousingSpeech", trigger));

        Step(game); // First scan: granted.
        Assert.True(cavalryAlly.HasAttributeModifier("RohanCharge"));

        cavalryAlly.UpdateTransform(new Vector3(1000, 0, 0));
        cavalryAlly.UpdateColliders();

        Step(game); // Next scan: revoked mid-speech, well before SpeechDuration (13 frames) elapses.
        Assert.False(cavalryAlly.HasAttributeModifier("RohanCharge"));
    }

    [Fact]
    public void RequiredConditions_GatesCandidatesNotSource_ReadingA()
    {
        var game = NewGame();
        var leader = game.SpawnObject("MountedGatedTheoden", game.CivilianPlayer, Vector3.Zero);
        // The speech-giver itself does NOT have MOUNTED set.
        Assert.False(leader.ModelConditionFlags.Get(ModelConditionFlag.Mounted));

        var trigger = game.SpawnObject("Rider", game.CivilianPlayer, new Vector3(5, 0, 0));
        var mountedAlly = game.SpawnObject("CavalryAlly", game.CivilianPlayer, new Vector3(10, 0, 0));
        mountedAlly.SetModelConditionState(ModelConditionFlag.Mounted);
        var unmountedAlly = game.SpawnObject("CavalryAlly", game.CivilianPlayer, new Vector3(10, 0, 0));

        var module = ModuleOf(leader);

        // The source's own MOUNTED state does not gate the trigger (Reading A, not Reading B).
        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialAbilityRousingSpeech", trigger));

        Step(game);

        Assert.True(mountedAlly.HasAttributeModifier("RohanCharge"));
        Assert.False(unmountedAlly.HasAttributeModifier("RohanCharge"));
    }

    [Fact]
    public void ApproachRequiresLOS_CreateWave_WaveWidth_ParseOnly()
    {
        var game = NewGame();
        var leader = game.SpawnObject("Theoden", game.CivilianPlayer, Vector3.Zero);
        var trigger = game.SpawnObject("Rider", game.CivilianPlayer, new Vector3(5, 0, 0));
        var cavalryAlly = game.SpawnObject("CavalryAlly", game.CivilianPlayer, new Vector3(10, 0, 0));

        // Module-level assertion (exposed read-only, same posture as
        // ToggleHiddenSpecialAbilityUpdate.ShowsPalantirTimer): the three fields read back
        // exactly as parsed.
        var module = ModuleOf(leader);
        Assert.False(module.ApproachRequiresLos);
        Assert.True(module.CreateWave);
        Assert.Equal(50, module.WaveWidth);

        // The grant/revoke/FX behavior is byte-for-byte unaffected by these three fields:
        // nothing accidentally gates or blocks on them (same full-cycle shape as the case above).
        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialAbilityRousingSpeech", trigger));
        Step(game);
        Assert.True(cavalryAlly.HasAttributeModifier("RohanCharge"));
        Step(game, 12);
        Assert.False(cavalryAlly.HasAttributeModifier("RohanCharge"));
    }

    [Fact]
    public void Xfer_SaveLoadRoundTrip_MidSpeech_PreservesActiveStateAndGrants()
    {
        var game = NewGame();
        var leader = game.SpawnObject("Theoden", game.CivilianPlayer, Vector3.Zero);
        var trigger = game.SpawnObject("Rider", game.CivilianPlayer, new Vector3(5, 0, 0));
        var cavalryAlly = game.SpawnObject("CavalryAlly", game.CivilianPlayer, new Vector3(10, 0, 0));

        var live = ModuleOf(leader);
        Assert.True(live.InitiateIntentToDoSpecialPower("SpecialAbilityRousingSpeech", trigger));

        // Past the first scan, cavalryAlly granted, mid-speech (9 of 13 SpeechDuration frames remain).
        Step(game, 4);
        Assert.True(cavalryAlly.HasAttributeModifier("RohanCharge"));

        var state = PortedModuleTestKit.Save(live);
        // NextWakeFrameForWalk is engine-owned, walk-carried scheduling state (UpdateModule.cs's
        // own doc comment), not part of the module's own Xfer() - it is normally carried by the
        // per-object save walk, not this test kit's module-only Xfer. Same workaround as
        // InvisibilityUpdateContractTests' MidBehavior_SaveLoadRoundTrip: capture and restore it
        // by hand around the module-only Load.
        var wake = live.NextWakeFrameForWalk;

        // Placed at the same spot as the original leader (not far away, unlike the
        // ShadowCopy test below) so the loaded instance's own subsequent scans still see
        // cavalryAlly within BonusRadius - this test exercises continued ticking after Load,
        // not just a CRC comparison, so a stray out-of-range host would revoke the target for
        // the wrong reason (falling out of the loaded instance's own radius) instead of the
        // one this test is actually checking (SpeechDuration elapsing).
        var loadHost = game.SpawnObject("Theoden", game.CivilianPlayer, Vector3.Zero);
        var loaded = ModuleOf(loadHost);
        PortedModuleTestKit.Load(loaded, state);
        loaded.NextWakeFrameForWalk = wake;

        // The loaded instance drives the SAME granted GameObject (RohanCharge on cavalryAlly),
        // continuing the original speech's remaining lifetime.
        Assert.True(loaded.IsActive);
        Assert.Contains(cavalryAlly.Id, loaded.GrantedTargets);

        Step(game, 9); // Completes the remaining 9 frames on the loaded instance's own schedule.

        Assert.False(cavalryAlly.HasAttributeModifier("RohanCharge"));
        Assert.False(loaded.IsActive);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var leader = game.SpawnObject("Theoden", game.CivilianPlayer, Vector3.Zero);
        var trigger = game.SpawnObject("Rider", game.CivilianPlayer, new Vector3(5, 0, 0));
        game.SpawnObject("CavalryAlly", game.CivilianPlayer, new Vector3(10, 0, 0));

        var live = ModuleOf(leader);
        Assert.True(live.InitiateIntentToDoSpecialPower("SpecialAbilityRousingSpeech", trigger));
        Step(game, 4);

        var shadowHost = game.SpawnObject("Theoden", game.CivilianPlayer, new Vector3(400, 400, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
