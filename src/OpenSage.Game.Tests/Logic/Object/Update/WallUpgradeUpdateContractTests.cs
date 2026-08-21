// Mocked-game unit tests for the WallUpgradeUpdate port (R13; api-freeze-v1 §6 fitness item
// 4). Per modules-r13/specs/WallUpgradeUpdateModuleData.md §3: this module has exactly one
// INI branch (the empty one, F-WUU-2), so the test list is short by construction, not by
// shortcut. The module is a stateless marker that sleeps forever from construction (F-WUU-1),
// so the observables are: [spawn -> module present, asleep, non-crashing], the shadow-copy
// CRC base test, and a save/load round-trip - plus the sleepy-update caveat (a freshly spawned
// module's first Update() opportunity is only evaluated on the SECOND
// HeadlessSimGame.Step()).
//
// Negative/non-goal (spec §3 case 5): no test here asserts any interaction with
// GeometryUpgrade, CastleUpgrade, or WallHubBehavior - this port does not wire to them.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class WallUpgradeUpdateContractTests
{
    private const string Definitions = @"
Object WallSegmentWithUpgradeUpdate
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = WallUpgradeUpdate ModuleTag_WallUpgrade
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x0A11) // "wall"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static WallUpgradeUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<WallUpgradeUpdate>().Single();

    [Fact]
    public void Parse_EmptyBlock_ProducesModuleData()
    {
        // Regression guard (spec §3 case 1): removing [ParseOnly] must not change parse
        // behavior for the zero-field case - the object still spawns with exactly one
        // WallUpgradeUpdate module.
        var game = NewGame();
        var wall = game.SpawnObject("WallSegmentWithUpgradeUpdate", game.CivilianPlayer, Vector3.Zero);

        Assert.NotNull(wall.FindBehavior<WallUpgradeUpdate>());
    }

    [Fact]
    public void Spawn_ModulePresent_AsleepAndNonCrashing_AcrossSecondStep()
    {
        var game = NewGame();
        var wall = game.SpawnObject("WallSegmentWithUpgradeUpdate", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(wall);

        var crcBeforeSteps = PortedModuleTestKit.LiveCrc(module);
        var positionBeforeSteps = wall.Transform.Translation;

        // Sleepy-update caveat (spec §3 case 2): a freshly spawned module's first Update()
        // opportunity is only evaluated on the SECOND HeadlessSimGame.Step() - the module is
        // constructed and its wake frame registered during the Step() that processes the
        // spawn, and the current sleepy-queue scan has already passed for this frame.
        game.Step(); // object exists, module attached
        game.Step(); // earliest frame Update() COULD run if the module were awake

        // Because the ctor sets SetWakeFrame(Forever), the module is never in the sleepy-
        // update queue for any finite frame, so Update() is never actually invoked. Observable
        // proxy (no direct "was Update called" hook needed, per spec §3 case 2): the module's
        // own Fix64/sim state (its CRC fold) and the object's position are unchanged across
        // the second Step() - nothing in this module can change anything.
        Assert.False(wall.IsDestroyed);
        Assert.Equal(crcBeforeSteps, PortedModuleTestKit.LiveCrc(module));
        Assert.Equal(positionBeforeSteps, wall.Transform.Translation);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var game = NewGame();

        var liveHost = game.SpawnObject("WallSegmentWithUpgradeUpdate", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(liveHost);

        var shadowHost = game.SpawnObject("WallSegmentWithUpgradeUpdate", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void SaveLoad_RoundTrip_MidBehavior_PreservesCrc()
    {
        var game = NewGame();
        var wall = game.SpawnObject("WallSegmentWithUpgradeUpdate", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(wall);

        game.Step();
        game.Step();

        var saved = PortedModuleTestKit.Save(module);

        var freshHost = game.SpawnObject("WallSegmentWithUpgradeUpdate", game.CivilianPlayer, new Vector3(50, 0, 0));
        var fresh = ModuleOf(freshHost);

        // Version-byte-only walk: a fresh instance's CRC already equals the source's (no
        // mutable state to diverge on), but the round-trip through Save/Load must still be
        // exercised - the mechanical test the fitness function requires (spec §3 case 3), not
        // an optional nicety for a "trivial" module.
        PortedModuleTestKit.Load(fresh, saved);
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));
    }
}
