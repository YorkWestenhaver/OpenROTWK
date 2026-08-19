// Mocked-game contract tests for the S6 horde/formation system (api-freeze-v1 §6 fitness
// item 4 shape): slot construction with deterministic jitter, InitialPayload + banner
// spawn, member steering to slot world positions through the S2 locomotor, melee rank
// release + back-up shuffle, the confirmed flanking formula, banner replenish and
// banner-death lifecycles, empty-horde destruction - plus the shadow-copy base test, a
// mid-state save/load round-trip continuation, and a run-twice bit-determinism check.
//
// Definitions parse from INI text through the real parser, so the audited quantizing
// parse functions (durations, angles, percentages, Fix64 pairs) are on the tested path.

using System.Collections.Generic;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Horde;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Horde;

public class SimHordeContainContractTests
{
    // Speed 30 -> 6/frame at the frozen 5 Hz. Backup delays 200..400 ms -> 1..2 frames.
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

Object HordeBanner
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = SimHordeMember ModuleTag_HordeMember
  End
  Behavior = SimBannerCarrierUpdate ModuleTag_Banner
    IdleSpawnRate = 1000
    MeleeFreeUnitSpawnTime = 2000
    DiedRespawnTime = 2000
    MeleeFreeBannerReSpawnTime = 2000
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

Object GruntHorde
  KindOf = HORDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = SimHordeContain ModuleTag_Contain
    ObjectStatusOfContained =
    InitialPayload = HordeGrunt 4
    Slots = 5
    RandomOffset = X:2 Y:2
    RankInfo = RankNumber:1 UnitType:HordeGrunt Position:X:10 Y:-10 Position:X:10 Y:10
    RankInfo = RankNumber:2 UnitType:HordeGrunt Position:X:25 Y:-10 Position:X:25 Y:10
    RanksToReleaseWhenAttacking = 2
    MeleeAttackLeashDistance = 60
    BackUpMinDelayTime = 200
    BackUpMaxDelayTime = 400
    BackUpMinDistance = 1
    BackUpMaxDistance = 2
    BackupPercentage = 100%
    FrontAngle = 180
    FlankedDelay = 1000
    FlankedDuration = 2000
    BannerCarriersAllowed = HordeBanner
    BannerCarrierPosition = UnitType:HordeBanner Pos:X:40 Y:0
  End
  Locomotor = SET_NORMAL TestHordeLoco
End

Object DoomedHorde
  KindOf = HORDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = SimHordeContain ModuleTag_Contain
    InitialPayload = HordeGrunt 2
    Slots = 3
    RankInfo = RankNumber:1 UnitType:HordeGrunt Position:X:10 Y:-10 Position:X:10 Y:10
    BannerCarriersAllowed = HordeBanner
    BannerCarrierPosition = UnitType:HordeBanner Pos:X:40 Y:0
    BannerCarrierDestroyHordeOnDeath = Yes
    BannerCarrierHordeDeathType = NORMAL
  End
  Locomotor = SET_NORMAL TestHordeLoco
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x60DE)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static (HeadlessSimGame Game, GameObject Horde, SimHordeContain Contain) SpawnHorde(
        uint seed = 0x60DE, string template = "GruntHorde", int warmupFrames = 2)
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

    // ---- slot construction + payload (spec §5.2 / §4.1 InitialPayload) ----

    [Fact]
    public void InitialPayload_SpawnsMembersAndBannerIntoSlots()
    {
        var (game, _, contain) = SpawnHorde();

        // 4 rank slots + 1 appended banner slot, all seated.
        Assert.Equal(5, contain.SlotCount);
        Assert.Equal(5, contain.MemberCount);
        var members = Members(game, contain);
        Assert.Equal(4, members.FindAll(m => m.Definition.Name == "HordeGrunt").Count);
        Assert.Single(members.FindAll(m => m.Definition.Name == "HordeBanner"));
    }

    [Fact]
    public void SlotJitter_IsWithinRandomOffset_AndAppliedPerSlot()
    {
        var (_, _, contain) = SpawnHorde();

        // Slot 0 declared at X:10 Y:-10, RandomOffset X:2 Y:2 -> jittered within +-2.
        var two = Fix64.FromDecimalLiteral("2");
        var expectedX = Fix64.FromDecimalLiteral("10");
        var expectedY = Fix64.FromDecimalLiteral("-10");
        var slot = contain.GetSlot(0);
        Assert.True(slot.OffsetX >= expectedX - two && slot.OffsetX <= expectedX + two);
        Assert.True(slot.OffsetY >= expectedY - two && slot.OffsetY <= expectedY + two);

        // The banner slot (appended last) takes the BannerCarrierPosition and NO jitter roll
        // is observable there beyond the configured pair (RankInfoIndex -1).
        var banner = contain.GetSlot(4);
        Assert.Equal(-1, banner.RankInfoIndex);
        Assert.Equal(Fix64.FromDecimalLiteral("40"), banner.OffsetX);
        Assert.Equal(Fix64.Zero, banner.OffsetY);
    }

    [Fact]
    public void SlotJitter_IsDeterministicPerSeed()
    {
        var (_, _, a) = SpawnHorde(seed: 0xAAA1);
        var (_, _, b) = SpawnHorde(seed: 0xAAA1);
        var (_, _, c) = SpawnHorde(seed: 0xBBB2);

        var anyDiffers = false;
        for (var i = 0; i < a.SlotCount; i++)
        {
            Assert.Equal(a.GetSlot(i).OffsetX.RawValue, b.GetSlot(i).OffsetX.RawValue);
            Assert.Equal(a.GetSlot(i).OffsetY.RawValue, b.GetSlot(i).OffsetY.RawValue);
            if (a.GetSlot(i).OffsetX.RawValue != c.GetSlot(i).OffsetX.RawValue ||
                a.GetSlot(i).OffsetY.RawValue != c.GetSlot(i).OffsetY.RawValue)
            {
                anyDiffers = true;
            }
        }
        Assert.True(anyDiffers, "different match seeds should roll different slot jitter");
    }

    // ---- steering (spec §5.3) ----

    [Fact]
    public void Members_SteerIntoTheirSlotWorldPositions()
    {
        var (game, _, contain) = SpawnHorde();
        for (var i = 0; i < 20; i++)
        {
            game.Step();
        }

        var closeEnoughSq = Fix64.FromDecimalLiteral("16"); // within 4 of the slot
        for (var i = 0; i < contain.SlotCount; i++)
        {
            var slot = contain.GetSlot(i);
            Assert.True(slot.Occupant.IsValid);
            Assert.True(contain.TryGetSlotWorldPosition(i, out var target));
            var member = game.GameLogic.GetObjectById(slot.Occupant);
            var pos = member.FindBehavior<SimLocomotorUpdate>().Physics.Position;
            var dx = pos.X - target.X;
            var dy = pos.Y - target.Y;
            Assert.True(dx * dx + dy * dy <= closeEnoughSq,
                $"slot {i}: member at ({pos.X},{pos.Y}) not near ({target.X},{target.Y})");
        }
    }

    // ---- melee: rank release + back-up shuffle (spec §5.3/§5.4) ----

    [Fact]
    public void MeleeAttack_ReleasesConfiguredRanks_AndBacksUpTheRest()
    {
        var (game, _, contain) = SpawnHorde();
        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        contain.SetMeleeAttacking(true);
        Assert.True(contain.IsMeleeAttacking);

        // Rank 2 is in RanksToReleaseWhenAttacking; rank 1 is not.
        for (var i = 0; i < 4; i++)
        {
            var slot = contain.GetSlot(i);
            Assert.Equal(slot.RankNumber == 2, slot.Released);
        }

        // BackupPercentage 100%: every retained (non-released) rank slot backs up within a
        // few delay windows (1..2 frames each), by 1..2 cells (10..20 world units) per roll.
        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }
        for (var i = 0; i < 4; i++)
        {
            var slot = contain.GetSlot(i);
            if (slot.Released)
            {
                Assert.Equal(Fix64.Zero, slot.BackupDistance);
            }
            else
            {
                Assert.True(slot.BackupDistance >= Fix64.FromDecimalLiteral("10"),
                    $"slot {i} never backed up");
            }
        }

        // Leaving melee clears release flags and the accumulated back-up.
        contain.SetMeleeAttacking(false);
        for (var i = 0; i < 4; i++)
        {
            Assert.False(contain.GetSlot(i).Released);
            Assert.Equal(Fix64.Zero, contain.GetSlot(i).BackupDistance);
        }
    }

    // ---- flanking (spec §6, confirmed formula) ----

    [Fact]
    public void Flanking_FrontalAttackDoesNotFlank_RearAttackDoes()
    {
        var (game, _, contain) = SpawnHorde();
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        var member = Members(game, contain)[0];

        // Horde spawn yaw 0 -> forward = +X. FrontAngle 180: the frontal arc is the +X
        // half-plane; an attack from straight ahead must NOT flank.
        var front = game.SpawnObject("Enemy", game.CivilianPlayer, new Vector3(300, 100, 0));
        game.Step();
        PortedModuleTestKit.ApplyDamage(member, 10f, source: front);
        Assert.False(contain.IsFlanked);

        // From straight behind (-X): dot(d, f) = -1 < cos(90 deg) = 0 -> FLANKED.
        var rear = game.SpawnObject("Enemy", game.CivilianPlayer, new Vector3(-100, 100, 0));
        game.Step();
        PortedModuleTestKit.ApplyDamage(member, 10f, source: rear);
        Assert.True(contain.IsFlanked);
    }

    [Fact]
    public void Flanking_ExpiresAfterFlankedDuration()
    {
        var (game, _, contain) = SpawnHorde();
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        var member = Members(game, contain)[0];
        var rear = game.SpawnObject("Enemy", game.CivilianPlayer, new Vector3(-100, 100, 0));
        game.Step();
        PortedModuleTestKit.ApplyDamage(member, 10f, source: rear);
        Assert.True(contain.IsFlanked);

        // FlankedDuration = 2000 ms -> 10 frames at 5 Hz.
        for (var i = 0; i < 10; i++)
        {
            Assert.True(contain.IsFlanked);
            game.Step();
        }
        Assert.False(contain.IsFlanked);
    }

    // ---- banner replenish (spec §7 / §4.3) ----

    [Fact]
    public void Banner_ReplenishesADeadMemberWhileIdle()
    {
        var (game, _, contain) = SpawnHorde();
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        Assert.Equal(5, contain.MemberCount);

        var victim = Members(game, contain)[0];
        PortedModuleTestKit.TriggerDeath(victim);
        game.Step();
        Assert.Equal(4, contain.MemberCount);

        // IdleSpawnRate = 1000 ms -> 5 frames; the horde never fought, so it is idle.
        for (var i = 0; i < 8 && contain.MemberCount < 5; i++)
        {
            game.Step();
        }
        Assert.Equal(5, contain.MemberCount);
    }

    [Fact]
    public void BannerDeath_WithDestroyHordeOnDeath_KillsTheWholeHorde()
    {
        var (game, horde, contain) = SpawnHorde(template: "DoomedHorde");
        Assert.Equal(3, contain.MemberCount);

        GameObject banner = null;
        foreach (var member in Members(game, contain))
        {
            if (member.Definition.Name == "HordeBanner")
            {
                banner = member;
            }
        }
        Assert.NotNull(banner);

        PortedModuleTestKit.TriggerDeath(banner);
        game.Step();  // horde reaps the banner, kills the members
        game.Step();  // horde reaps the members, destroys itself

        Assert.Equal(0, contain.MemberCount);
        Assert.True(horde.IsDestroyed);
    }

    // ---- lifecycle: empty horde dies (spec §8) ----

    [Fact]
    public void LastMemberDeath_DestroysTheHordeObject()
    {
        var (game, horde, contain) = SpawnHorde();
        foreach (var member in Members(game, contain))
        {
            PortedModuleTestKit.TriggerDeath(member);
        }
        game.Step();
        Assert.True(horde.IsDestroyed);
    }

    // ---- xfer: shadow copy + mid-state round trip + run-twice determinism ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var (game, _, live) = SpawnHorde();
        for (var i = 0; i < 8; i++)
        {
            game.Step();
        }
        live.SetMeleeAttacking(true);
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }
        var member = Members(game, live)[0];
        var rear = game.SpawnObject("Enemy", game.CivilianPlayer, new Vector3(-100, 100, 0));
        game.Step();
        PortedModuleTestKit.ApplyDamage(member, 10f, source: rear);

        // A differently-stated shadow in a second game (never initialized).
        var shadowGame = NewGame(seed: 0xD1FF);
        var shadowHorde = shadowGame.SpawnObject("GruntHorde", shadowGame.CivilianPlayer, new Vector3(0, 0, 0));
        var shadow = shadowHorde.FindBehavior<SimHordeContain>();
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void BannerModule_ShadowCopyCrcEqualsLiveCrc()
    {
        var (game, _, contain) = SpawnHorde();
        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }
        GameObject banner = null;
        foreach (var member in Members(game, contain))
        {
            if (member.Definition.Name == "HordeBanner")
            {
                banner = member;
            }
        }
        var live = banner.FindBehavior<SimBannerCarrierUpdate>();

        var shadowGame = NewGame(seed: 0xD1FF);
        var shadowBanner = shadowGame.SpawnObject("HordeBanner", shadowGame.CivilianPlayer, new Vector3(0, 0, 0));
        var shadow = shadowBanner.FindBehavior<SimBannerCarrierUpdate>();
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_ContinuesBitIdentical()
    {
        // Two identical runs; run B's horde module state is saved and re-loaded onto itself
        // at frame 10 (a perturbation that must be invisible if Xfer is complete).
        var (gameA, _, containA) = SpawnHorde(seed: 0xC0DE);
        var (gameB, _, containB) = SpawnHorde(seed: 0xC0DE);

        for (var i = 0; i < 8; i++)
        {
            gameA.Step();
            gameB.Step();
        }
        containA.SetMeleeAttacking(true);
        containB.SetMeleeAttacking(true);
        for (var i = 0; i < 2; i++)
        {
            gameA.Step();
            gameB.Step();
        }

        PortedModuleTestKit.Load(containB, PortedModuleTestKit.Save(containB));

        for (var i = 0; i < 8; i++)
        {
            gameA.Step();
            gameB.Step();
        }

        Assert.Equal(containA.SlotCount, containB.SlotCount);
        for (var i = 0; i < containA.SlotCount; i++)
        {
            var slotA = containA.GetSlot(i);
            var slotB = containB.GetSlot(i);
            Assert.Equal(slotA.Occupant, slotB.Occupant);
            Assert.Equal(slotA.BackupDistance.RawValue, slotB.BackupDistance.RawValue);
            var memberA = gameA.GameLogic.GetObjectById(slotA.Occupant);
            var memberB = gameB.GameLogic.GetObjectById(slotB.Occupant);
            if (memberA == null || memberB == null)
            {
                Assert.Equal(memberA == null, memberB == null);
                continue;
            }
            var posA = memberA.FindBehavior<SimLocomotorUpdate>().Physics.Position;
            var posB = memberB.FindBehavior<SimLocomotorUpdate>().Physics.Position;
            Assert.Equal(posA.X.RawValue, posB.X.RawValue);
            Assert.Equal(posA.Y.RawValue, posB.Y.RawValue);
        }
    }

    [Fact]
    public void RunTwice_MovingMeleeHorde_IsBitDeterministic()
    {
        var (gameA, hordeA, containA) = SpawnHorde(seed: 0xFEED);
        var (gameB, hordeB, containB) = SpawnHorde(seed: 0xFEED);

        hordeA.FindBehavior<SimLocomotorUpdate>()
            .SetTargetPosition(new FixVector3(
                Fix64.FromDecimalLiteral("300"), Fix64.FromDecimalLiteral("200"), Fix64.Zero),
                Fix64.FromDecimalLiteral("99999"));
        hordeB.FindBehavior<SimLocomotorUpdate>()
            .SetTargetPosition(new FixVector3(
                Fix64.FromDecimalLiteral("300"), Fix64.FromDecimalLiteral("200"), Fix64.Zero),
                Fix64.FromDecimalLiteral("99999"));

        for (var i = 0; i < 12; i++)
        {
            gameA.Step();
            gameB.Step();
        }
        containA.SetMeleeAttacking(true);
        containB.SetMeleeAttacking(true);
        for (var i = 0; i < 13; i++)
        {
            gameA.Step();
            gameB.Step();
        }

        var membersA = Members(gameA, containA);
        var membersB = Members(gameB, containB);
        Assert.Equal(membersA.Count, membersB.Count);
        for (var i = 0; i < membersA.Count; i++)
        {
            var posA = membersA[i].FindBehavior<SimLocomotorUpdate>().Physics.Position;
            var posB = membersB[i].FindBehavior<SimLocomotorUpdate>().Physics.Position;
            Assert.Equal(posA.X.RawValue, posB.X.RawValue);
            Assert.Equal(posA.Y.RawValue, posB.Y.RawValue);
            Assert.Equal(posA.Z.RawValue, posB.Z.RawValue);
        }
    }
}
