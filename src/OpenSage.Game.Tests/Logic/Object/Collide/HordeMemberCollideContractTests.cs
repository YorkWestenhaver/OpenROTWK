// Mocked-game contract tests for the HordeMemberCollide port (R11 Track B): the REAL INI
// name (the empty member-side block authored data ships, e.g. AotR MordorFighter) must
// produce the live SimHordeMember runtime - horde back-reference, damage forwarding into
// the horde flank path - plus the shadow-copy base test.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Horde;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Collide;

public class HordeMemberCollideContractTests
{
    // A 2-slot horde whose members carry the REAL "HordeMemberCollide" block.
    private const string Definitions = @"
Locomotor TestHordeLoco
  Surfaces = GROUND
  Speed = 30
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Object CollideGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = HordeMemberCollide ModuleTag_HordeMemberCollide
  End
  Locomotor = SET_NORMAL TestHordeLoco
End

Object CollideHorde
  KindOf = HORDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = SimHordeContain ModuleTag_Contain
    InitialPayload = CollideGrunt 2
    Slots = 2
    RankInfo = RankNumber:1 UnitType:CollideGrunt Position:X:10 Y:-10 Position:X:10 Y:10
  End
  Locomotor = SET_NORMAL TestHordeLoco
End
";

    private static (HeadlessSimGame Game, GameObject Horde, SimHordeContain Contain) SpawnHorde()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0x9EED);
        game.LoadIniText(Definitions);
        var horde = game.SpawnObject("CollideHorde", game.CivilianPlayer, new Vector3(100, 100, 0));
        for (var i = 0; i < 2; i++)
        {
            game.Step();
        }
        return (game, horde, horde.FindBehavior<SimHordeContain>());
    }

    [Fact]
    public void RealIniName_CreatesSimHordeMemberRuntime()
    {
        var (game, horde, contain) = SpawnHorde();
        Assert.Equal(2, contain.MemberCount);

        foreach (var id in contain.MemberIds)
        {
            var member = game.GameLogic.GetObjectById(id);
            var runtime = member.FindBehavior<SimHordeMember>();
            Assert.NotNull(runtime);
            // Seated by the horde: the back-reference points at the owning horde object.
            Assert.Equal(horde.Id, runtime.HordeId);
            Assert.Same(contain, runtime.GetHorde());
        }
    }

    [Fact]
    public void MemberDamage_ForwardsToOwningHorde()
    {
        var (game, _, contain) = SpawnHorde();
        var enemy = game.SpawnObject("CollideGrunt", game.PlayerManager.NeutralPlayer, new Vector3(160, 100, 0));

        GameObject firstMember = null;
        foreach (var id in contain.MemberIds)
        {
            firstMember = game.GameLogic.GetObjectById(id);
            break;
        }

        // Sub-lethal damage with a live attacker routes OnDamage -> horde flank path
        // without throwing; the member survives and stays seated.
        var result = PortedModuleTestKit.ApplyDamage(firstMember, 10f, source: enemy);
        Assert.False(result.Died);
        Assert.Equal(2, contain.MemberCount);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var (gameA, _, containA) = SpawnHorde();
        GameObject liveMember = null;
        foreach (var id in containA.MemberIds)
        {
            liveMember = gameA.GameLogic.GetObjectById(id);
            break;
        }
        var live = liveMember.FindBehavior<SimHordeMember>();

        // A fresh, never-seated shadow in a second game.
        var (gameB, _, _) = SpawnHorde();
        var shadowMember = gameB.SpawnObject("CollideGrunt", gameB.CivilianPlayer, new Vector3(0, 0, 0));
        var shadow = shadowMember.FindBehavior<SimHordeMember>();
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
