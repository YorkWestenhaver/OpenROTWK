// R7 Body-batch port: HiveStructureBody redirect-to-slave/rider damage semantics, on
// HeadlessSimGame with real parsed INI so the ParseEnumBitArray damage-type flags and the
// real SpawnBehavior spawn path are on the tested path. One test per GPL branch of
// HiveStructureBody::attemptDamage, plus the shadow-copy base test and a mid-state
// save/load continuation (damage-to-health application).

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Body;

public class HiveStructureBodyContractTests
{
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Object HiveSlave
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object Shooter
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object TestHive
  KindOf = STRUCTURE IMMOBILE
  Geometry = BOX
  GeometryMajorRadius = 20
  GeometryMinorRadius = 20
  GeometryHeight = 20
  Body = HiveStructureBody ModuleTag_Body
    MaxHealth = 500
    InitialHealth = 500
    PropagateDamageTypesToSlavesWhenExisting = SLASH PIERCE
    SwallowDamageTypesIfSlavesNotExisting = PIERCE
  End
  Behavior = SpawnBehavior ModuleTag_Spawn
    SpawnNumber = 2
    SpawnTemplateName = HiveSlave
  End
  Behavior = GarrisonContain ModuleTag_Contain
    ContainMax = 10
    AllowInsideKindOf = INFANTRY
  End
End

Object NoSpawnHive
  KindOf = STRUCTURE IMMOBILE
  Geometry = BOX
  GeometryMajorRadius = 20
  GeometryMinorRadius = 20
  GeometryHeight = 20
  Body = HiveStructureBody ModuleTag_Body
    MaxHealth = 500
    InitialHealth = 500
    PropagateDamageTypesToSlavesWhenExisting = SLASH
    SwallowDamageTypesIfSlavesNotExisting = SLASH
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0FFEEu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definition, float x = 0, float y = 0)
        => game.SpawnObject(definition, game.CivilianPlayer, new Vector3(x, y, 0));

    private static HiveStructureBody HiveOf(GameObject gameObject)
        => Assert.IsType<HiveStructureBody>(gameObject.BodyModule);

    private static ActiveBody BodyOf(GameObject gameObject)
        => Assert.IsType<ActiveBody>(gameObject.BodyModule, exactMatch: false);

    private static Fix64 Fix(int value) => new(value);

    private static DamageInfoInput Damage(
        float amount,
        DamageType type,
        GameObject source)
        => new(source)
        {
            DamageType = type,
            Amount = amount,
        };

    /// <summary>Drives the hive's real SpawnBehavior so its slave list is populated, then
    /// returns the spawned slaves (the objects this hive created), in ascending id.</summary>
    private static GameObject[] SpawnSlaves(HeadlessSimGame game, GameObject hive)
    {
        hive.FindBehavior<SpawnBehavior>().SpawnInitial();
        return game.GameLogic.Objects
            .Where(o => o.CreatedByObjectID == hive.Id)
            .OrderBy(o => o.Id.Index)
            .ToArray();
    }

    private static void MoveTo(GameObject o, float x, float y)
    {
        o.UpdateTransform(new Vector3(x, y, 0));
        o.UpdateColliders();
    }

    // ================================================================
    // ModuleData audit
    // ================================================================

    [Fact]
    public void ModuleData_ParsesPropagateAndSwallowFlags()
    {
        var game = NewGame();
        var hive = Spawn(game, "TestHive");
        var data = Assert.IsType<HiveStructureBodyModuleData>(hive.Definition.Behaviors["ModuleTag_Body"].Data);

        Assert.True(data.PropagateDamageTypesToSlavesWhenExisting.Get(DamageType.Slash));
        Assert.True(data.PropagateDamageTypesToSlavesWhenExisting.Get(DamageType.Pierce));
        Assert.False(data.PropagateDamageTypesToSlavesWhenExisting.Get(DamageType.Crush));

        Assert.True(data.SwallowDamageTypesIfSlavesNotExisting.Get(DamageType.Pierce));
        Assert.False(data.SwallowDamageTypesIfSlavesNotExisting.Get(DamageType.Slash));
    }

    // ================================================================
    // Redirect to slave (GPL: propagate type + a slave exists)
    // ================================================================

    [Fact]
    public void PropagateType_WithSlave_RedirectsDamageToSlave_HiveUntouched()
    {
        var game = NewGame();
        var hive = Spawn(game, "TestHive", 0, 0);
        var shooter = Spawn(game, "Shooter", 100, 0);
        var slaves = SpawnSlaves(game, hive);
        Assert.Equal(2, slaves.Length);

        // Both slaves start at the hive (0,0), equidistant from the shooter, so the first
        // spawned slave is chosen (GPL keeps the first on a distance tie: strict-less-than).
        var output = hive.AttemptDamage(Damage(40, DamageType.Slash, shooter));

        Assert.Equal(40.0f, output.ActualDamageDealt);
        Assert.Equal(Fix(60), BodyOf(slaves[0]).DamageCore.CurrentHealth);
        Assert.Equal(Fix(100), BodyOf(slaves[1]).DamageCore.CurrentHealth);
        Assert.Equal(Fix(500), HiveOf(hive).DamageCore.CurrentHealth);
    }

    [Fact]
    public void PropagateType_RedirectsToClosestSlaveToShooter()
    {
        var game = NewGame();
        var hive = Spawn(game, "TestHive", 0, 0);
        var shooter = Spawn(game, "Shooter", 1000, 0);
        var slaves = SpawnSlaves(game, hive);
        Assert.Equal(2, slaves.Length);

        // slaves[0] far from the shooter, slaves[1] near it.
        MoveTo(slaves[0], 0, 0);
        MoveTo(slaves[1], 990, 0);

        hive.AttemptDamage(Damage(30, DamageType.Slash, shooter));

        // Only the nearer slave took the hit.
        Assert.Equal(Fix(100), BodyOf(slaves[0]).DamageCore.CurrentHealth);
        Assert.Equal(Fix(70), BodyOf(slaves[1]).DamageCore.CurrentHealth);
        Assert.Equal(Fix(500), HiveOf(hive).DamageCore.CurrentHealth);
    }

    // ================================================================
    // Swallow (GPL: propagate+swallow type, spawner exists but no slaves)
    // ================================================================

    [Fact]
    public void SwallowType_WithSpawnerButNoSlaves_DiscardsDamage_NoEffect()
    {
        var game = NewGame();
        var hive = Spawn(game, "TestHive", 0, 0);
        var shooter = Spawn(game, "Shooter", 100, 0);
        // Deliberately do NOT spawn slaves: the SpawnBehavior exists but its list is empty.

        // PIERCE is both a propagate type AND a swallow type.
        var output = hive.AttemptDamage(Damage(40, DamageType.Pierce, shooter));

        Assert.True(output.NoEffect);
        Assert.Equal(0.0f, output.ActualDamageDealt);
        Assert.Equal(Fix(500), HiveOf(hive).DamageCore.CurrentHealth);
    }

    [Fact]
    public void PropagateOnlyType_WithSpawnerButNoSlaves_DamagesHive()
    {
        var game = NewGame();
        var hive = Spawn(game, "TestHive", 0, 0);
        var shooter = Spawn(game, "Shooter", 100, 0);
        // No slaves spawned; SLASH is a propagate type but NOT a swallow type.

        var output = hive.AttemptDamage(Damage(40, DamageType.Slash, shooter));

        Assert.False(output.NoEffect);
        Assert.Equal(40.0f, output.ActualDamageDealt);
        Assert.Equal(Fix(460), HiveOf(hive).DamageCore.CurrentHealth);
    }

    // ================================================================
    // Fall-through cases (GPL: falls to StructureBody::attemptDamage)
    // ================================================================

    [Fact]
    public void NonPropagateType_AlwaysDamagesHive_EvenWithSlaves()
    {
        var game = NewGame();
        var hive = Spawn(game, "TestHive", 0, 0);
        var shooter = Spawn(game, "Shooter", 100, 0);
        var slaves = SpawnSlaves(game, hive);

        // CRUSH is not in the propagate set: the hive takes it, slaves are untouched.
        hive.AttemptDamage(Damage(50, DamageType.Crush, shooter));

        Assert.Equal(Fix(450), HiveOf(hive).DamageCore.CurrentHealth);
        Assert.All(slaves, s => Assert.Equal(Fix(100), BodyOf(s).DamageCore.CurrentHealth));
    }

    [Fact]
    public void PropagateType_WithNoShooter_DamagesHive()
    {
        var game = NewGame();
        var hive = Spawn(game, "TestHive", 0, 0);
        var slaves = SpawnSlaves(game, hive);

        // Slaves exist, but there is no shooter (invalid source id): GPL falls through and
        // the hive takes the damage.
        var output = hive.AttemptDamage(new DamageInfoInput(null)
        {
            DamageType = DamageType.Slash,
            Amount = 40,
        });

        Assert.Equal(Fix(460), HiveOf(hive).DamageCore.CurrentHealth);
        Assert.All(slaves, s => Assert.Equal(Fix(100), BodyOf(s).DamageCore.CurrentHealth));
    }

    [Fact]
    public void PropagateType_WithNoSpawnBehaviorOrContain_DamagesHive()
    {
        var game = NewGame();
        var hive = Spawn(game, "NoSpawnHive", 0, 0);
        var shooter = Spawn(game, "Shooter", 100, 0);

        // Propagate type, valid shooter, but the hive has neither a SpawnBehavior nor a
        // Contain module (the GPL DEBUG_CRASH data-error case): shipped behavior is to take
        // the damage.
        hive.AttemptDamage(Damage(40, DamageType.Slash, shooter));

        Assert.Equal(Fix(460), HiveOf(hive).DamageCore.CurrentHealth);
    }

    // ================================================================
    // Xfer: shadow copy + mid-state save/load continuation
    // ================================================================

    [Fact]
    public void HasSimXfer_And_ShadowCopyCrcMatches_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game, "TestHive", 0, 0);
        var shadow = Spawn(game, "TestHive", 0, 0);
        var shooter = Spawn(game, "Shooter", 100, 0);

        Assert.True(HiveOf(live).HasSimXfer);

        // Put the live hive mid-behavior (non-propagate damage lands on the hive itself), the
        // shadow differently-stated.
        live.AttemptDamage(Damage(120, DamageType.Crush, shooter));
        shadow.AttemptDamage(Damage(300, DamageType.Crush, shooter));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(HiveOf(live), HiveOf(shadow));
    }

    [Fact]
    public void SaveLoad_ContinuationMatches_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game, "TestHive", 0, 0);
        var shooter = Spawn(game, "Shooter", 100, 0);

        live.AttemptDamage(Damage(150, DamageType.Crush, shooter));
        Assert.Equal(Fix(350), HiveOf(live).DamageCore.CurrentHealth);

        var state = PortedModuleTestKit.Save(HiveOf(live));
        var restoredHost = Spawn(game, "TestHive", 0, 0);
        PortedModuleTestKit.Load(HiveOf(restoredHost), state);

        // An identical follow-up hit produces identical Fix64 health on both.
        live.AttemptDamage(Damage(100, DamageType.Crush, shooter));
        restoredHost.AttemptDamage(Damage(100, DamageType.Crush, shooter));

        Assert.Equal(
            HiveOf(live).DamageCore.CurrentHealth,
            HiveOf(restoredHost).DamageCore.CurrentHealth);
        Assert.Equal(Fix(250), HiveOf(live).DamageCore.CurrentHealth);
    }
}
