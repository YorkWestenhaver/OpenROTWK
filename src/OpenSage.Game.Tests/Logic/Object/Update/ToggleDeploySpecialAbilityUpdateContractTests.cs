// Mocked-game unit tests for the ToggleDeploySpecialAbilityUpdate port (api-freeze-v1 §6
// fitness item 4): one test per behavior branch, [create -> Toggle() -> observable effect],
// covering the R13 spec's own contract-test plan (research/modules-r13/specs/
// ToggleDeploySpecialAbilityUpdateModuleData.md §3).
//
// Sleepy-update caveat: this module's own Update() is intentionally inert (no timer field
// exists to advance against) and the constructor sets UpdateSleepTime.Forever, so it never
// wakes on its own. Toggle() is a directly-invoked method, not something Update() produces:
// every test below calls Toggle() on the module instance directly and does not rely on any
// Step() call for a Toggle() call to take effect. Tests still Step() once after spawn where the
// spec calls for it, to confirm the module survives a frame without spuriously flipping.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class ToggleDeploySpecialAbilityUpdateContractTests
{
    private const string Definitions = @"
Object Deployer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ToggleDeploySpecialAbilityUpdate ModuleTag_Deploy
    SpecialPowerTemplate = TestDeployPower
    SoundDeploy = Sound_Deploy
    SoundUndeploy = Sound_Undeploy
  End
End

Object SilentDeployer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ToggleDeploySpecialAbilityUpdate ModuleTag_Deploy
    SpecialPowerTemplate = TestDeployPower
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x7071DE)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static ToggleDeploySpecialAbilityUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<ToggleDeploySpecialAbilityUpdate>().Single();

    [Fact]
    public void Toggle_WrongTemplateName_NoOp()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);
        var unit = game.SpawnObject("Deployer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(unit);

        game.Step();

        Assert.False(module.Toggle("SpecialPower_WrongName", null, true));
        Assert.False(unit.ModelConditionFlags.Get(ModelConditionFlag.Deployed));
        Assert.Empty(recorder.AudioEvents);
    }

    [Fact]
    public void Toggle_Deploy_SetsFlagAndFiresSoundDeploy()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);
        var unit = game.SpawnObject("Deployer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(unit);

        game.Step();

        Assert.True(module.Toggle("TestDeployPower", null, true));
        Assert.True(unit.ModelConditionFlags.Get(ModelConditionFlag.Deployed));
        Assert.Equal(new[] { ("Sound_Deploy", unit.Id) }, recorder.AudioEvents);
    }

    [Fact]
    public void Toggle_Undeploy_ClearsFlagAndFiresSoundUndeploy()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);
        var unit = game.SpawnObject("Deployer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(unit);

        game.Step();
        Assert.True(module.Toggle("TestDeployPower", null, true));

        Assert.True(module.Toggle("TestDeployPower", null, false));
        Assert.False(unit.ModelConditionFlags.Get(ModelConditionFlag.Deployed));
        Assert.Equal(2, recorder.AudioEvents.Count);
        Assert.Equal(("Sound_Undeploy", unit.Id), recorder.AudioEvents[1]);
    }

    [Fact]
    public void Toggle_RepeatSameState_IsNoOp()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);
        var unit = game.SpawnObject("Deployer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(unit);

        game.Step();
        Assert.True(module.Toggle("TestDeployPower", null, true));

        Assert.False(module.Toggle("TestDeployPower", null, true));
        Assert.True(unit.ModelConditionFlags.Get(ModelConditionFlag.Deployed));
        Assert.Single(recorder.AudioEvents);
    }

    [Fact]
    public void Toggle_EmptySoundField_NoEventFired()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);
        var unit = game.SpawnObject("SilentDeployer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(unit);

        game.Step();

        Assert.True(module.Toggle("TestDeployPower", null, true));
        Assert.True(unit.ModelConditionFlags.Get(ModelConditionFlag.Deployed));
        Assert.Empty(recorder.AudioEvents);
    }

    [Fact]
    public void Xfer_SaveLoadRoundTrip_PreservesDeployedState()
    {
        var game = NewGame();
        var unit = game.SpawnObject("Deployer", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(unit);

        game.Step();
        Assert.True(live.Toggle("TestDeployPower", null, true));

        var shadowHost = game.SpawnObject("Deployer", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);

        // Indirect proof the loaded state is Deployed, not Undeployed: a further Toggle(true)
        // on the shadow instance is rejected by the no-op-on-same-state branch.
        PortedModuleTestKit.Load(shadow, PortedModuleTestKit.Save(live));
        Assert.False(shadow.Toggle("TestDeployPower", null, true));
    }

    [Fact]
    public void Update_NeverWakesOnItsOwn_NoStateChange()
    {
        var game = NewGame();
        var unit = game.SpawnObject("Deployer", game.CivilianPlayer, Vector3.Zero);

        for (var i = 0; i < 5; i++)
        {
            game.Step();
            Assert.False(unit.ModelConditionFlags.Get(ModelConditionFlag.Deployed));
        }
    }
}
