// Mocked-game unit tests for the StealthDetectorUpdate port (api-freeze-v1 §6 fitness
// item 4): one test per INI-configurable behavior branch, [create -> tick -> observable
// effect], plus the shadow-copy base test and a mid-state save/load round-trip. Object
// definitions are parsed from INI text through the real parser, so the S5 quantizing parse
// of DetectionRange (Fix64) is on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class StealthDetectorUpdateContractTests
{
    // 5 Hz (F6): DetectionRate 200 ms -> 1 frame.
    private const string Definitions = @"
Object Detector
  KindOf = STRUCTURE
  VisionRange = 100
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = StealthDetectorUpdate ModuleTag_Detect
    DetectionRate = 200
    DetectionRange = 50
  End
End

Object VisionDetector
  KindOf = STRUCTURE
  VisionRange = 60
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = StealthDetectorUpdate ModuleTag_Detect
    DetectionRate = 200
    DetectionRange = 0
  End
End

Object DisabledDetector
  KindOf = STRUCTURE
  VisionRange = 100
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = StealthDetectorUpdate ModuleTag_Detect
    DetectionRate = 200
    DetectionRange = 50
    InitiallyDisabled = Yes
  End
End

Object Sneaker
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x57EA)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static StealthDetectorUpdate DetectorModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<StealthDetectorUpdate>().Single();

    private static GameObject SpawnStealthed(HeadlessSimGame game, Player owner, in Vector3 pos)
    {
        var obj = game.SpawnObject("Sneaker", owner, pos);
        obj.SetObjectStatus(ObjectStatus.Stealthed, true);
        return obj;
    }

    [Fact]
    public void RevealsStealthedEnemyInRange()
    {
        var game = NewGame();
        game.SpawnObject("Detector", game.CivilianPlayer, Vector3.Zero);
        var enemy = SpawnStealthed(game, game.PlayerManager.Players[0], new Vector3(20, 0, 0));

        Assert.False(enemy.TestStatus(ObjectStatus.Detected));

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        Assert.True(enemy.TestStatus(ObjectStatus.Detected));   // revealed
    }

    [Fact]
    public void DoesNotRevealNonStealthedObject()
    {
        var game = NewGame();
        game.SpawnObject("Detector", game.CivilianPlayer, Vector3.Zero);
        var plain = game.SpawnObject("Sneaker", game.PlayerManager.Players[0], new Vector3(20, 0, 0));

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        Assert.False(plain.TestStatus(ObjectStatus.Detected));  // nothing to detect
    }

    [Fact]
    public void DoesNotRevealStealthedAlly()
    {
        var game = NewGame();
        game.SpawnObject("Detector", game.CivilianPlayer, Vector3.Zero);
        var ally = SpawnStealthed(game, game.CivilianPlayer, new Vector3(20, 0, 0));

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        Assert.False(ally.TestStatus(ObjectStatus.Detected));   // allies are never revealed
    }

    [Fact]
    public void DoesNotRevealStealthedEnemyOutOfRange()
    {
        var game = NewGame();
        game.SpawnObject("Detector", game.CivilianPlayer, Vector3.Zero);
        var far = SpawnStealthed(game, game.PlayerManager.Players[0], new Vector3(500, 0, 0));

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        Assert.False(far.TestStatus(ObjectStatus.Detected));    // beyond DetectionRange 50
    }

    [Fact]
    public void VisionRangeFallback_WhenDetectionRangeIsZero()
    {
        var game = NewGame();
        game.SpawnObject("VisionDetector", game.CivilianPlayer, Vector3.Zero);
        var inVision = SpawnStealthed(game, game.PlayerManager.Players[0], new Vector3(40, 0, 0));
        var beyondVision = SpawnStealthed(game, game.PlayerManager.Players[0], new Vector3(80, 0, 0));

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        Assert.True(inVision.TestStatus(ObjectStatus.Detected));     // within VisionRange 60
        Assert.False(beyondVision.TestStatus(ObjectStatus.Detected)); // outside it
    }

    [Fact]
    public void InitiallyDisabled_DetectsNothing()
    {
        var game = NewGame();
        game.SpawnObject("DisabledDetector", game.CivilianPlayer, Vector3.Zero);
        var enemy = SpawnStealthed(game, game.PlayerManager.Players[0], new Vector3(20, 0, 0));

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        Assert.False(enemy.TestStatus(ObjectStatus.Detected));  // disabled => never scans
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var detector = game.SpawnObject("Detector", game.CivilianPlayer, Vector3.Zero);
        SpawnStealthed(game, game.PlayerManager.Players[0], new Vector3(20, 0, 0));
        var live = DetectorModuleOf(detector);

        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }

        // The shadow is the same class over the same data on a second object in a different
        // (disabled) state; Load must overwrite everything the walk carries.
        var shadowHost = game.SpawnObject("DisabledDetector", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = DetectorModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_PreservesEnabledFlag()
    {
        var game = NewGame();
        var detector = game.SpawnObject("Detector", game.CivilianPlayer, Vector3.Zero);
        var module = DetectorModuleOf(detector);

        // Drive the enabled flag away from its constructed value, then round-trip it.
        module.Active = false;
        var state = PortedModuleTestKit.Save(module);

        var shadowHost = game.SpawnObject("Detector", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = DetectorModuleOf(shadowHost);
        Assert.True(shadow.Active);                             // constructed enabled

        PortedModuleTestKit.Load(shadow, state);
        Assert.False(shadow.Active);                            // load carried the flag over
    }
}
