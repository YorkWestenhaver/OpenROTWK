// Mocked-game contract tests for the AutoPickUpUpdate port (R13), following
// bfme2-workbench/research/modules-r13/specs/AutoPickUpUpdateModuleData.md §3: the hunger gate
// (§1.1), the scan + AND'ed filter gates (§1.2, F-APU-1), consume-and-heal-toward-TargetHealth
// with no overheal (§1.3), the unconditional re-arm (§1.4), the shadow-copy base test, and a
// mid-scan save/load round-trip.
//
// Sleepy-update caveat (api-freeze-v1 §S6, confirmed by EmpUpdateContractTests): a freshly
// spawned module's NextCallFrame is floored to "now" at creation, and Update() only runs once
// CurrentFrame >= NextCallFrame - the tick that observes CurrentFrame == N runs on the (N+1)th
// HeadlessSimGame.Step() call. A freshly spawned module's first real Update() therefore lands
// on the second Step(), never the first.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class AutoPickUpUpdateContractTests
{
    // 5 Hz logic rate (F6): 1000ms = 5 frames.
    private const string Definitions = @"
Object Eater
  KindOf = NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoPickUpUpdate ModuleTag_Eat
    ScanDelayTime = 1000
    ScanDistance = 50
    EatObjectEntry = MyHealth:80% TargetHealth:100% Filter:NONE+FOOD
  End
End

Object EaterLowTarget
  KindOf = NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoPickUpUpdate ModuleTag_Eat
    ScanDelayTime = 1000
    ScanDistance = 50
    EatObjectEntry = MyHealth:80% TargetHealth:60% Filter:NONE+FOOD
  End
End

Object FoodProp
  KindOf = FOOD NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
End

Object NonFoodProp
  KindOf = NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
End

Object EaterWithUnwiredFields
  KindOf = NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoPickUpUpdate ModuleTag_Eat
    ScanDelayTime = 1000
    ScanDistance = 50
    EatObjectEntry = MyHealth:80% TargetHealth:100% Filter:NONE+FOOD
    Bored = Yes
    BoredFilter = NONE+FOOD
    RunFromButton = Yes
    RunFromButtonNumber = 3
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xA9C) // "auto pick up"
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

    private static AutoPickUpUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<AutoPickUpUpdate>().Single();

    /// <summary>Damages an object down to the given absolute health (from its current, full
    /// health), leaving it below the 80% hunger threshold when requested.</summary>
    private static void DamageTo(GameObject target, float targetHealth)
    {
        var body = target.BodyModule;
        var amount = body.Health - targetHealth;
        if (amount > 0f)
        {
            PortedModuleTestKit.ApplyDamage(target, amount);
        }
    }

    [Fact]
    public void NotHungry_NoScanEffect_AboveMyHealthThreshold()
    {
        var game = NewGame();
        var eater = game.SpawnObject("Eater", game.CivilianPlayer, new Vector3(100, 100, 0));
        var food = game.SpawnObject("FoodProp", game.CivilianPlayer, new Vector3(120, 100, 0));

        StepFrames(game, 6);

        Assert.False(food.IsDestroyed);
        Assert.Equal(100f, eater.BodyModule.Health);
        Assert.Equal(0, ModuleOf(eater).NumEaten);
    }

    [Fact]
    public void Hungry_EatsNearestQualifyingCandidate_HealsTowardTargetHealth()
    {
        var game = NewGame();
        var eater = game.SpawnObject("Eater", game.CivilianPlayer, new Vector3(100, 100, 0));
        DamageTo(eater, 50f);
        var food = game.SpawnObject("FoodProp", game.CivilianPlayer, new Vector3(120, 100, 0));

        StepFrames(game, 6);

        Assert.True(food.IsDestroyed);
        Assert.Equal(1, ModuleOf(eater).NumEaten);
        Assert.True(eater.BodyModule.Health > 50f, "health must have moved toward TargetHealth");
        Assert.True(eater.BodyModule.Health <= 100f, "health must never exceed max");
    }

    [Fact]
    public void NoOverheal_TargetHealthBelow100Percent()
    {
        var game = NewGame();
        var eater = game.SpawnObject("EaterLowTarget", game.CivilianPlayer, new Vector3(100, 100, 0));
        DamageTo(eater, 50f);
        game.SpawnObject("FoodProp", game.CivilianPlayer, new Vector3(120, 100, 0));

        StepFrames(game, 6);

        Assert.Equal(1, ModuleOf(eater).NumEaten);
        Assert.Equal(60f, eater.BodyModule.Health, precision: 3);
    }

    [Fact]
    public void OutOfRangeCandidate_NotEaten()
    {
        var game = NewGame();
        var eater = game.SpawnObject("Eater", game.CivilianPlayer, new Vector3(100, 100, 0));
        DamageTo(eater, 50f);
        var food = game.SpawnObject("FoodProp", game.CivilianPlayer, new Vector3(300, 100, 0));

        StepFrames(game, 6);

        Assert.False(food.IsDestroyed);
        Assert.Equal(0, ModuleOf(eater).NumEaten);
    }

    [Fact]
    public void FilterExcludesNonFoodCandidate()
    {
        var game = NewGame();
        var eater = game.SpawnObject("Eater", game.CivilianPlayer, new Vector3(100, 100, 0));
        DamageTo(eater, 50f);
        var nonFood = game.SpawnObject("NonFoodProp", game.CivilianPlayer, new Vector3(120, 100, 0));

        StepFrames(game, 6);

        Assert.False(nonFood.IsDestroyed);
        Assert.Equal(0, ModuleOf(eater).NumEaten);
    }

    [Fact]
    public void RearmsOnScanDelayTime_RegardlessOfOutcome()
    {
        var game = NewGame();
        var eater = game.SpawnObject("Eater", game.CivilianPlayer, new Vector3(100, 100, 0));
        DamageTo(eater, 50f);
        var farFood = game.SpawnObject("FoodProp", game.CivilianPlayer, new Vector3(300, 100, 0));

        // First scan (module observes CurrentFrame == 5, seen on the 6th Step()): the only
        // candidate is out of range, so the scan fails - a failed scan must not stop the
        // module from re-arming.
        StepFrames(game, 6);
        Assert.False(farFood.IsDestroyed);
        Assert.Equal(0, ModuleOf(eater).NumEaten);

        // A qualifying candidate now appears in range. It must not be picked up before the
        // module's next scheduled cadence tick (CurrentFrame == 10, seen on the 11th Step() -
        // 5 more Step() calls from here), proving the module is asleep in between, not busy-
        // looping - and it must be picked up exactly on that tick, proving the failed scan did
        // re-arm rather than sleeping forever.
        var nearFood = game.SpawnObject("FoodProp", game.CivilianPlayer, new Vector3(120, 100, 0));
        StepFrames(game, 4);
        Assert.False(nearFood.IsDestroyed, "must not re-scan before the next cadence tick");
        StepFrames(game, 1);
        Assert.True(nearFood.IsDestroyed, "must re-scan exactly on the next cadence tick");
        Assert.Equal(1, ModuleOf(eater).NumEaten);
    }

    [Fact]
    public void UnwiredFields_Bored_BoredFilter_RunFromButton_ParseRoundTripWithoutThrowing()
    {
        // F-APU-3 / F-APU-4: Bored/BoredFilter/RunFromButton/RunFromButtonNumber are parsed
        // for authoring round-trip fidelity but not wired into any observable behavior - this
        // is a parse-only check, not a behavior test.
        var game = NewGame();
        var data = (AutoPickUpUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("EaterWithUnwiredFields").Behaviors["ModuleTag_Eat"].Data;

        Assert.True(data.Bored);
        Assert.NotNull(data.BoredFilter);
        Assert.True(data.RunFromButton);
        Assert.Equal(3, data.RunFromButtonNumber);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidScan()
    {
        var game = NewGame();
        var eater = game.SpawnObject("Eater", game.CivilianPlayer, new Vector3(100, 100, 0));
        DamageTo(eater, 50f);
        game.SpawnObject("FoodProp", game.CivilianPlayer, new Vector3(120, 100, 0));
        var live = ModuleOf(eater);

        StepFrames(game, 6);
        Assert.Equal(1, live.NumEaten);

        var shadow = ModuleOf(game.SpawnObject("Eater", game.CivilianPlayer, new Vector3(400, 400, 0)));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidScan_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 3);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static (bool FoodDestroyed, int NumEaten)[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame();
        var eater = game.SpawnObject("Eater", game.CivilianPlayer, new Vector3(100, 100, 0));
        DamageTo(eater, 50f);
        var food = game.SpawnObject("FoodProp", game.CivilianPlayer, new Vector3(120, 100, 0));
        var module = ModuleOf(eater);

        var trajectory = new (bool, int)[8];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = (food.IsDestroyed, module.NumEaten);
        }

        return trajectory;
    }
}
