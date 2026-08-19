// Mocked-game contract tests for the StancesBehavior port (R11 Track B): stance-set
// resolution from the StanceTemplate asset, the deterministic stance switch, and the
// shadow-copy base test. Definitions parse from INI text through the real parser.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class StancesBehaviorContractTests
{
    private const string Definitions = @"
StanceTemplate TestStances
  Stance = Aggressive
  End
  Stance = Battle
  End
  Stance = HoldGround
  End
End

Object StancedUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = StancesBehavior ModuleTag_Stances
    StanceTemplate = TestStances
  End
End
";

    private static (HeadlessSimGame Game, StancesBehavior Module) Spawn()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0x57A);
        game.LoadIniText(Definitions);
        var unit = game.SpawnObject("StancedUnit", game.CivilianPlayer, Vector3.Zero);
        return (game, unit.BehaviorModules.OfType<StancesBehavior>().Single());
    }

    [Fact]
    public void SpawnsWithTemplateResolved_FirstStanceCurrent()
    {
        var (_, module) = Spawn();

        Assert.Equal(3, module.StanceCount);
        Assert.Equal("Aggressive", module.CurrentStanceName);
    }

    [Fact]
    public void SetStance_SwitchesByName_UnknownIgnored()
    {
        var (_, module) = Spawn();

        Assert.True(module.SetStance("HoldGround"));
        Assert.Equal("HoldGround", module.CurrentStanceName);

        // Unknown names change nothing; re-setting the current stance reports no change.
        Assert.False(module.SetStance("NoSuchStance"));
        Assert.Equal("HoldGround", module.CurrentStanceName);
        Assert.False(module.SetStance("HoldGround"));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var (game, live) = Spawn();
        live.SetStance("Battle");

        var shadowHost = game.SpawnObject("StancedUnit", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = shadowHost.BehaviorModules.OfType<StancesBehavior>().Single();

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
        Assert.Equal("Battle", shadow.CurrentStanceName);
    }
}
