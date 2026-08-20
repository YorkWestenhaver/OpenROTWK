// Mocked-game contract tests for the NotifyTargetsOfImminentProbableCrushingUpdate port
// (R12): the periodic scan within DefaultScanWidth, the enemy/non-structure/CrusherLevel vs
// CrushableLevel eligibility gate, simultaneous multi-target warning, the falling edge on a
// target leaving range or dying, the no-module-means-no-broadcast baseline, plus the
// shadow-copy base test and a mid-state save/load round-trip continuation. Object
// definitions are parsed from INI text through the real parser (a bare, field-less
// Behavior block - the corpus-confirmed shape for this class, see the module's file header).

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class NotifyTargetsOfImminentProbableCrushingUpdateContractTests
{
    // Cavalry: CrusherLevel 2 (can crush CrushableLevel < 2). DefaultScanWidth = 40.
    private const string Definitions = @"
Object Cavalry
  KindOf = CAVALRY
  CrusherLevel = 2
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = NotifyTargetsOfImminentProbableCrushingUpdate ModuleTag_NotifyCrushScan
  End
End

Object CavalryNoModule
  KindOf = CAVALRY
  CrusherLevel = 2
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object Infantry
  KindOf = INFANTRY
  CrushableLevel = 1
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object ToughInfantry
  KindOf = INFANTRY
  CrushableLevel = 2
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object Bunker
  KindOf = STRUCTURE
  CrushableLevel = 1
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC90C)
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

    private static NotifyTargetsOfImminentProbableCrushingUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<NotifyTargetsOfImminentProbableCrushingUpdate>().Single();

    private static bool Braced(GameObject obj) =>
        obj.ModelConditionFlags.Get(ModelConditionFlag.EmotionBraceForBeingCrushed);

    private static void MakeEnemies(Player a, Player b)
    {
        a.AddEnemy(b);
        b.AddEnemy(a);
    }

    [Fact]
    public void CrusherInScanWidth_WarnsInfantryTarget()
    {
        var game = NewGame();
        var cavalry = game.SpawnObject("Cavalry", game.CivilianPlayer, new Vector3(0, 0, 0));
        var infantry = game.SpawnObject("Infantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        Assert.False(Braced(infantry)); // nothing scanned yet
        StepFrames(game, 2);
        Assert.True(Braced(infantry));
    }

    [Fact]
    public void MultipleTargetsInScanWidth_AllWarnedSimultaneously()
    {
        var game = NewGame();
        var cavalry = game.SpawnObject("Cavalry", game.CivilianPlayer, new Vector3(0, 0, 0));
        var infantryA = game.SpawnObject("Infantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        var infantryB = game.SpawnObject("Infantry", game.PlayerManager.NeutralPlayer, new Vector3(-15, 5, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 2);

        Assert.True(Braced(infantryA));
        Assert.True(Braced(infantryB));
        Assert.Equal(2, ModuleOf(cavalry).WarnedTargets.Count);
    }

    [Fact]
    public void TargetOutsideScanWidth_IsNotWarned()
    {
        var game = NewGame();
        game.SpawnObject("Cavalry", game.CivilianPlayer, new Vector3(0, 0, 0));
        var farInfantry = game.SpawnObject("Infantry", game.PlayerManager.NeutralPlayer, new Vector3(500, 0, 0)); // > DefaultScanWidth (40)
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 2);

        Assert.False(Braced(farInfantry));
    }

    [Fact]
    public void NonEnemyTarget_IsNotWarned()
    {
        var game = NewGame();
        game.SpawnObject("Cavalry", game.CivilianPlayer, new Vector3(0, 0, 0));
        // Same owner, no enemy relationship declared: an ally is never braced.
        var ally = game.SpawnObject("Infantry", game.CivilianPlayer, new Vector3(10, 0, 0));

        StepFrames(game, 2);

        Assert.False(Braced(ally));
    }

    [Fact]
    public void StructureTarget_IsExcluded()
    {
        var game = NewGame();
        game.SpawnObject("Cavalry", game.CivilianPlayer, new Vector3(0, 0, 0));
        var bunker = game.SpawnObject("Bunker", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 2);

        Assert.False(Braced(bunker));
    }

    [Fact]
    public void TargetNotCrushableByThisCrusher_IsNotWarned()
    {
        var game = NewGame();
        game.SpawnObject("Cavalry", game.CivilianPlayer, new Vector3(0, 0, 0));
        // CrushableLevel 2 == CrusherLevel 2: not strictly lower, so not crushable here.
        var tough = game.SpawnObject("ToughInfantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 2);

        Assert.False(Braced(tough));
    }

    [Fact]
    public void TargetMovesBeyondScanWidth_FallingEdgeClearsBrace()
    {
        var game = NewGame();
        var cavalry = game.SpawnObject("Cavalry", game.CivilianPlayer, new Vector3(0, 0, 0));
        var infantry = game.SpawnObject("Infantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 2);
        Assert.True(Braced(infantry));

        infantry.UpdateTransform(new Vector3(500, 0, 0));
        infantry.UpdateColliders();
        StepFrames(game, 2);

        Assert.False(Braced(infantry));
        Assert.Empty(ModuleOf(cavalry).WarnedTargets);
    }

    [Fact]
    public void TargetDies_CrushProbabilityDropsToZero_ClearsBrace()
    {
        var game = NewGame();
        var cavalry = game.SpawnObject("Cavalry", game.CivilianPlayer, new Vector3(0, 0, 0));
        var infantry = game.SpawnObject("Infantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 2);
        Assert.True(Braced(infantry));

        infantry.Kill();
        StepFrames(game, 2);

        Assert.False(Braced(infantry));
        Assert.Empty(ModuleOf(cavalry).WarnedTargets);
    }

    [Fact]
    public void CrusherWithoutTheModule_NeverBroadcastsWarnings()
    {
        var game = NewGame();
        game.SpawnObject("CavalryNoModule", game.CivilianPlayer, new Vector3(0, 0, 0));
        var infantry = game.SpawnObject("Infantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 2);

        Assert.False(Braced(infantry));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("Cavalry", game.CivilianPlayer, new Vector3(0, 0, 0));
        game.SpawnObject("Infantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 2);
        var live = ModuleOf(liveHost);
        Assert.NotEmpty(live.WarnedTargets);

        var shadowHost = game.SpawnObject("Cavalry", game.CivilianPlayer, new Vector3(400, 400, 0));
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
        var cavalry = game.SpawnObject("Cavalry", game.CivilianPlayer, new Vector3(0, 0, 0));
        var infantry = game.SpawnObject("Infantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);
        var module = ModuleOf(cavalry);

        var trajectory = new int[10];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            // Move the target out of and back into scan width mid-run so the brace flag
            // actually toggles, exercising both edges through the round-trip.
            infantry.UpdateTransform(i == 5 ? new Vector3(500, 0, 0) : new Vector3(10, 0, 0));
            infantry.UpdateColliders();

            game.Step();
            trajectory[i] = Braced(infantry) ? 1 : 0;
        }

        return trajectory;
    }
}
