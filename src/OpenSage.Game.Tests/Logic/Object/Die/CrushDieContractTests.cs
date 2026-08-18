// Mocked-game unit tests for the CrushDie port (experiment-round-4 §4.1 DoD item 4): one
// test per INI/behaviour branch, each [create -> trigger death -> observable effect] via the
// batch's death-trigger helper, plus the shadow-copy base test and the mid-behavior
// save/load continuation. Object definitions come from INI text through the real parser, so
// the audited parse vocabulary is on the tested path.
//
// Geometry used throughout: victims have GeometryMajorRadius = 10 and face +X (yaw 0), so
// their crush points are centre (0,0), front (+5,0) and back (-5,0) in local terms. The
// crusher is placed to make exactly one of them nearest:
//   (20, 0) -> front  (d^2 225 vs centre 400, back 625)
//   (-20,0) -> back   (d^2 225 vs centre 400, front 625)
//   (0, 20) -> centre (d^2 400 vs front 425, back 425)  => TOTAL_CRUSH

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class CrushDieContractTests
{
    private const string Definitions = @"
Object CrushVictim
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 10
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CrushDie ModuleTag_Die
  End
End

Object CrushVictimCrushedOnly
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 10
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CrushDie ModuleTag_Die
    DeathTypes = NONE +CRUSHED
  End
End

Object CrushVictimWithSounds
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 10
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CrushDie ModuleTag_Die
    TotalCrushSound = CrushTotal
    BackEndCrushSound = CrushBack
    FrontEndCrushSound = CrushFront
  End
End

Object CrushVictimNeverAudible
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 10
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CrushDie ModuleTag_Die
    TotalCrushSound = CrushTotal
    TotalCrushSoundPercent = 0
  End
End

Object Crusher
  KindOf = VEHICLE
  Geometry = BOX
  GeometryMajorRadius = 15
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0FFEEu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject SpawnVictim(HeadlessSimGame game, string definition = "CrushVictim")
        => game.SpawnObject(definition, game.CivilianPlayer, Vector3.Zero);

    private static GameObject SpawnCrusher(HeadlessSimGame game, float x, float y)
        => game.SpawnObject("Crusher", game.CivilianPlayer, new Vector3(x, y, 0));

    private static CrushDie CrushModuleOf(GameObject gameObject)
        => Assert.IsType<CrushDie>(gameObject.FindBehavior<CrushDie>());

    /// <summary>The physics crush: crush damage, crushed death, from a crusher object.</summary>
    private static void Crush(GameObject victim, GameObject crusher) =>
        PortedModuleTestKit.TriggerDeath(victim, DeathType.Crushed, DamageType.Crush, crusher);

    private static ulong DrawCount(HeadlessSimGame game)
        => game.GameEngine.SimContext.GameLogicRandom.DrawCount;

    private static void AssertCrushed(GameObject victim, bool front, bool back)
    {
        Assert.Equal(front, victim.BodyModule.FrontCrushed);
        Assert.Equal(back, victim.BodyModule.BackCrushed);
        Assert.Equal(front, victim.ModelConditionFlags.Get(ModelConditionFlag.FrontCrushed));
        Assert.Equal(back, victim.ModelConditionFlags.Get(ModelConditionFlag.BackCrushed));
    }

    // ---- branch: crush point selection ----

    [Fact]
    public void CrusherNearestTheCentre_TotalCrush()
    {
        var game = NewGame();
        var victim = SpawnVictim(game);
        var crusher = SpawnCrusher(game, 0, 20);

        Crush(victim, crusher);

        AssertCrushed(victim, front: true, back: true);
    }

    [Fact]
    public void CrusherNearestTheFrontPoint_FrontEndCrush()
    {
        var game = NewGame();
        var victim = SpawnVictim(game);
        var crusher = SpawnCrusher(game, 20, 0);

        Crush(victim, crusher);

        AssertCrushed(victim, front: true, back: false);
    }

    [Fact]
    public void CrusherNearestTheBackPoint_BackEndCrush()
    {
        var game = NewGame();
        var victim = SpawnVictim(game);
        var crusher = SpawnCrusher(game, -20, 0);

        Crush(victim, crusher);

        AssertCrushed(victim, front: false, back: true);
    }

    [Fact]
    public void AlreadyBackCrushed_FrontHit_UpgradesToTotalCrush()
    {
        // The half-crushed path: the centre point is no longer offered, and hitting the one
        // remaining (front) point finishes the object off.
        var game = NewGame();
        var victim = SpawnVictim(game);
        victim.BodyModule.BackCrushed = true;
        var crusher = SpawnCrusher(game, 20, 0);

        Crush(victim, crusher);

        AssertCrushed(victim, front: true, back: true);
    }

    [Fact]
    public void AlreadyFrontCrushed_BackHit_UpgradesToTotalCrush()
    {
        var game = NewGame();
        var victim = SpawnVictim(game);
        victim.BodyModule.FrontCrushed = true;
        var crusher = SpawnCrusher(game, -20, 0);

        Crush(victim, crusher);

        AssertCrushed(victim, front: true, back: true);
    }

    [Fact]
    public void AlreadyTotallyCrushed_NoCrushPointRemains_NothingChanges()
    {
        // Both flags set: every candidate block is skipped, the sentinel survives and the
        // result is NO_CRUSH - the module returns before touching anything.
        var game = NewGame();
        var victim = SpawnVictim(game);
        victim.BodyModule.FrontCrushed = true;
        victim.BodyModule.BackCrushed = true;
        var crusher = SpawnCrusher(game, 20, 0);
        var drawsBefore = DrawCount(game);

        Crush(victim, crusher);

        // Flags unchanged, model conditions never assigned, and no percentage roll happened.
        Assert.True(victim.BodyModule.FrontCrushed);
        Assert.True(victim.BodyModule.BackCrushed);
        Assert.False(victim.ModelConditionFlags.Get(ModelConditionFlag.FrontCrushed));
        Assert.False(victim.ModelConditionFlags.Get(ModelConditionFlag.BackCrushed));
        Assert.Equal(drawsBefore, DrawCount(game));
    }

    // ---- branch: the damage-type and death-type gates ----

    [Fact]
    public void NonCrushDamage_IsIgnored()
    {
        var game = NewGame();
        var victim = SpawnVictim(game);
        var crusher = SpawnCrusher(game, 0, 20);

        PortedModuleTestKit.TriggerDeath(victim, DeathType.Exploded, DamageType.Explosion, crusher);

        AssertCrushed(victim, front: false, back: false);
    }

    [Fact]
    public void DeathTypesFilter_KeepsTheModuleOutOfNonCrushedDeaths()
    {
        // The shared Die mux runs before the module: DeathTypes = NONE +CRUSHED means a
        // crush-damage death with a non-crushed DeathType never reaches CrushDie.
        var game = NewGame();
        var filtered = SpawnVictim(game, "CrushVictimCrushedOnly");
        var crusher = SpawnCrusher(game, 0, 20);

        PortedModuleTestKit.TriggerDeath(filtered, DeathType.Burned, DamageType.Crush, crusher);
        AssertCrushed(filtered, front: false, back: false);

        var accepted = SpawnVictim(game, "CrushVictimCrushedOnly");
        Crush(accepted, crusher);
        AssertCrushed(accepted, front: true, back: true);
    }

    [Fact]
    public void MissingDamageDealer_DegeneratesToTotalCrush()
    {
        // A crusher destroyed in the same frame leaves no object to measure against.
        var game = NewGame();
        var victim = SpawnVictim(game);

        PortedModuleTestKit.TriggerDeath(victim, DeathType.Crushed, DamageType.Crush, source: null);

        AssertCrushed(victim, front: true, back: true);
    }

    // ---- branch: the crush-sound percentage roll (conformance channel 5) ----

    [Fact]
    public void NoSoundConfigured_TakesNoRandomDraw()
    {
        var game = NewGame();
        var victim = SpawnVictim(game);
        var crusher = SpawnCrusher(game, 0, 20);
        var drawsBefore = DrawCount(game);

        Crush(victim, crusher);

        Assert.Equal(drawsBefore, DrawCount(game));
        AssertCrushed(victim, front: true, back: true);
    }

    [Fact]
    public void SoundConfigured_TakesExactlyOneRandomDraw()
    {
        var game = NewGame();
        var victim = SpawnVictim(game, "CrushVictimWithSounds");
        var crusher = SpawnCrusher(game, 20, 0);
        var drawsBefore = DrawCount(game);

        Crush(victim, crusher);

        // The audio is an output, but the roll is on the logic stream and is counted.
        Assert.Equal(drawsBefore + 1, DrawCount(game));
        AssertCrushed(victim, front: true, back: false);
    }

    [Fact]
    public void ZeroPercentSound_StillTakesTheDrawAndStillCrushes()
    {
        // 0 == never audible, but GPL rolls first and compares second: the draw happens,
        // so removing it would desync the stream even though nothing is heard.
        var game = NewGame();
        var victim = SpawnVictim(game, "CrushVictimNeverAudible");
        var crusher = SpawnCrusher(game, 0, 20);
        var drawsBefore = DrawCount(game);

        Crush(victim, crusher);

        Assert.Equal(drawsBefore + 1, DrawCount(game));
        AssertCrushed(victim, front: true, back: true);
    }

    [Fact]
    public void SoundPercentDefaultsToAlways()
    {
        // GPL's ModuleData ctor seeds all three percentages with 100; an INI that names a
        // sound and no percentage must therefore always play it.
        var game = NewGame();
        var data = Assert.IsType<CrushDieModuleData>(
            game.AssetStore.ObjectDefinitions.GetByName("CrushVictimWithSounds")
                .Behaviors["ModuleTag_Die"].Data);

        Assert.Equal(100, data.TotalCrushSoundPercent);
        Assert.Equal(100, data.BackEndCrushSoundPercent);
        Assert.Equal(100, data.FrontEndCrushSoundPercent);
    }

    // ---- the contract walk ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        // Mid-behavior: the live module has already resolved a crush and written its result
        // through to the body, so the walk is exercised on a module that has done its work.
        var game = NewGame();
        var victim = SpawnVictim(game);
        var crusher = SpawnCrusher(game, 20, 0);
        var live = CrushModuleOf(victim);

        Crush(victim, crusher);
        game.Step();
        AssertCrushed(victim, front: true, back: false);

        var shadowHost = game.SpawnObject("CrushVictim", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = CrushModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        // Two identical games, identical script; game B round-trips the module state
        // mid-behavior (between the crush and the frames that follow it). Anything the walk
        // lost or misread would show up as a different continuation.
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 2);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static (bool Front, bool Back, bool Destroyed, ulong Draws)[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEEDu);
        var victim = SpawnVictim(game, "CrushVictimWithSounds");
        var crusher = SpawnCrusher(game, -20, 0);
        var module = CrushModuleOf(victim);

        var trajectory = new (bool, bool, bool, ulong)[6];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == 1)
            {
                // The behavior itself: a back-end crush, one draw, flags written.
                Crush(victim, crusher);
            }

            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                PortedModuleTestKit.Load(module, state);
            }

            game.Step();
            trajectory[i] = (victim.BodyModule.FrontCrushed, victim.BodyModule.BackCrushed,
                victim.IsDestroyed, DrawCount(game));
        }

        return trajectory;
    }
}
