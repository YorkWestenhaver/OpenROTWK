// Mocked-game unit tests for the KeepObjectDie port (experiment-round-4 §4.1, DoD item 4):
// one test per INI branch, each [create -> trigger death -> observable effect] through the
// batch's shared death-trigger helper, plus the shadow-copy base test and the mid-behavior
// save/load continuation.
//
// The observable effect for THIS class is an absence: the corpse is still in the world after
// the death. An absence is only evidence if the same test can see its presence, so every
// survival assertion is paired with a DestroyDie control in the same game - if the harness
// were failing to kill anything, or if deaths were not reaching Die modules at all, the
// control would survive too and the pairing fails.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class KeepObjectDieContractTests
{
    private const string Definitions = @"
Object KeepPlain
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = KeepObjectDie ModuleTag_IWantRubble
  End
End

Object KeepNotSuicided
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = KeepObjectDie ModuleTag_IWantRubble
    DeathTypes = ALL -SUICIDED
  End
End

Object KeepExemptSold
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = KeepObjectDie ModuleTag_IWantRubble
    ExemptStatus = SOLD
  End
End

Object KeepWithBfme2Fields
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = KeepObjectDie ModuleTag_IWantRubble
    CollapsingTime = 10000
    StayOnRadar = Yes
  End
End

Object KeepAndDestroy
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = KeepObjectDie ModuleTag_IWantRubble
  End
  Behavior = DestroyDie ModuleTag_Destroy
  End
End

Object DestroyControl
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DestroyDie ModuleTag_Destroy
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0FFEEu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static KeepObjectDie ModuleOf(GameObject gameObject) =>
        gameObject.FindBehavior<KeepObjectDie>();

    private static bool StillInWorld(HeadlessSimGame game, GameObject gameObject) =>
        game.GameLogic.Objects.Any(o => ReferenceEquals(o, gameObject));

    // ---- branch: no INI fields at all (the bare "Behavior = KeepObjectDie" form) ----

    [Fact]
    public void PlainKeep_DeathLeavesTheObjectInTheWorld()
    {
        var game = NewGame();
        var kept = game.SpawnObject("KeepPlain", game.CivilianPlayer, Vector3.Zero);
        var control = game.SpawnObject("DestroyControl", game.CivilianPlayer, new Vector3(40, 0, 0));

        PortedModuleTestKit.TriggerDeath(kept);
        PortedModuleTestKit.TriggerDeath(control);
        game.Step();

        // The control proves the deaths really reached the Die modules...
        Assert.True(control.IsDestroyed);
        Assert.False(StillInWorld(game, control));

        // ...and the kept object survived its own, identical death.
        Assert.False(kept.IsDestroyed);
        Assert.True(StillInWorld(game, kept));
        Assert.True(kept.IsEffectivelyDead);       // dead, but still a thing in the world
    }

    // ---- branch: DeathTypes filter. Census of the AotR corpus, counted per block rather
    // than by a grep window (a window bleeds into the neighbouring module and reports the
    // opposite polarity): 375 KeepObjectDie blocks - 312 bare, 46 DeathTypes = ALL -SUICIDED,
    // 15 StayOnRadar = Yes, 2 CollapsingTime = 10000. ALL -SUICIDED is the only DeathTypes
    // form the corpus writes, so it is the form tested here. ----

    [Fact]
    public void DeathTypesFilter_ObjectSurvivesWhetherOrNotTheFilterAdmitsTheDeath()
    {
        // KeepObjectDie is a no-op on both sides of its own filter: an admitted death runs
        // Die() (which does nothing) and a rejected death returns early (which also does
        // nothing). This test pins that equivalence rather than pretending to observe the
        // branch - the filter itself is DieLogicData's, covered by DeathTriggerKitTests.
        var game = NewGame();
        var admitted = game.SpawnObject("KeepNotSuicided", game.CivilianPlayer, Vector3.Zero);
        var rejected = game.SpawnObject("KeepNotSuicided", game.CivilianPlayer, new Vector3(40, 0, 0));

        var admittedCrcBefore = PortedModuleTestKit.LiveCrc(ModuleOf(admitted));

        // ALL -SUICIDED: a Normal death is admitted, a Suicided one is filtered out.
        PortedModuleTestKit.TriggerDeath(admitted, DeathType.Normal);
        PortedModuleTestKit.TriggerDeath(rejected, DeathType.Suicided);
        game.Step();

        Assert.True(StillInWorld(game, admitted));
        Assert.True(StillInWorld(game, rejected));
        Assert.False(admitted.IsDestroyed);
        Assert.False(rejected.IsDestroyed);

        // And the admitted death left no state behind: the module's CRC is what it was.
        Assert.Equal(admittedCrcBefore, PortedModuleTestKit.LiveCrc(ModuleOf(admitted)));
        Assert.Equal(
            PortedModuleTestKit.LiveCrc(ModuleOf(admitted)),
            PortedModuleTestKit.LiveCrc(ModuleOf(rejected)));
    }

    // ---- branch: ExemptStatus. NOT written by any AotR KeepObjectDie block; kept because
    // it is the other half of the inherited DieLogicData filter and costs one object. ----

    [Fact]
    public void ExemptStatus_SoldObjectStillSurvivesItsDeath()
    {
        var game = NewGame();
        var sold = game.SpawnObject("KeepExemptSold", game.CivilianPlayer, Vector3.Zero);
        sold.SetObjectStatus(ObjectStatus.Sold, true);

        PortedModuleTestKit.TriggerDeath(sold);
        game.Step();

        // ExemptStatus made the module decline the death; since declining and accepting are
        // the same no-op, the corpse stays either way.
        Assert.True(StillInWorld(game, sold));
        Assert.False(sold.IsDestroyed);
    }

    // ---- branch: the two BFME2-only fields (parsed, deliberately unconsumed) ----

    [Fact]
    public void Bfme2Fields_AreParsedAndChangeNoBehavior()
    {
        var game = NewGame();
        var plain = game.SpawnObject("KeepPlain", game.CivilianPlayer, Vector3.Zero);
        var decorated = game.SpawnObject("KeepWithBfme2Fields", game.CivilianPlayer, new Vector3(40, 0, 0));

        var data = (KeepObjectDieModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("KeepWithBfme2Fields")
            .Behaviors["ModuleTag_IWantRubble"].Data;

        // Parsed: the audited vocabulary reached the flyweight...
        Assert.Equal(10000, data.CollapsingTime);
        Assert.True(data.StayOnRadar);

        // ...and consumed by nothing: the death outcome is byte-for-byte the plain one.
        PortedModuleTestKit.TriggerDeath(plain);
        PortedModuleTestKit.TriggerDeath(decorated);
        game.Step();

        Assert.True(StillInWorld(game, plain));
        Assert.True(StillInWorld(game, decorated));
        Assert.Equal(
            PortedModuleTestKit.Save(ModuleOf(plain)),
            PortedModuleTestKit.Save(ModuleOf(decorated)));
    }

    // ---- composition: KeepObjectDie does not VETO other Die modules ----

    [Fact]
    public void KeepObjectDie_DoesNotVetoADestroyingDieModuleOnTheSameObject()
    {
        // Worth pinning because the class name invites the opposite reading. GPL's onDie has
        // no veto path and Object::onDie runs every IDieModule in ModuleIndex order, so an
        // object carrying both modules is destroyed: "keep" means "this module does not
        // remove you", never "nothing may remove you".
        var game = NewGame();
        var both = game.SpawnObject("KeepAndDestroy", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(both);
        game.Step();

        Assert.True(both.IsDestroyed);
        Assert.False(StillInWorld(game, both));
    }

    // ---- the shadow-copy base test, mid-behavior ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var kept = game.SpawnObject("KeepNotSuicided", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(kept);

        // "Mid-behavior" for a Die module is after its behavior has happened: damage, the
        // death itself, and frames of the corpse continuing to exist.
        PortedModuleTestKit.ApplyDamage(kept, amount: 30f);
        game.Step();
        PortedModuleTestKit.TriggerDeath(kept, DeathType.Normal);
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("KeepNotSuicided", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    // ---- mid-behavior save/load continuation ----

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        // Two identical games, identical script; game B round-trips the module through
        // Save->Load on the frame of the death. A load path that lost the version tag, or a
        // walk that read a different number of bytes than it wrote, diverges here.
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 3);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    /// <summary>
    /// Records, per frame, the facts a KeepObjectDie regression would move: whether the
    /// corpse is still in the world, whether the engine considers it destroyed, and the
    /// module's own CRC. The death lands on frame 3, so the round-trip point is mid-death.
    /// </summary>
    private static (bool InWorld, bool Destroyed, uint Crc)[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame();
        var kept = game.SpawnObject("KeepNotSuicided", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(kept);

        var trajectory = new (bool, bool, uint)[8];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == 3)
            {
                PortedModuleTestKit.TriggerDeath(kept, DeathType.Normal);
            }

            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                PortedModuleTestKit.Load(module, state);
            }

            game.Step();
            trajectory[i] = (StillInWorld(game, kept), kept.IsDestroyed, PortedModuleTestKit.LiveCrc(module));
        }

        return trajectory;
    }
}
