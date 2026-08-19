// Mocked-game contract tests for the LargeGroupBonusUpdate port (R11 Track B): the
// periodic group census (template-name filter, AlliesOnly), the threshold edge in both
// directions, and the shadow-copy base test.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class LargeGroupBonusUpdateContractTests
{
    // UpdateRate 1000 ms -> 5 frames at the frozen 5 Hz; Count 3 within Radius 100.
    private const string Definitions = @"
ModifierList GroupBuff
  Category = LEADERSHIP
  Modifier = ARMOR 25%
End

Object SwarmGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object OtherGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object SwarmLeader
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = LargeGroupBonusUpdate ModuleTag_GroupBonus
    UpdateRate = 1000
    HordeMemberFilter = NONE +SwarmGrunt
    Count = 3
    Radius = 100
    RubOffRadius = 100
    AlliesOnly = Yes
    AttributeModifier = GroupBuff
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0x960);
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

    [Fact]
    public void BelowCount_NoBonus()
    {
        var game = NewGame();
        var leader = game.SpawnObject("SwarmLeader", game.CivilianPlayer, new Vector3(100, 100, 0));
        game.SpawnObject("SwarmGrunt", game.CivilianPlayer, new Vector3(110, 100, 0));
        game.SpawnObject("SwarmGrunt", game.CivilianPlayer, new Vector3(120, 100, 0));

        StepFrames(game, 6);

        Assert.False(leader.HasAttributeModifier("GroupBuff"));
        Assert.False(leader.BehaviorModules.OfType<LargeGroupBonusUpdate>().Single().BonusActive);
    }

    [Fact]
    public void AtCount_BonusApplies_TemplateNameFilterMatches()
    {
        var game = NewGame();
        var leader = game.SpawnObject("SwarmLeader", game.CivilianPlayer, new Vector3(100, 100, 0));
        for (var i = 0; i < 3; i++)
        {
            game.SpawnObject("SwarmGrunt", game.CivilianPlayer, new Vector3(110 + i * 10, 100, 0));
        }
        // Wrong template: never counted (the filter is NONE +SwarmGrunt).
        game.SpawnObject("OtherGrunt", game.CivilianPlayer, new Vector3(105, 100, 0));

        StepFrames(game, 6);

        Assert.True(leader.HasAttributeModifier("GroupBuff"));
    }

    [Fact]
    public void OutOfRadiusMembers_DoNotCount()
    {
        var game = NewGame();
        var leader = game.SpawnObject("SwarmLeader", game.CivilianPlayer, new Vector3(100, 100, 0));
        game.SpawnObject("SwarmGrunt", game.CivilianPlayer, new Vector3(110, 100, 0));
        game.SpawnObject("SwarmGrunt", game.CivilianPlayer, new Vector3(120, 100, 0));
        // 200 units away: outside the 100 radius.
        game.SpawnObject("SwarmGrunt", game.CivilianPlayer, new Vector3(300, 100, 0));

        StepFrames(game, 6);

        Assert.False(leader.HasAttributeModifier("GroupBuff"));
    }

    [Fact]
    public void FallingBelowCount_RemovesTheBonus()
    {
        var game = NewGame();
        var leader = game.SpawnObject("SwarmLeader", game.CivilianPlayer, new Vector3(100, 100, 0));
        var grunts = new GameObject[3];
        for (var i = 0; i < 3; i++)
        {
            grunts[i] = game.SpawnObject("SwarmGrunt", game.CivilianPlayer, new Vector3(110 + i * 10, 100, 0));
        }
        StepFrames(game, 6);
        Assert.True(leader.HasAttributeModifier("GroupBuff"));

        grunts[0].Kill();
        StepFrames(game, 6);

        Assert.False(leader.HasAttributeModifier("GroupBuff"));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("SwarmLeader", game.CivilianPlayer, new Vector3(100, 100, 0));
        for (var i = 0; i < 3; i++)
        {
            game.SpawnObject("SwarmGrunt", game.CivilianPlayer, new Vector3(110 + i * 10, 100, 0));
        }
        StepFrames(game, 6);
        var live = liveHost.BehaviorModules.OfType<LargeGroupBonusUpdate>().Single();
        Assert.True(live.BonusActive);

        var shadowHost = game.SpawnObject("SwarmLeader", game.CivilianPlayer, new Vector3(400, 400, 0));
        var shadow = shadowHost.BehaviorModules.OfType<LargeGroupBonusUpdate>().Single();

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
