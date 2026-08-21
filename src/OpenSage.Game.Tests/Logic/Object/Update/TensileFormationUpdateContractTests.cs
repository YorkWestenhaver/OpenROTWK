// Mocked-game unit tests for the TensileFormationUpdate port (api-freeze-v1 §6 fitness item 4):
// one test per behavior branch, [create -> tick -> observable effect], plus the mid-behavior
// save/load round-trip and the shadow-copy base test. Object definitions are parsed from INI
// text through the real parser, so the Enabled/CrackSound parse is on the tested path.
//
// The observable is a formation member's BodyModule.DamageState: propagateDislodgement knocks
// every nearby TensileFormationUpdate member to Damaged (S1 body + S3 partition), and a member
// that runs its full life settles into Rubble. HeadlessSimGame carries a real body + partition,
// so both are live.

using System.Linq;
using System.Numerics;
using OpenSage;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class TensileFormationUpdateContractTests
{
    // Boulder: a disabled formation member. PrimedBoulder: one that starts already unzipping
    // (INI Enabled = Yes). Bystander: NOT a formation member (no TFU module), so the cascade
    // must skip it even in range.
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Object Boulder
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = TensileFormationUpdate ModuleTag_TFU
    Enabled = No
  End
End

Object PrimedBoulder
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = TensileFormationUpdate ModuleTag_TFU
    Enabled = Yes
  End
End

Object Bystander
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x7F0)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static TensileFormationUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<TensileFormationUpdate>().Single();

    private static void Step(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    // ------------------------------------------------------------------------------------------
    // Branch: an enabled member propagates dislodgement to nearby formation members only.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void EnabledMember_PropagatesDamageToNearbyFormationMembers()
    {
        var game = NewGame();
        game.SpawnObject("PrimedBoulder", game.CivilianPlayer, Vector3.Zero);
        var neighbour = game.SpawnObject("Boulder", game.CivilianPlayer, new Vector3(50, 0, 0)); // within 100

        Assert.Equal(BodyDamageType.Pristine, neighbour.BodyModule.DamageState);

        // First propagation fires at life % 30 == 29; step past it with margin.
        Step(game, 35);

        Assert.Equal(BodyDamageType.Damaged, neighbour.BodyModule.DamageState);
    }

    [Fact]
    public void NonFormationBystanderInRange_IsNotDislodged()
    {
        var game = NewGame();
        game.SpawnObject("PrimedBoulder", game.CivilianPlayer, Vector3.Zero);
        var bystander = game.SpawnObject("Bystander", game.CivilianPlayer, new Vector3(60, 0, 0)); // within 100

        Step(game, 35);

        // No TensileFormationUpdate module -> PartitionFilterTensileFormationMember rejects it.
        Assert.Equal(BodyDamageType.Pristine, bystander.BodyModule.DamageState);
    }

    [Fact]
    public void MemberOutsideRadius_IsNotDislodged()
    {
        var game = NewGame();
        game.SpawnObject("PrimedBoulder", game.CivilianPlayer, Vector3.Zero);
        var far = game.SpawnObject("Boulder", game.CivilianPlayer, new Vector3(500, 0, 0)); // > 100

        Step(game, 35);

        Assert.Equal(BodyDamageType.Pristine, far.BodyModule.DamageState);
    }

    // ------------------------------------------------------------------------------------------
    // Branch: a disabled member enables when it (self) becomes damaged, then unzips the cluster.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void SelfDamage_EnablesFormation_AndCascadesToNeighbour()
    {
        var game = NewGame();
        var source = game.SpawnObject("Boulder", game.CivilianPlayer, Vector3.Zero);
        var neighbour = game.SpawnObject("Boulder", game.CivilianPlayer, new Vector3(50, 0, 0));

        // The source gets hurt (GPL: any state >= Damaged trips the formation).
        source.BodyModule.DamageState = BodyDamageType.Damaged;

        // Disabled members poll every 30 frames; the source enables at its first poll, then its
        // life counter must reach 29 before the first propagation. Step generously.
        Step(game, 70);

        // The cascade knocked the neighbour to Damaged, which will in turn enable its own TFU.
        Assert.Equal(BodyDamageType.Damaged, neighbour.BodyModule.DamageState);
    }

    [Fact]
    public void DisabledMember_NeverDislodgesWhileUndamaged()
    {
        var game = NewGame();
        game.SpawnObject("Boulder", game.CivilianPlayer, Vector3.Zero);
        var neighbour = game.SpawnObject("Boulder", game.CivilianPlayer, new Vector3(50, 0, 0));

        // Nobody is hurt: the whole cluster sits idle indefinitely.
        Step(game, 70);

        Assert.Equal(BodyDamageType.Pristine, neighbour.BodyModule.DamageState);
    }

    // ------------------------------------------------------------------------------------------
    // Branch: a running member settles into rubble after its lifetime and stops.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void EnabledMember_SettlesIntoRubbleAfterLifetime()
    {
        var game = NewGame();
        var source = game.SpawnObject("PrimedBoulder", game.CivilianPlayer, Vector3.Zero);

        // Before the cutoff it has not become rubble.
        Step(game, 100);
        Assert.NotEqual(BodyDamageType.Rubble, source.BodyModule.DamageState);

        // life > 300 -> rubble, sleep forever.
        Step(game, 210);
        Assert.Equal(BodyDamageType.Rubble, source.BodyModule.DamageState);
    }

    // ------------------------------------------------------------------------------------------
    // Xfer: shadow-copy CRC equality mid-behavior.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var host = game.SpawnObject("PrimedBoulder", game.CivilianPlayer, Vector3.Zero);

        // Drive real state (enabled = true, life advanced several frames).
        Step(game, 12);
        var live = ModuleOf(host);

        // The shadow is the same class over the same data on a second, differently-stated object
        // (its life is 0); Load must overwrite everything the walk carries.
        var shadowHost = game.SpawnObject("PrimedBoulder", game.CivilianPlayer, new Vector3(300, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    // ------------------------------------------------------------------------------------------
    // Xfer: mid-behavior save/load continues bit-identically.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 4);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static int[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0x7F00D);
        var source = game.SpawnObject("PrimedBoulder", game.CivilianPlayer, Vector3.Zero);
        var neighbour = game.SpawnObject("Boulder", game.CivilianPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(source);

        var trajectory = new int[40];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                // Round-trip the enabled flag AND the life counter (F-TFU-7); losing either
                // shifts the propagation frame and diverges the neighbour's DamageState track.
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;     // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = (int)neighbour.BodyModule.DamageState;
        }

        return trajectory;
    }
}
