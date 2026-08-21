// HordeTransportContainDamage R13 contract tests (task packet modules-r13/specs/
// HordeTransportContainDamageModuleData.md §3).
//
// SEQUENCING GAP (spec §5, carried into the tests; UPDATE: the sibling HordeTransportContain
// module has since landed its own runtime port - see HordeTransportContain.cs - so this is now
// a follow-up port task, not a compile-time blocker). At the time of this port, HordeTransportContain
// was still [ParseOnly] and had no runtime class - there was nothing this module's OnDamage could
// call FindBehavior<HordeTransportContain>() against, so the
// numbered damage-propagation cases in the spec (§3.1-§3.8) are NOT written here as running
// [Fact]s; they remain a recorded [create -> tick -> observable] contract in the spec itself,
// to be turned into real tests once HordeTransportContainDamage.OnDamage is implemented against
// the now-landed sibling (spec's own framing: "a sequencing dependency, not a missing fact").
//
// What IS tested here, real and running today: the module parses, instantiates, and wires
// cleanly (ParseOnly removal + CreateModule), its OnDamage dispatch is a safe no-op (matches
// its currently-unimplemented body - see the source file header), and its version-only Xfer
// walk round-trips (shadow-copy CRC + save/load), per api-freeze-v1 §6's "every porting task
// clones its structure" rule.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Damage;

public class HordeTransportContainDamageContractTests
{
    private const string Definitions = @"
Object Mumakil
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = HordeTransportContainDamage ModuleTag_ContainDamage
  End
End

Object Passenger
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x7A0D)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2Rotwk, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static HordeTransportContainDamage ContainDamageOf(GameObject obj) =>
        Assert.IsType<HordeTransportContainDamage>(obj.FindBehavior<HordeTransportContainDamage>());

    // ---- instantiation (spec §3.1's create-time half; no Update() to observe - see §2) ----

    [Fact]
    public void Instantiation_ParsesAndWiresCleanly()
    {
        var game = NewGame();
        var transport = game.SpawnObject("Mumakil", game.CivilianPlayer, Vector3.Zero);

        var module = ContainDamageOf(transport);
        Assert.NotNull(module);
        Assert.True(module.HasSimXfer);
    }

    // ---- OnDamage dispatch: safe no-op today (spec §5 sequencing gap) ----

    [Fact]
    public void ContainerDamage_WithNoSiblingContainModule_DoesNotThrow_AndDoesNotAffectTransportHealth()
    {
        // No HordeTransportContain sibling is parsed on Mumakil above (it is still
        // [ParseOnly], see the source file header). ActiveBody still dispatches
        // IDamageModule.OnDamage unconditionally to every module found via
        // FindBehaviors<IDamageModule>() (per the spec's own read of ActiveBody.cs:355-360);
        // this pins down that the dispatch itself is harmless against today's unimplemented
        // body, independent of whether a sibling exists.
        var game = NewGame();
        var transport = game.SpawnObject("Mumakil", game.CivilianPlayer, Vector3.Zero);

        var result = PortedModuleTestKit.ApplyDamage(transport, 20f);

        Assert.Equal(480f, result.HealthAfter);
    }

    [Fact]
    public void ContainerDamage_DoesNotAffectUnseatedNearbyObjects()
    {
        // With OnDamage currently a no-op (sequencing gap, spec §5), a bystander object must
        // be completely unaffected by damage applied to the transport - pins down that no
        // implicit/global effect leaks out of this module today.
        var game = NewGame();
        var transport = game.SpawnObject("Mumakil", game.CivilianPlayer, Vector3.Zero);
        var bystander = game.SpawnObject("Passenger", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.ApplyDamage(transport, 20f);

        Assert.Equal(100f, bystander.BodyModule.Health);
    }

    // ---- shared base tests (api-freeze-v1 §6) ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var game = NewGame();
        var live = ContainDamageOf(game.SpawnObject("Mumakil", game.CivilianPlayer, Vector3.Zero));
        var shadow = ContainDamageOf(game.SpawnObject("Mumakil", game.CivilianPlayer, new Vector3(50, 0, 0)));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var game = NewGame();
        var live = ContainDamageOf(game.SpawnObject("Mumakil", game.CivilianPlayer, Vector3.Zero));

        var state = PortedModuleTestKit.Save(live);
        var restored = ContainDamageOf(game.SpawnObject("Mumakil", game.CivilianPlayer, Vector3.Zero));
        PortedModuleTestKit.Load(restored, state);

        Assert.Equal(PortedModuleTestKit.LiveCrc(live), PortedModuleTestKit.LiveCrc(restored));
        Assert.Equal(state, PortedModuleTestKit.Save(restored));
    }
}
