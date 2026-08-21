// Mocked-game unit tests for the HeightDieUpdate port (api-freeze-v1 §6 fitness item 4): one
// test per behavior branch from the R12 task packet, [create -> tick -> observable effect],
// plus the mid-behavior save/load round-trip and the shadow-copy base test - the same shape
// as CheckpointUpdateContractTests, its direct analog in this port round.
//
// HeightDieUpdate does not move objects itself (that's the physics/locomotion systems'
// job); these tests drive Z by calling UpdateTransform directly between Steps, the same way
// CheckpointUpdateContractTests drives ally position to make its scan-and-react branches
// observable.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class HeightDieUpdateContractTests
{
    private const string Definitions = @"
Object Faller
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = HeightDieUpdate ModuleTag_HeightDie
    TargetHeight = 50
  End
End

Object FallerMovingDownOnly
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = HeightDieUpdate ModuleTag_HeightDie
    TargetHeight = 50
    OnlyWhenMovingDown = Yes
  End
End

Object FallerOverStructure
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = HeightDieUpdate ModuleTag_HeightDie
    TargetHeight = 10
    TargetHeightIncludesStructures = Yes
  End
End

Object Tower
  KindOf = STRUCTURE
  Geometry = BOX
  GeometryMajorRadius = 20
  GeometryMinorRadius = 20
  GeometryHeight = 100
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object FallerDelayed
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = HeightDieUpdate ModuleTag_HeightDie
    TargetHeight = 1000
    InitialDelay = 2000
  End
End

Object FallerParticles
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = HeightDieUpdate ModuleTag_HeightDie
    TargetHeight = -1000
    DestroyAttachedParticlesAtHeight = 30
  End
End

Object FallerSnap
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = HeightDieUpdate ModuleTag_HeightDie
    TargetHeight = 50
    SnapToGroundOnDeath = Yes
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x4E10) // "height"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static HeightDieUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<HeightDieUpdate>().Single();

    private static void SetZ(GameObject obj, float z)
    {
        var t = obj.Transform.Translation;
        obj.UpdateTransform(new Vector3(t.X, t.Y, z));
        obj.UpdateColliders();
    }

    [Fact]
    public void FallsBelowTargetHeight_Dies()
    {
        var game = NewGame();
        var obj = game.SpawnObject("Faller", game.CivilianPlayer, new Vector3(0, 0, 200));

        game.Step();
        Assert.False(obj.IsDestroyed);

        SetZ(obj, 40); // < TargetHeight (50)
        game.Step();

        Assert.True(obj.IsDestroyed);
    }

    [Fact]
    public void StaysAboveTargetHeight_NeverDies()
    {
        var game = NewGame();
        var obj = game.SpawnObject("Faller", game.CivilianPlayer, new Vector3(0, 0, 200));

        for (var i = 0; i < 5; i++)
        {
            SetZ(obj, 200 - i * 5); // descending but never below 50
            game.Step();
        }

        Assert.False(obj.IsDestroyed);
    }

    [Fact]
    public void OnlyWhenMovingDown_RisingBelowTarget_DoesNotDie()
    {
        var game = NewGame();
        var obj = game.SpawnObject("FallerMovingDownOnly", game.CivilianPlayer, new Vector3(0, 0, 20));

        // First tick: lastPosition is still the ctor sentinel, so directionOK is false
        // regardless (matches GPL exactly - the very first tick after spawn/delay never
        // qualifies as "moving down" under this flag).
        game.Step();
        Assert.False(obj.IsDestroyed);

        // Now genuinely rise (20 -> 45), staying below TargetHeight (50) the whole time.
        SetZ(obj, 45);
        game.Step();

        Assert.False(obj.IsDestroyed);
    }

    [Fact]
    public void OnlyWhenMovingDown_FallingBelowTarget_Dies()
    {
        var game = NewGame();
        var obj = game.SpawnObject("FallerMovingDownOnly", game.CivilianPlayer, new Vector3(0, 0, 100));

        // The module's sleepy-update registration wakes it on the frame after spawn (see
        // OnlyWhenMovingDown_RisingBelowTarget_DoesNotDie), so this first Step() is a no-op;
        // the module's actual first-ever tick is the second Step() below, which is therefore
        // the ctor-sentinel tick (directionOK forced false, matches GPL) - it seeds
        // lastPosition at 100 and stays alive (100 >= 50) regardless.
        game.Step();
        game.Step();
        Assert.False(obj.IsDestroyed);

        SetZ(obj, 40); // genuinely falling: 40 < lastPosition (100), and 40 < target (50)
        game.Step();

        Assert.True(obj.IsDestroyed);
    }

    [Fact]
    public void TargetHeightIncludesStructures_DiesRelativeToStructureTop()
    {
        var game = NewGame();
        game.SpawnObject("Tower", game.CivilianPlayer, new Vector3(0, 0, 0)); // MaxZ = 100
        var obj = game.SpawnObject("FallerOverStructure", game.CivilianPlayer, new Vector3(0, 0, 150));

        game.Step(); // 150 >= structure top (100): alive
        Assert.False(obj.IsDestroyed);

        // Below the structure's top (100) but well above the INI TargetHeight (10) alone -
        // only dies if the structure-relative target actually won.
        SetZ(obj, 90);
        game.Step();

        Assert.True(obj.IsDestroyed);
    }

    [Fact]
    public void TargetHeightIncludesStructures_NoStructureUnderneath_UsesPlainTargetHeight()
    {
        var game = NewGame();
        var obj = game.SpawnObject("FallerOverStructure", game.CivilianPlayer, new Vector3(0, 0, 90));

        // No structure nearby: falls back to the INI TargetHeight (10). 90 is well above it.
        game.Step();
        Assert.False(obj.IsDestroyed);

        SetZ(obj, 5); // < TargetHeight (10)
        game.Step();
        Assert.True(obj.IsDestroyed);
    }

    [Fact]
    public void InitialDelay_IgnoresHeightUntilElapsed()
    {
        var game = NewGame();
        // 2000ms InitialDelay = 10 frames at 5 Hz (F6). TargetHeight (1000) means "always
        // below" once the delay gate opens, from any reasonable spawn height.
        var obj = game.SpawnObject("FallerDelayed", game.CivilianPlayer, new Vector3(0, 0, 0));

        for (var i = 0; i < 8; i++)
        {
            game.Step();
        }
        Assert.False(obj.IsDestroyed);

        for (var i = 0; i < 8; i++)
        {
            game.Step();
        }
        Assert.True(obj.IsDestroyed);
    }

    [Fact]
    public void DestroyAttachedParticlesAtHeight_FiresOnceBelowThreshold()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);
        var obj = game.SpawnObject("FallerParticles", game.CivilianPlayer, new Vector3(0, 0, 100));

        game.Step(); // 100 >= 30: nothing fired yet
        Assert.Empty(recorder.DestroyedAttachedParticleSystemsFor);

        SetZ(obj, 20); // < DestroyAttachedParticlesAtHeight (30)
        game.Step();
        Assert.Single(recorder.DestroyedAttachedParticleSystemsFor);
        Assert.Equal(obj.Id, recorder.DestroyedAttachedParticleSystemsFor[0]);

        // Idempotent: staying below the threshold does not fire a second time.
        SetZ(obj, 10);
        game.Step();
        Assert.Single(recorder.DestroyedAttachedParticleSystemsFor);

        // TargetHeight (-1000) never triggers death, so this is a pure particle-cleanup
        // observation, uncoupled from Kill().
        Assert.False(obj.IsDestroyed);
    }

    [Fact]
    public void SnapToGroundOnDeath_SetsZToTerrainHeight()
    {
        var game = NewGame();
        var obj = game.SpawnObject("FallerSnap", game.CivilianPlayer, new Vector3(5, 5, 200));

        game.Step();
        Assert.False(obj.IsDestroyed);

        SetZ(obj, 10); // < TargetHeight (50)
        game.Step();

        Assert.True(obj.IsDestroyed);
        // The headless host's flat map is height 0 everywhere.
        Assert.Equal(0f, obj.Transform.Translation.Z, precision: 3);
    }

    [Fact]
    public void WithoutSnapToGroundOnDeath_LeavesZUnlessBelowGround()
    {
        var game = NewGame();
        var obj = game.SpawnObject("Faller", game.CivilianPlayer, new Vector3(0, 0, 200));

        game.Step();
        SetZ(obj, 40); // < TargetHeight (50), still above ground (0)
        game.Step();

        Assert.True(obj.IsDestroyed);
        Assert.Equal(40f, obj.Transform.Translation.Z, precision: 3);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var obj = game.SpawnObject("Faller", game.CivilianPlayer, new Vector3(0, 0, 200));

        game.Step();
        SetZ(obj, 100);
        game.Step();
        var live = ModuleOf(obj);

        var shadowHost = game.SpawnObject("Faller", game.CivilianPlayer, new Vector3(300, 0, 200));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 3);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static int[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var obj = game.SpawnObject("Faller", game.CivilianPlayer, new Vector3(0, 0, 200));
        var module = ModuleOf(obj);

        var trajectory = new int[8];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            // Descend steadily, crossing TargetHeight (50) partway through. Once the object
            // has died it is reaped from the active list, so stop touching its transform.
            if (!obj.IsDestroyed)
            {
                SetZ(obj, 200 - i * 30);
            }

            game.Step();
            trajectory[i] = obj.IsDestroyed ? 1 : 0;
        }

        return trajectory;
    }
}
