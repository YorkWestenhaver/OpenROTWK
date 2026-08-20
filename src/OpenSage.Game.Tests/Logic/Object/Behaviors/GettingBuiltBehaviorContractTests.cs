// Mocked-game contract tests for the GettingBuiltBehavior R12 port: worker spawn/despawn
// around the construction clock, evil-faction worker selection, damage extending the clock,
// the rubble -> RebuildWhenDead restart, and the BFME2 DisallowRebuildFilter/-Range gate.
// BFME2 logic rate is 5 Hz (F6), so a 1-second SpawnTimer/RebuildTimeSeconds is 5 frames.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class GettingBuiltBehaviorContractTests
{
    private const string Definitions = @"
Object BuiltStructure
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GettingBuiltBehavior ModuleTag_Build
    WorkerName = GoodWorker
    SpawnTimer = 1
    RebuildTimeSeconds = 1
    RebuildWhenDead = Yes
  End
End

Object EvilStructure
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GettingBuiltBehavior ModuleTag_Build
    WorkerName = GoodWorker
    EvilWorkerName = EvilWorker
    SpawnTimer = 1
    RebuildTimeSeconds = 1
  End
End

Object GuardedStructure
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GettingBuiltBehavior ModuleTag_Build
    WorkerName = GoodWorker
    SpawnTimer = 1
    RebuildTimeSeconds = 1
    RebuildWhenDead = Yes
    DisallowRebuildRange = 50
    DisallowRebuildFilter = NONE +INFANTRY
  End
End

Object GoodWorker
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End

Object EvilWorker
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End

Object Guard
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB17D)
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

    private static void LethalDamage(GameObject target)
    {
        target.AttemptDamage(new DamageInfoInput(null)
        {
            DamageType = DamageType.Unresistable,
            DeathType = DeathType.Normal,
            Amount = 1000f,
        });
    }

    private static GettingBuiltBehavior ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<GettingBuiltBehavior>().Single();

    private static GameObject StartConstruction(HeadlessSimGame game, string definitionName, Vector3 position)
    {
        var structure = game.SpawnObject(definitionName, game.CivilianPlayer, position);
        structure.SetIsBeingConstructed();
        return structure;
    }

    [Fact]
    public void ConstructionStart_SpawnsWorker()
    {
        var game = NewGame();
        var structure = StartConstruction(game, "BuiltStructure", new Vector3(100, 100, 0));

        // The ctor arms UpdateSleepTime.None (delay 1), and the sleepy queue only runs a
        // module once CurrentFrame reaches its NextCallFrame at the START of Step() - one
        // frame later than the delay alone suggests - so this is the first tick that sees it.
        StepFrames(game, 2);

        var module = ModuleOf(structure);
        Assert.True(module.WorkerId.IsValid);
        var worker = game.GameLogic.GetObjectById(module.WorkerId);
        Assert.NotNull(worker);
        Assert.Equal("GoodWorker", worker.Definition.Name);
        Assert.True(module.IsConstructionActive);
    }

    [Fact]
    public void ConstructionCompletes_AfterSpawnTimer_AndDespawnsWorker()
    {
        var game = NewGame();
        var structure = StartConstruction(game, "BuiltStructure", new Vector3(100, 100, 0));

        StepFrames(game, 2); // detect + start the clock, spawn the worker
        var module = ModuleOf(structure);
        var workerId = module.WorkerId;
        Assert.True(workerId.IsValid);

        StepFrames(game, 5); // SpawnTimer = 1s = 5 frames

        Assert.False(module.IsConstructionActive);
        Assert.False(structure.IsBeingConstructed());
        Assert.Null(game.GameLogic.GetObjectById(workerId));
    }

    [Fact]
    public void EvilWorkerName_PreferredOverWorkerName()
    {
        var game = NewGame();
        var structure = StartConstruction(game, "EvilStructure", new Vector3(100, 100, 0));

        StepFrames(game, 2);

        var module = ModuleOf(structure);
        Assert.Equal("EvilWorker", module.EffectiveWorkerName);
        var worker = game.GameLogic.GetObjectById(module.WorkerId);
        Assert.Equal("EvilWorker", worker.Definition.Name);
    }

    [Fact]
    public void DamageDuringConstruction_ExtendsCompletionFrame()
    {
        var game = NewGame();
        var structure = StartConstruction(game, "BuiltStructure", new Vector3(100, 100, 0));

        StepFrames(game, 2); // clock started
        var module = ModuleOf(structure);
        var completionBeforeDamage = module.CompletionFrame;

        structure.AttemptDamage(new DamageInfoInput(null)
        {
            DamageType = DamageType.Explosion,
            DeathType = DeathType.Normal,
            Amount = 10f,
        });

        Assert.True(module.CompletionFrame > completionBeforeDamage);

        // Construction takes longer than the bare SpawnTimer would have (5 frames from start).
        StepFrames(game, 5);
        Assert.True(module.IsConstructionActive);
    }

    [Fact]
    public void RubbleDestruction_RebuildResumes_WhenRebuildWhenDeadTrue()
    {
        var game = NewGame();
        var structure = StartConstruction(game, "BuiltStructure", new Vector3(100, 100, 0));
        StepFrames(game, 2);
        var module = ModuleOf(structure);

        LethalDamage(structure);

        Assert.False(structure.IsEffectivelyDead);
        Assert.Equal(BodyDamageType.Rubble, structure.BodyModule.DamageState);
        Assert.True(module.IsRebuildBlocked);
        Assert.False(module.IsConstructionActive);

        StepFrames(game, 2); // no DisallowRebuildFilter configured: clears immediately

        Assert.False(module.IsRebuildBlocked);
        Assert.True(module.IsRebuilding);
        Assert.True(module.IsConstructionActive);

        StepFrames(game, 5); // RebuildTimeSeconds = 1s = 5 frames

        Assert.False(module.IsConstructionActive);
        Assert.False(structure.IsBeingConstructed());
    }

    [Fact]
    public void DisallowRebuildFilter_BlocksRebuild_UntilGuardIsGone()
    {
        var game = NewGame();
        var structure = StartConstruction(game, "GuardedStructure", new Vector3(200, 200, 0));
        StepFrames(game, 2);
        var module = ModuleOf(structure);

        var guard = game.SpawnObject("Guard", game.CivilianPlayer, new Vector3(210, 200, 0));

        LethalDamage(structure);
        StepFrames(game, 3);

        Assert.True(module.IsRebuildBlocked);
        Assert.False(module.IsConstructionActive);

        guard.Destroy();
        StepFrames(game, 1);

        Assert.False(module.IsRebuildBlocked);
        Assert.True(module.IsRebuilding);
        Assert.True(module.IsConstructionActive);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidConstruction()
    {
        var game = NewGame();
        var structure = StartConstruction(game, "BuiltStructure", new Vector3(100, 100, 0));
        StepFrames(game, 2);
        var live = ModuleOf(structure);
        Assert.True(live.IsConstructionActive);

        var shadowHost = game.SpawnObject("BuiltStructure", game.CivilianPlayer, new Vector3(400, 400, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
