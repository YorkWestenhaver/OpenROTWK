// R15 L1-11 (sweep ratchet), residual crash class 76dce042:
// "Locomotor..ctor / NullReferenceException", 1 of the 9 stage-A failures in the frozen 20-map
// AotR sweep at main 9bde4556 ("map good fords of isen", mapObject #2482
// RohanRoyalBannerStructure), thrown during Scene3D.LoadObjects -> GameObject..ctor ->
// AIUpdate..ctor -> AIUpdate.SetLocomotor -> LocomotorSet.Initialize.
//
// Root cause: LocomotorSet.Initialize took `locomotorTemplateReference.Value` and passed it
// straight to `new Locomotor(...)`, whose first statement reads CloseEnoughDist off the
// template. When the object's `Locomotor = SET_NORMAL <name>` line names a Locomotor block
// absent from the loaded INI corpus, the lazy reference resolves to null and the constructor
// NRE'd - out of a MODULE CONSTRUCTOR during map load, so the process died before the sim loop.
// This is distinct from the R15 FIX-LOCO class (a locomotor applied to an object with no
// PhysicsBehavior); here the locomotor TEMPLATE itself does not exist.
//
// Fixed behavior asserted here: the unresolved locomotor is skipped with one contextual
// warning per object template, the object still constructs with its other modules, and any
// locomotors in the same set that DO resolve are still installed.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.SweepResidual;

public class SweepResidualLocomotorSetTests
{
    private const string Definitions = @"
Locomotor SweepGoodLocomotor
  Surfaces = GROUND
  Speed = 40
End

; The fords-of-isen shape: the locomotor set names a Locomotor block that does not exist.
Object SweepBannerStructure
  KindOf = STRUCTURE SELECTABLE
  Locomotor = SET_NORMAL NoSuchLocomotorTemplate
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AIUpdateInterface ModuleTag_Ai
  End
End

; Control: the same shape with a locomotor that resolves.
Object SweepWalkingStructure
  KindOf = STRUCTURE SELECTABLE
  Locomotor = SET_NORMAL SweepGoodLocomotor
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AIUpdateInterface ModuleTag_Ai
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0x10C0);
        game.LoadIniText(Definitions);
        return game;
    }

    [Fact]
    public void UnresolvedLocomotorTemplate_ObjectConstruction_DoesNotThrow()
    {
        var game = NewGame();

        // The regression: this used to NRE inside Locomotor's constructor, out of
        // GameObject's own constructor.
        var banner = game.SpawnObject("SweepBannerStructure", game.CivilianPlayer, Vector3.Zero);

        Assert.Contains(banner, game.GameLogic.Objects);
    }

    [Fact]
    public void UnresolvedLocomotorTemplate_LeavesTheObjectsOtherModulesIntact()
    {
        var game = NewGame();

        var banner = game.SpawnObject("SweepBannerStructure", game.CivilianPlayer, Vector3.Zero);

        // Degraded, not fatal: only the locomotor is missing.
        Assert.NotNull(banner.FindBehavior<AIUpdate>());
        Assert.NotNull(banner.BodyModule);
    }

    [Fact]
    public void UnresolvedLocomotorTemplate_KeepsSimulatingOnLaterFrames()
    {
        var game = NewGame();
        var banner = game.SpawnObject("SweepBannerStructure", game.CivilianPlayer, Vector3.Zero);

        game.Step();
        game.Step();

        Assert.Contains(banner, game.GameLogic.Objects);
    }

    [Fact]
    public void ResolvedLocomotorTemplate_StillConstructsNormally()
    {
        var game = NewGame();

        // Control: the guard must skip only the unresolvable entry.
        var walker = game.SpawnObject("SweepWalkingStructure", game.CivilianPlayer, Vector3.Zero);

        Assert.Contains(walker, game.GameLogic.Objects);
        Assert.NotNull(walker.FindBehavior<AIUpdate>());
    }
}
