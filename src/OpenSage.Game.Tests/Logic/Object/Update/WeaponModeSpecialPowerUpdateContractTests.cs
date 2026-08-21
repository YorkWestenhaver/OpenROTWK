// Mocked-game contract tests for the WeaponModeSpecialPowerUpdate port (R13): the
// special-power-gated, timed, reversible weapon-set switch described in the port spec
// (bfme2-workbench/research/modules-r13/specs/WeaponModeSpecialPowerUpdateModuleData.md).
//
// Sleepy-update caveat (spec §3): this module is NOT an UpdateModule (F-WMSP-4) - its
// Duration revert has no automatic per-frame caller, so tests call CheckRevert() explicitly
// after game.Step() rather than relying on the sleepy UpdateModule queue's timing. Case 5b
// exists specifically to demonstrate that omitting the CheckRevert() call leaves the
// activated effects in place, as a testable fact rather than prose.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class WeaponModeSpecialPowerUpdateContractTests
{
    // ReloadTime=200 (1 frame at 5 Hz) - reload never gates the case-1..6/8..10 scenarios,
    // which never call Activate() twice. TestPowerSlowCooldown's 10000 (50 frames) is what
    // case 7 needs to observe a still-on-cooldown rejection.
    private const string Definitions = @"
SpecialPower TestPower
  Enum = SPECIAL_CASH_HACK
  ReloadTime = 200
End

SpecialPower TestPowerSlowCooldown
  Enum = SPECIAL_CASH_HACK
  ReloadTime = 10000
End

ModifierList TestBuff
  Category = LEADERSHIP
  Modifier = ARMOR 25%
End

Object Switcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = WeaponModeSpecialPowerUpdate ModuleTag_WMSP
    SpecialPowerTemplate = TestPower
    WeaponSetFlags = WEAPONSET_HERO_MODE
    AttributeModifier = TestBuff
    Duration = 1000
    InitiateSound = Sound_WeaponModeSwitch
  End
End

Object PausedSwitcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = WeaponModeSpecialPowerUpdate ModuleTag_WMSP
    SpecialPowerTemplate = TestPower
    WeaponSetFlags = WEAPONSET_HERO_MODE
    StartsPaused = Yes
  End
End

Object SlowCooldownSwitcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = WeaponModeSpecialPowerUpdate ModuleTag_WMSP
    SpecialPowerTemplate = TestPowerSlowCooldown
    AttributeModifier = TestBuff
    Duration = 1000
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x5145) // ""WMSP"-ish
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static WeaponModeSpecialPowerUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<WeaponModeSpecialPowerUpdate>().Single();

    private static bool HeroMode(GameObject obj) =>
        obj.WeaponSetConditions.Get(WeaponSetConditions.WeaponsetHeroMode);

    [Fact]
    public void Create_NotPaused_IsReadyImmediately()
    {
        var game = NewGame();
        var obj = game.SpawnObject("Switcher", game.CivilianPlayer, Vector3.Zero);
        game.Step();

        Assert.True(ModuleOf(obj).Activate());
    }

    [Fact]
    public void Create_StartsPaused_ActivateFails_UntilUnpause()
    {
        var game = NewGame();
        var obj = game.SpawnObject("PausedSwitcher", game.CivilianPlayer, Vector3.Zero);
        game.Step();

        var module = ModuleOf(obj);
        Assert.False(module.Activate());
        Assert.False(HeroMode(obj));

        module.Unpause();
        Assert.True(module.Activate());
    }

    [Fact]
    public void Activate_SetsWeaponSetFlagsAndGrantsAttributeModifier()
    {
        var game = NewGame();
        var obj = game.SpawnObject("Switcher", game.CivilianPlayer, Vector3.Zero);
        game.Step();

        Assert.True(ModuleOf(obj).Activate());

        Assert.True(HeroMode(obj));
        Assert.True(obj.HasAttributeModifier("TestBuff"));
    }

    [Fact]
    public void Activate_FiresInitiateSound()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);
        var obj = game.SpawnObject("Switcher", game.CivilianPlayer, Vector3.Zero);
        game.Step();

        ModuleOf(obj).Activate();

        Assert.Single(recorder.AudioEvents);
        Assert.Contains(("Sound_WeaponModeSwitch", obj.Id), recorder.AudioEvents);
    }

    [Fact]
    public void Duration_Elapses_CheckRevert_ClearsFlagsAndRemovesModifier()
    {
        var game = NewGame();
        var obj = game.SpawnObject("Switcher", game.CivilianPlayer, Vector3.Zero);
        game.Step();

        var module = ModuleOf(obj);
        Assert.True(module.Activate());

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        module.CheckRevert();

        Assert.False(HeroMode(obj));
        Assert.False(obj.HasAttributeModifier("TestBuff"));
    }

    [Fact]
    public void Duration_Elapses_WithoutCheckRevertCall_EffectsStillActive()
    {
        var game = NewGame();
        var obj = game.SpawnObject("Switcher", game.CivilianPlayer, Vector3.Zero);
        game.Step();

        var module = ModuleOf(obj);
        Assert.True(module.Activate());

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        // F-WMSP-4: no automatic caller exists - CheckRevert() was never called, so the
        // effects must still be applied even well past Duration.
        Assert.True(HeroMode(obj));
        Assert.True(obj.HasAttributeModifier("TestBuff"));
    }

    [Fact]
    public void CheckRevert_BeforeDurationElapses_IsNoOp()
    {
        var game = NewGame();
        var obj = game.SpawnObject("Switcher", game.CivilianPlayer, Vector3.Zero);
        game.Step();

        var module = ModuleOf(obj);
        Assert.True(module.Activate());

        for (var i = 0; i < 2; i++)
        {
            game.Step();
        }

        module.CheckRevert();

        Assert.True(HeroMode(obj));
    }

    [Fact]
    public void Activate_WhileOnCooldown_IsRejected_NoDoubleGrant()
    {
        var game = NewGame();
        var obj = game.SpawnObject("SlowCooldownSwitcher", game.CivilianPlayer, Vector3.Zero);
        game.Step();

        var module = ModuleOf(obj);
        Assert.True(module.Activate());
        Assert.False(module.Activate());

        Assert.True(obj.HasAttributeModifier("TestBuff"));
    }

    [Fact]
    public void Revert_OnlyClearsFlagsThisModuleSet_NotFlagsSetByOtherModules()
    {
        var game = NewGame();
        var obj = game.SpawnObject("Switcher", game.CivilianPlayer, Vector3.Zero);
        game.Step();

        // Simulates an unrelated module independently raising a different WeaponSetConditions
        // bit on the same object.
        obj.SetWeaponSetCondition(WeaponSetConditions.Veteran, true);

        var module = ModuleOf(obj);
        Assert.True(module.Activate());

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        module.CheckRevert();

        Assert.False(HeroMode(obj));
        Assert.True(obj.WeaponSetConditions.Get(WeaponSetConditions.Veteran));
    }

    [Fact]
    public void Xfer_SaveLoadRoundTrip_MidActivePhase()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("Switcher", game.CivilianPlayer, Vector3.Zero);
        game.Step();
        var live = ModuleOf(liveHost);
        live.Activate();

        for (var i = 0; i < 2; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("Switcher", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_ReadyPhase()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("Switcher", game.CivilianPlayer, Vector3.Zero);
        game.Step();
        var live = ModuleOf(liveHost);

        var shadowHost = game.SpawnObject("Switcher", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
