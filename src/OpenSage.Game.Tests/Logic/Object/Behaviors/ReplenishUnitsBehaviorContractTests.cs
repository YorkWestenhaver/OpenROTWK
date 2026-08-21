// Mocked-game contract tests for ReplenishUnitsBehavior (api-freeze-v1 §6 fitness item 4):
// the replenish cadence (StartsActive gate + LogicRandom startup stagger + steady ReplenishDelay),
// the enemy-within-radius suppression, ReplenishStatii stamping, the nearby-horde reach, plus the
// shadow-copy base test, a mid-state save/load continuation, and a run-twice determinism check.
//
// Every object definition parses from INI text through the real IniParser, so the audited
// quantizing parse functions (ReplenishDelay ms->frames, the Fix64 radii) are on the tested path.

using System.Collections.Generic;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Horde;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class ReplenishUnitsBehaviorContractTests
{
    // A 4-slot grunt horde (two ranks of two, no banner) that periodically tops itself up.
    // ReplenishDelay 2000 ms -> 10 frames at the frozen 5 Hz; the enemy-suppression radius is 50.
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

Object HordeGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = SimHordeMember ModuleTag_HordeMember
  End
  Locomotor = SET_NORMAL TestHordeLoco
End

Object Enemy
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Locomotor = SET_NORMAL TestHordeLoco
End

Object ReplenishHorde
  KindOf = HORDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = SimHordeContain ModuleTag_Contain
    InitialPayload = HordeGrunt 4
    Slots = 4
    RankInfo = RankNumber:1 UnitType:HordeGrunt Position:X:10 Y:-10 Position:X:10 Y:10
    RankInfo = RankNumber:2 UnitType:HordeGrunt Position:X:25 Y:-10 Position:X:25 Y:10
  End
  Behavior = ReplenishUnitsBehavior ModuleTag_Replenish
    ReplenishDelay = 2000
    NoReplenishIfEnemyWithinRadius = 50
    ReplenishStatii = UNSELECTABLE
    ReplenishFXList = FX_Replenish
    ReplenishHordeMembersOnly = Yes
    StartsActive = Yes
  End
  Locomotor = SET_NORMAL TestHordeLoco
End

Object InactiveReplenishHorde
  KindOf = HORDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = SimHordeContain ModuleTag_Contain
    InitialPayload = HordeGrunt 4
    Slots = 4
    RankInfo = RankNumber:1 UnitType:HordeGrunt Position:X:10 Y:-10 Position:X:10 Y:10
    RankInfo = RankNumber:2 UnitType:HordeGrunt Position:X:25 Y:-10 Position:X:25 Y:10
  End
  Behavior = ReplenishUnitsBehavior ModuleTag_Replenish
    ReplenishDelay = 2000
    StartsActive = No
  End
  Locomotor = SET_NORMAL TestHordeLoco
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x9EED)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static (HeadlessSimGame Game, GameObject Horde, SimHordeContain Contain) SpawnHorde(
        string template = "ReplenishHorde", uint seed = 0x9EED, int warmupFrames = 2)
    {
        var game = NewGame(seed);
        var horde = game.SpawnObject(template, game.CivilianPlayer, new Vector3(100, 100, 0));
        for (var i = 0; i < warmupFrames; i++)
        {
            game.Step();
        }
        return (game, horde, horde.FindBehavior<SimHordeContain>());
    }

    private static List<GameObject> Members(HeadlessSimGame game, SimHordeContain contain)
    {
        var result = new List<GameObject>();
        foreach (var id in contain.MemberIds)
        {
            result.Add(game.GameLogic.GetObjectById(id));
        }
        return result;
    }

    // Kill n members (never the whole horde, which would destroy the horde object), then step
    // once so the horde reaps the dead and frees their slots.
    private static void KillMembers(HeadlessSimGame game, SimHordeContain contain, int n)
    {
        var members = Members(game, contain);
        for (var i = 0; i < n && i < members.Count; i++)
        {
            PortedModuleTestKit.ApplyDamage(members[i], 500f, source: members[i]);
        }
        game.Step();
    }

    private static void MakeEnemies(Player a, Player b)
    {
        a.AddEnemy(b);
        b.AddEnemy(a);
    }

    // ---- item (1): audited parse quantization ----

    [Fact]
    public void ReplenishDelay_ParsesAsLogicFrameSpan_MsCeilToFrames()
    {
        var game = NewGame();
        var data = (ReplenishUnitsBehaviorModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("ReplenishHorde").Behaviors["ModuleTag_Replenish"].Data;

        // 2000 ms at the 5 Hz BFME2 title rate = 10 logic frames (ceil).
        Assert.Equal(10u, data.ReplenishDelay.Value);
    }

    [Fact]
    public void Radii_ParseAsFix64()
    {
        var game = NewGame();
        var data = (ReplenishUnitsBehaviorModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("ReplenishHorde").Behaviors["ModuleTag_Replenish"].Data;

        Assert.Equal(Fix64.FromDecimalLiteral("50"), data.NoReplenishIfEnemyWithinRadius);
        Assert.Equal(ObjectStatus.Unselectable, data.ReplenishStatii);
        Assert.True(data.StartsActive);
        Assert.True(data.ReplenishHordeMembersOnly);
    }

    // ---- the replenish cadence ----

    [Fact]
    public void DeadHordeMembers_AreReplenishedAfterDelay()
    {
        var (game, _, contain) = SpawnHorde();
        Assert.Equal(4, contain.MemberCount);

        KillMembers(game, contain, 2);
        Assert.Equal(2, contain.MemberCount);

        // Within one ReplenishDelay window (10 frames) after the kill a cadence tick tops the
        // horde back up to its 4 slots. 14 frames covers any startup-stagger phase.
        for (var i = 0; i < 14; i++)
        {
            game.Step();
        }
        Assert.Equal(4, contain.MemberCount);
    }

    [Fact]
    public void ReplenishedMembers_CarryReplenishStatii()
    {
        var (game, _, contain) = SpawnHorde();
        KillMembers(game, contain, 2);
        for (var i = 0; i < 14; i++)
        {
            game.Step();
        }

        Assert.Equal(4, contain.MemberCount);
        // At least one member was (re)spawned by the behavior and stamped UNSELECTABLE; the two
        // survivors were never stamped, so this asserts the stamp reached the new members only.
        var stamped = 0;
        foreach (var member in Members(game, contain))
        {
            if (member.TestStatus(ObjectStatus.Unselectable))
            {
                stamped++;
            }
        }
        Assert.Equal(2, stamped);
    }

    [Fact]
    public void EnemyWithinRadius_SuppressesReplenish_UntilItLeaves()
    {
        var (game, horde, contain) = SpawnHorde();
        KillMembers(game, contain, 2);
        Assert.Equal(2, contain.MemberCount);

        // An enemy 20 units away (< the 50 suppression radius): every cadence tick is skipped.
        var enemy = game.SpawnObject("Enemy", game.PlayerManager.NeutralPlayer, new Vector3(120, 100, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);
        for (var i = 0; i < 14; i++)
        {
            game.Step();
        }
        Assert.Equal(2, contain.MemberCount);

        // Remove the enemy: the next tick replenishes.
        enemy.Kill();
        game.Step();
        for (var i = 0; i < 12; i++)
        {
            game.Step();
        }
        Assert.Equal(4, contain.MemberCount);
    }

    [Fact]
    public void StartsActiveNo_NeverReplenishes()
    {
        var (game, _, contain) = SpawnHorde("InactiveReplenishHorde");
        KillMembers(game, contain, 2);
        Assert.Equal(2, contain.MemberCount);

        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }
        // The module parked itself forever at construction; the dead members stay dead.
        Assert.Equal(2, contain.MemberCount);
    }

    // ---- item (3): xfer shadow copy + mid-state round trip + determinism ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var (game, horde, contain) = SpawnHorde();
        KillMembers(game, contain, 1);
        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }
        var live = horde.FindBehavior<ReplenishUnitsBehavior>();

        // A differently-seeded shadow in a second game (never ticked).
        var shadowGame = NewGame(seed: 0xD1FF);
        var shadowHorde = shadowGame.SpawnObject("ReplenishHorde", shadowGame.CivilianPlayer, new Vector3(0, 0, 0));
        var shadow = shadowHorde.FindBehavior<ReplenishUnitsBehavior>();
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_ContinuesIdentical()
    {
        var (gameA, hordeA, containA) = SpawnHorde(seed: 0xC0DE);
        var (gameB, hordeB, containB) = SpawnHorde(seed: 0xC0DE);

        KillMembers(gameA, containA, 2);
        KillMembers(gameB, containB, 2);
        for (var i = 0; i < 3; i++)
        {
            gameA.Step();
            gameB.Step();
        }

        // Save B's behavior and re-load it onto itself mid-run: invisible if Xfer is complete.
        var replenishB = hordeB.FindBehavior<ReplenishUnitsBehavior>();
        PortedModuleTestKit.Load(replenishB, PortedModuleTestKit.Save(replenishB));

        for (var i = 0; i < 14; i++)
        {
            gameA.Step();
            gameB.Step();
        }

        Assert.Equal(containA.MemberCount, containB.MemberCount);
        Assert.Equal(4, containB.MemberCount);
    }

    [Fact]
    public void RunTwice_ReplenishCadence_IsBitDeterministic()
    {
        var (gameA, _, containA) = SpawnHorde(seed: 0xFEED);
        var (gameB, _, containB) = SpawnHorde(seed: 0xFEED);

        KillMembers(gameA, containA, 3);
        KillMembers(gameB, containB, 3);

        // The startup-stagger RNG draw and the replenish member ids must match frame-for-frame.
        for (var frame = 0; frame < 30; frame++)
        {
            gameA.Step();
            gameB.Step();
            Assert.Equal(containA.MemberCount, containB.MemberCount);
            var idsA = new List<ObjectId>(containA.MemberIds);
            var idsB = new List<ObjectId>(containB.MemberIds);
            Assert.Equal(idsA, idsB);
        }
        Assert.Equal(4, containA.MemberCount);
    }
}
