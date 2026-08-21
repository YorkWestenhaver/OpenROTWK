// Mocked-game contract tests for the MonitorConditionUpdate port (R13): baseline capture,
// ModelConditionFlags/WeaponSetFlags toggle-on/off (restore), independent-inert-pair
// non-interference, and the shadow-copy base test. See
// bfme2-workbench/research/modules-r13/specs/MonitorConditionUpdateModuleData.md for the
// full behavioral spec and corpus citations (harondorraiderhorde.ini, angmarthrallmaster.ini,
// siegemumak.ini, cavetroll.ini).
//
// Sleepy-update caveat (spec §3, honored in every test below): SetWakeFrame(UpdateSleepTime
// .None) in the ctor delays the module's first real Update() body by one logic frame, so a
// freshly spawned module's first observable effect - including the lazy baseline capture -
// appears only after the SECOND game.Step() call, not the first (mirrors
// HeightDieUpdateContractTests's OnlyWhenMovingDown_FallingBelowTarget_Dies pattern).

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class MonitorConditionUpdateContractTests
{
    private const string Definitions = @"
CommandSet BaseCommandSet
End

CommandSet AltModelCommandSet
End

CommandSet AltWeaponCommandSet
End

Object ModelConditionOnly
  KindOf = INFANTRY
  CommandSet = BaseCommandSet
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = MonitorConditionUpdate ModuleTag_Monitor
    ModelConditionFlags = USER_1
    ModelConditionCommandSet = AltModelCommandSet
  End
End

Object WeaponSetOnly
  KindOf = INFANTRY
  CommandSet = BaseCommandSet
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = MonitorConditionUpdate ModuleTag_Monitor
    WeaponSetFlags = WEAPONSET_TOGGLE_1
    WeaponToggleCommandSet = AltWeaponCommandSet
  End
End

Object BothPairs
  KindOf = INFANTRY
  CommandSet = BaseCommandSet
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = MonitorConditionUpdate ModuleTag_Monitor
    ModelConditionFlags = USER_1
    ModelConditionCommandSet = AltModelCommandSet
    WeaponSetFlags = WEAPONSET_TOGGLE_1
    WeaponToggleCommandSet = AltWeaponCommandSet
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x0C57)
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

    private static MonitorConditionUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<MonitorConditionUpdate>().Single();

    private static string CommandSetName(GameObject obj) => obj.Definition.CommandSet.Value.Name;

    [Fact]
    public void BaselineCapture_NoConditionSet_CommandSetUnchanged()
    {
        var game = NewGame();
        var obj = game.SpawnObject("ModelConditionOnly", game.CivilianPlayer, new Vector3(0, 0, 0));

        // First Step() is the sleepy-update no-op frame; the module's first real body
        // execution - including the lazy baseline capture - happens on the second.
        StepFrames(game, 2);

        Assert.Equal("BaseCommandSet", CommandSetName(obj));
    }

    [Fact]
    public void ModelConditionFlags_ToggleOn_ForcesAltCommandSet()
    {
        var game = NewGame();
        var obj = game.SpawnObject("ModelConditionOnly", game.CivilianPlayer, new Vector3(0, 0, 0));

        StepFrames(game, 2); // baseline capture, still original CommandSet
        Assert.Equal("BaseCommandSet", CommandSetName(obj));

        obj.SetModelConditionState(ModelConditionFlag.User1);
        game.Step();

        Assert.Equal("AltModelCommandSet", CommandSetName(obj));
    }

    [Fact]
    public void ModelConditionFlags_ToggleOff_RestoresBaseline()
    {
        var game = NewGame();
        var obj = game.SpawnObject("ModelConditionOnly", game.CivilianPlayer, new Vector3(0, 0, 0));

        StepFrames(game, 2);
        obj.SetModelConditionState(ModelConditionFlag.User1);
        game.Step();
        Assert.Equal("AltModelCommandSet", CommandSetName(obj));

        obj.ClearModelConditionState(ModelConditionFlag.User1);
        game.Step();

        // The single highest-value assertion in this packet (spec §3 case 3): proves
        // restore-to-baseline, not stuck-on-last-toggle.
        Assert.Equal("BaseCommandSet", CommandSetName(obj));
    }

    [Fact]
    public void WeaponSetFlags_ToggleOnThenOff_ForcesThenRestores()
    {
        var game = NewGame();
        var obj = game.SpawnObject("WeaponSetOnly", game.CivilianPlayer, new Vector3(0, 0, 0));

        StepFrames(game, 2);
        Assert.Equal("BaseCommandSet", CommandSetName(obj));

        obj.SetWeaponSetCondition(WeaponSetConditions.WeaponsetToggle1, true);
        game.Step();
        Assert.Equal("AltWeaponCommandSet", CommandSetName(obj));

        obj.SetWeaponSetCondition(WeaponSetConditions.WeaponsetToggle1, false);
        game.Step();
        Assert.Equal("BaseCommandSet", CommandSetName(obj));
    }

    [Fact]
    public void BothPairsAuthored_OnlyModelConditionTrue_ModelConditionWins_WeaponPairInert()
    {
        var game = NewGame();
        var obj = game.SpawnObject("BothPairs", game.CivilianPlayer, new Vector3(0, 0, 0));

        StepFrames(game, 2);
        obj.SetModelConditionState(ModelConditionFlag.User1);
        game.Step();

        Assert.Equal("AltModelCommandSet", CommandSetName(obj));
    }

    [Fact]
    public void BothPairsAuthored_OnlyWeaponSetTrue_WeaponToggleWins_ModelConditionPairInert()
    {
        var game = NewGame();
        var obj = game.SpawnObject("BothPairs", game.CivilianPlayer, new Vector3(0, 0, 0));

        StepFrames(game, 2);
        obj.SetWeaponSetCondition(WeaponSetConditions.WeaponsetToggle1, true);
        game.Step();

        Assert.Equal("AltWeaponCommandSet", CommandSetName(obj));
    }

    [Fact]
    public void UnauthoredWeaponSetPair_StaysInert_RegardlessOfWeaponSetState()
    {
        var game = NewGame();
        // ModelConditionOnly authors no WeaponSetFlags/WeaponToggleCommandSet pair at all
        // (angmarthrallmaster.ini / siegemumak.ini shape): the module must not evaluate an
        // unauthored pair. A default-constructed (null) BitArray never intersects, so this
        // holds by construction, but is worth a regression test since an
        // empty-set-always-intersects bug would be silent and catastrophic.
        var obj = game.SpawnObject("ModelConditionOnly", game.CivilianPlayer, new Vector3(0, 0, 0));

        StepFrames(game, 2);
        obj.SetWeaponSetCondition(WeaponSetConditions.WeaponsetToggle1, true);
        game.Step();

        Assert.Equal("BaseCommandSet", CommandSetName(obj));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidToggle()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("ModelConditionOnly", game.CivilianPlayer, new Vector3(0, 0, 0));

        StepFrames(game, 2);
        liveHost.SetModelConditionState(ModelConditionFlag.User1);
        game.Step();
        Assert.Equal("AltModelCommandSet", CommandSetName(liveHost));

        var live = ModuleOf(liveHost);
        var shadowHost = game.SpawnObject("ModelConditionOnly", game.CivilianPlayer, new Vector3(400, 400, 0));
        var shadow = ModuleOf(shadowHost);

        // Only XferVersion is walked (see the module's file header, F-MCU-2): no frozen
        // IXfer primitive can carry the _baselineCommandSet asset-reference identity, so the
        // walk deliberately excludes it rather than corrupt it. The CRC equality here is
        // therefore a real but narrower guarantee than the spec's original phrasing ("confirms
        // _baselineCommandSet/_baselineCaptured round-trip") - it confirms the walk is
        // internally consistent (Save/Load/Crc agree byte-for-byte), not that the baseline
        // reference itself survives a real save/load. See file header for the full resolution.
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
