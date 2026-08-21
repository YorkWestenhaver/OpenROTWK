// Mocked-game contract tests for the FireOCLAfterWeaponCooldownUpdate port (R12): the
// module parses every INI-configurable field and instantiates as a live (parked-for-now)
// runtime module, and round-trips its empty state - the [ParseOnly] hole is closed without
// inventing a firing signal the frozen ISimContext does not yet expose (see the module
// header for why: no current-weapon / weapon-slot / last-shot-frame surface exists yet).
//
// The packet's testCases (T1-T6, all about shot counting / OCL firing / lifetime scaling)
// cannot be exercised: they all require observing "current weapon" and "last shot frame",
// which the frozen contract does not expose to [SimState] module code. What IS tested here
// is the part that genuinely ported: every field parses to the audited value, the module is
// live (not [ParseOnly]), stepping is harmless, and the Xfer walk round-trips.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class FireOCLAfterWeaponCooldownUpdateContractTests
{
    private const string Definitions = @"
ObjectCreationList OCL_Smoke
  CreateObject
    ObjectNames = SmokePuff
    Count = 1
  End
End

Object SmokePuff
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End

Object Turret
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FireOCLAfterWeaponCooldownUpdate ModuleTag_FireOCL
    WeaponSlot = SECONDARY
    TriggeredBy = Upgrade_Overcharge
    ConflictsWith = Upgrade_Silencer
    OCL = OCL_Smoke
    MinShotsToCreateOCL = 3
    OCLLifetimePerSecond = 500
    OCLLifetimeMaxCap = 2000
  End
End
";

    private static (HeadlessSimGame Game, GameObject Unit) Spawn()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xF12E);
        game.LoadIniText(Definitions);
        return (game, game.SpawnObject("Turret", game.CivilianPlayer, Vector3.Zero));
    }

    [Fact]
    public void ParsesAndCreatesRuntimeModule()
    {
        var (game, unit) = Spawn();

        var module = unit.BehaviorModules.OfType<FireOCLAfterWeaponCooldownUpdate>().Single();
        Assert.NotNull(module);

        var data = (FireOCLAfterWeaponCooldownUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("Turret").Behaviors["ModuleTag_FireOCL"].Data;

        Assert.Equal(WeaponSlot.Secondary, data.WeaponSlot);
        Assert.Equal(new[] { "Upgrade_Overcharge" }, data.TriggeredBy);
        Assert.Equal(new[] { "Upgrade_Silencer" }, data.ConflictsWith);
        Assert.Equal("OCL_Smoke", data.OCL.Value.Name);
        Assert.Equal(3, data.MinShotsToCreateOCL);
        Assert.Equal(500, data.OCLLifetimePerSecond);
        // OCLLifetimeMaxCap = 2000 ms, quantized at parse to logic frames (BFME2: 5 fps).
        Assert.Equal(new LogicFrameSpan(10), data.OCLLifetimeMaxCap);
    }

    [Fact]
    public void SteppingIsHarmless_ModuleStaysParked()
    {
        var (game, unit) = Spawn();
        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }
        Assert.False(unit.IsDestroyed);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var (game, unit) = Spawn();
        var live = unit.BehaviorModules.OfType<FireOCLAfterWeaponCooldownUpdate>().Single();

        var shadowHost = game.SpawnObject("Turret", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = shadowHost.BehaviorModules.OfType<FireOCLAfterWeaponCooldownUpdate>().Single();

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var (game, unit) = Spawn();
        var module = unit.BehaviorModules.OfType<FireOCLAfterWeaponCooldownUpdate>().Single();

        game.Step();
        var saved = PortedModuleTestKit.Save(module);

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        // Nothing to diverge - the module carries no mutable state - but Load must still
        // succeed and leave the module harmlessly parked.
        PortedModuleTestKit.Load(module, saved);
        game.Step();

        Assert.False(unit.IsDestroyed);
    }
}
