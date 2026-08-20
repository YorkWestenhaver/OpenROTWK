// Mocked-game unit tests for the TransitionDamageFX port (experiment-round-4 §4.1, DoD item
// 4): one test per behavior branch, each [create -> apply real S1 damage -> observable
// effect], plus the shadow-copy base test taken mid-behavior and a mid-state save/load
// continuation. Object definitions are parsed from INI text through the real parser, so the
// real parse table is on the tested path.
//
// The module's whole output is a set of ISimEvents requests (FXList + attached particle
// systems), so the tests install the recording sink and assert on what the sim asked the
// client to do. Damage is applied through the S1 ActiveBody health pipeline (this batch
// builds ON S1); the damage-state transitions it drives are what invoke the module.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Damage;

public class TransitionDamageFXContractTests
{
    // Thresholds: Damaged at 65%, ReallyDamaged at 40% (division-free predicate,
    // health > max * threshold). MaxHealth 100 => Damaged when health <= 65,
    // ReallyDamaged when health <= 40.
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.65
  UnitReallyDamagedThreshold = 0.4
End

FXList FX_Damaged
End

FXList FX_ReallyDamaged
End

FXParticleSystem PS_Damaged
End

FXParticleSystem PS_ReallyDamaged
End

ObjectCreationList OCL_Debris
  CreateObject
    ObjectNames = Debris
    Count = 1
  End
End

Object Debris
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End

Object Tower
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = TransitionDamageFX ModuleTag_FX
    DamagedFXList1 = Loc: X:0 Y:0 Z:0 FXList:FX_Damaged
    DamagedParticleSystem1 = Bone:None RandomBone:No PSys:PS_Damaged
    DamagedOCL1 = Loc: X:0 Y:0 Z:0 OCL:OCL_Debris
    ReallyDamagedFXList1 = Loc: X:0 Y:0 Z:0 FXList:FX_ReallyDamaged
    ReallyDamagedParticleSystem1 = Bone:None RandomBone:No PSys:PS_ReallyDamaged
    ReallyDamagedOCL1 = Loc: X:0 Y:0 Z:0 OCL:OCL_Debris
  End
End

Object FlameOnlyParticleTower
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = TransitionDamageFX ModuleTag_FX
    DamageParticleTypes = NONE +FLAME
    DamagedFXList1 = Loc: X:0 Y:0 Z:0 FXList:FX_Damaged
    DamagedParticleSystem1 = Bone:None RandomBone:No PSys:PS_Damaged
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xD00)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static void Damage(GameObject target, float amount, DamageType type = DamageType.Explosion)
    {
        target.AttemptDamage(new DamageInfoInput(null)
        {
            DamageType = type,
            DeathType = DeathType.Normal,
            Amount = amount,
        });
    }

    private static TransitionDamageFX ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<TransitionDamageFX>().Single();

    [Fact]
    public void Pristine_To_Damaged_FiresDamagedFxAndParticles()
    {
        var game = NewGame();
        var tower = game.SpawnObject("Tower", game.CivilianPlayer, Vector3.Zero);
        var events = RecordingSimEvents.InstallOn(game);

        Damage(tower, 40f); // health 60 -> Damaged (<= 65)

        Assert.Equal(BodyDamageType.Damaged, tower.BodyModule.DamageState);
        Assert.Equal(60f, tower.BodyModule.Health);

        // FXList fired unoriented (GPL doFXPos), at the object.
        var fx = Assert.Single(events.Events);
        Assert.Equal("FX_Damaged", fx.FXListName);
        Assert.Equal(FXOrientation.PositionOnly, fx.Orientation);
        Assert.Equal(tower.Id, fx.ObjectId);

        var ps = Assert.Single(events.ParticleSystems);
        Assert.Equal("PS_Damaged", ps.ParticleSystemName);
        Assert.Equal(tower.Id, ps.ObjectId);
    }

    [Fact]
    public void SequentialWorsening_FiresEachReachedStateEffects()
    {
        var game = NewGame();
        var tower = game.SpawnObject("Tower", game.CivilianPlayer, Vector3.Zero);
        var events = RecordingSimEvents.InstallOn(game);

        Damage(tower, 40f); // -> Damaged (60)
        Damage(tower, 30f); // -> ReallyDamaged (30)

        Assert.Equal(BodyDamageType.ReallyDamaged, tower.BodyModule.DamageState);

        Assert.Equal(new[] { "FX_Damaged", "FX_ReallyDamaged" }, events.Events.Select(e => e.FXListName).ToArray());
        Assert.Equal(new[] { "PS_Damaged", "PS_ReallyDamaged" }, events.ParticleSystems.Select(p => p.ParticleSystemName).ToArray());
    }

    [Fact]
    public void SkippedState_FiresReachedStateOnly_NotIntermediate()
    {
        // A single blow Pristine -> ReallyDamaged fires the REACHED state's effects (GPL keys
        // the effect set by newState alone), never the skipped Damaged state's.
        var game = NewGame();
        var tower = game.SpawnObject("Tower", game.CivilianPlayer, Vector3.Zero);
        var events = RecordingSimEvents.InstallOn(game);

        Damage(tower, 70f); // health 30 -> ReallyDamaged in one hit

        Assert.Equal(BodyDamageType.ReallyDamaged, tower.BodyModule.DamageState);

        var fx = Assert.Single(events.Events);
        Assert.Equal("FX_ReallyDamaged", fx.FXListName);
        var ps = Assert.Single(events.ParticleSystems);
        Assert.Equal("PS_ReallyDamaged", ps.ParticleSystemName);
    }

    [Fact]
    public void Healing_ImprovingTransition_FiresNothing()
    {
        var game = NewGame();
        var tower = game.SpawnObject("Tower", game.CivilianPlayer, Vector3.Zero);

        Damage(tower, 40f); // -> Damaged
        Assert.Equal(BodyDamageType.Damaged, tower.BodyModule.DamageState);

        // Install the recorder AFTER the worsening so only the improving transition is watched.
        var events = RecordingSimEvents.InstallOn(game);
        tower.AttemptHealing(50f, null); // back to full -> Pristine (improving)

        Assert.Equal(BodyDamageType.Pristine, tower.BodyModule.DamageState);
        Assert.Empty(events.Events);
        Assert.Empty(events.ParticleSystems);
    }

    [Fact]
    public void DamageTypeGate_SuppressesParticles_ButNotDefaultFx()
    {
        // FlameOnlyParticleTower: DamageParticleTypes = NONE +FLAME (particles only on flame);
        // DamageFXTypes unset (default = all, GPL ctor). An EXPLOSION worsening therefore
        // fires the FXList but NOT the particle system.
        var game = NewGame();
        var tower = game.SpawnObject("FlameOnlyParticleTower", game.CivilianPlayer, Vector3.Zero);
        var events = RecordingSimEvents.InstallOn(game);

        Damage(tower, 40f, DamageType.Explosion); // -> Damaged, explosion type

        Assert.Single(events.Events);           // FX gate is default-all: fires
        Assert.Empty(events.ParticleSystems);   // particle gate excludes EXPLOSION
    }

    [Fact]
    public void DamageTypeGate_FlameDamage_FiresParticles()
    {
        var game = NewGame();
        var tower = game.SpawnObject("FlameOnlyParticleTower", game.CivilianPlayer, Vector3.Zero);
        var events = RecordingSimEvents.InstallOn(game);

        Damage(tower, 40f, DamageType.Flame); // -> Damaged, flame type: particle gate passes

        Assert.Single(events.Events);
        var ps = Assert.Single(events.ParticleSystems);
        Assert.Equal("PS_Damaged", ps.ParticleSystemName);
    }

    [Fact]
    public void DamagedOcl1_ParsesLocAndReference()
    {
        // F-R7-3: the per-state OCL slot is parse-only (audited, same Loc:/OCL: shape as the
        // FXList slots). Confirms the key is now recognized and resolves the referenced OCL.
        var game = NewGame();
        var data = Assert.IsType<TransitionDamageFXModuleData>(
            game.AssetStore.ObjectDefinitions.GetByName("Tower").Behaviors["ModuleTag_FX"].Data);

        Assert.NotNull(data.DamagedOcl1);
        Assert.Equal("OCL_Debris", data.DamagedOcl1.OCL.Value.Name);
        Assert.NotNull(data.ReallyDamagedOcl1);
        Assert.Equal("OCL_Debris", data.ReallyDamagedOcl1.OCL.Value.Name);
        Assert.Null(data.RubbleOcl1); // not set on this object -> unparsed slot stays null
    }

    [Fact]
    public void DamagedOcl1_ParsedButNotSpawned_RuntimeDeferredToOclRound()
    {
        // F-R7-3: parse-only. The OCL effect spawns sim objects (out of scope for a parse-only
        // fix, and sim-affecting), so OnBodyDamageStateChange must not act on it yet - only the
        // FXList/particle-system effects fire, exactly as before this change.
        var game = NewGame();
        var tower = game.SpawnObject("Tower", game.CivilianPlayer, Vector3.Zero);
        var events = RecordingSimEvents.InstallOn(game);

        Damage(tower, 40f); // -> Damaged: DamagedOCL1 is configured but must not spawn

        Assert.Single(events.Events);            // FXList still fires
        Assert.Single(events.ParticleSystems);   // particle system still fires
        Assert.DoesNotContain(
            game.GameLogic.Objects,
            obj => obj.Definition?.Name == "Debris");
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var tower = game.SpawnObject("Tower", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(tower);

        // Drive the module: real damage across two state transitions.
        Damage(tower, 40f);
        Damage(tower, 30f);

        var shadowHost = game.SpawnObject("Tower", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ModuleOf(shadowHost);

        // Version-only walk (this class has no mutable sim state); the base test still proves
        // the walk is symmetric and byte-stable.
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoad_ContinuesIdentically()
    {
        // Two identical games, identical damage script. Game B round-trips the module through
        // the contract Xfer walk mid-behavior; the S1 damage-to-health application and the
        // module's transition firing must be byte-identical across the save/load.
        var a = RunScenario(roundTripBeforeStep: -1);
        var b = RunScenario(roundTripBeforeStep: 1);
        Assert.Equal(a.Health, b.Health);
        Assert.Equal(a.Fx, b.Fx);
        Assert.Equal(a.Particles, b.Particles);
    }

    private static (float[] Health, string[] Fx, string[] Particles) RunScenario(int roundTripBeforeStep)
    {
        var game = NewGame(seed: 0xFEED);
        var tower = game.SpawnObject("Tower", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(tower);
        var events = RecordingSimEvents.InstallOn(game);

        // Each "step" is one damage application that worsens the state.
        var script = new[] { 40f, 30f, 25f }; // Pristine->Damaged->ReallyDamaged->Rubble(0)
        var health = new float[script.Length];

        for (var i = 0; i < script.Length; i++)
        {
            if (i == roundTripBeforeStep)
            {
                PortedModuleTestKit.Load(module, PortedModuleTestKit.Save(module));
            }

            Damage(tower, script[i]);
            health[i] = tower.BodyModule.Health;
        }

        return (
            health,
            events.Events.Select(e => e.FXListName).ToArray(),
            events.ParticleSystems.Select(p => p.ParticleSystemName).ToArray());
    }
}
