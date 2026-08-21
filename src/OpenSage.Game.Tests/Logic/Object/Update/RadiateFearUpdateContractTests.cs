// Mocked-game unit tests for the RadiateFearUpdate SPLIT port (R13; see
// bfme2-workbench/research/modules-r13/specs/RadiateFearUpdateModuleData.md). Shaped exactly
// like AutoAbilityBehaviorContractTests.cs: one Definitions INI string parsed through the real
// parser (so the corrected ParseFix64/ParseDurationLogicFrames/upgrade-mux parse is on the
// tested path), HeadlessSimGame.LoadIniText, [create -> tick -> observable] per branch, plus the
// mid-behavior save/load round-trip and the mandatory shadow-copy CRC test.
//
// The observable is LastPulseVictimIds / TryConsumePulse - the driven, Xfer'd decision seam the
// module exposes since no landed emotion-application primitive exists yet (see the module's
// file-header HELD section).
//
// Sleepy-update caveat, applied (identical to AutoAbilityBehaviorContractTests): a freshly
// spawned module's first Update() call lands on the object's SECOND HeadlessSimGame.Step(), not
// the first. Every test steps well past that margin before asserting.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class RadiateFearUpdateContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_Dread
  Type = PLAYER
End
Upgrade Upgrade_Never
  Type = PLAYER
End

Object DreadRadiator            ; upgrade-gated shape (blackrider.ini:903-911)
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RadiateFearUpdate ModuleTag_Fear
    InitiallyActive      = No
    TriggeredBy          = Upgrade_Dread
    WhichSpecialPower    = 1
    GenerateFear         = Yes
    EmotionPulseRadius   = 100
    EmotionPulseInterval = 1000
  End
End

Object AlwaysOnRadiator         ; always-on + filter shape (harbingerhorde.ini:591-597)
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RadiateFearUpdate ModuleTag_Fear
    InitiallyActive      = Yes
    GenerateFear         = Yes
    EmotionPulseRadius   = 200
    EmotionPulseInterval = 1000
    VictimFilter         = ANY +INFANTRY -STRUCTURE
  End
End

Object UnfilteredRadiator       ; no VictimFilter at all -> accepts every live candidate
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RadiateFearUpdate ModuleTag_Fear
    InitiallyActive      = Yes
    GenerateFear         = Yes
    EmotionPulseRadius   = 200
    EmotionPulseInterval = 1000
  End
End

Object AllTriggersRadiator      ; multi-trigger shape (createaheropowers.inc:258-268)
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RadiateFearUpdate ModuleTag_Fear
    InitiallyActive      = No
    TriggeredBy          = Upgrade_Dread Upgrade_Never
    RequiresAllTriggers  = Yes
    GenerateFear          = Yes
    EmotionPulseRadius   = 100
    EmotionPulseInterval = 100
  End
End

Object RoundTripRadiator
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RadiateFearUpdate ModuleTag_Fear
    InitiallyActive      = Yes
    GenerateFear         = Yes
    EmotionPulseRadius   = 200
    EmotionPulseInterval = 1000
  End
End

Object Grunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object Bunker
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xFEA2)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static RadiateFearUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<RadiateFearUpdate>().Single();

    private static UpgradeSet UpgradeSetOf(HeadlessSimGame game, params string[] upgradeNames)
    {
        var set = new UpgradeSet();
        foreach (var name in upgradeNames)
        {
            set.Add(game.AssetStore.Upgrades.GetByName(name));
        }
        return set;
    }

    /// <summary>Margin past the sleepy-update caveat's second-Step threshold.</summary>
    private static void StepPastFirstPulse(HeadlessSimGame game)
    {
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }
    }

    // 1. InitiallyActive = No, upgrade unapplied -> never pulses.
    [Fact]
    public void UpgradeNotTriggered_NeverPulses()
    {
        var game = NewGame();
        var radiator = game.SpawnObject("DreadRadiator", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));

        StepPastFirstPulse(game);

        var module = ModuleOf(radiator);
        Assert.Empty(module.LastPulseVictimIds);
        Assert.False(module.TryConsumePulse(out _));
    }

    // 2. Upgrade applied -> pulses with exactly the in-radius victim.
    [Fact]
    public void UpgradeApplied_Pulses_WithInRadiusVictim()
    {
        var game = NewGame();
        var radiator = game.SpawnObject("DreadRadiator", game.CivilianPlayer, Vector3.Zero);
        var grunt = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(radiator);

        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_Dread"));

        // EmotionPulseInterval = 1000ms; step well past one interval.
        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }

        Assert.True(module.TryConsumePulse(out var victims));
        Assert.Equal(new[] { grunt.Id }, victims);
    }

    // 3. RequiresAllTriggers = Yes: one-of-two vs. both triggers.
    [Fact]
    public void RequiresAllTriggers_OneOfTwo_StillGated()
    {
        var game = NewGame();
        var radiator = game.SpawnObject("AllTriggersRadiator", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(radiator);

        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_Dread"));
        StepPastFirstPulse(game);

        Assert.Empty(module.LastPulseVictimIds);
        Assert.False(module.TryConsumePulse(out _));
    }

    [Fact]
    public void RequiresAllTriggers_BothTriggers_Pulses()
    {
        var game = NewGame();
        var radiator = game.SpawnObject("AllTriggersRadiator", game.CivilianPlayer, Vector3.Zero);
        var grunt = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(radiator);

        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_Dread", "Upgrade_Never"));

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        Assert.True(module.TryConsumePulse(out var victims));
        Assert.Equal(new[] { grunt.Id }, victims);
    }

    // 4. InitiallyActive = Yes -> pulses with no upgrade at all.
    [Fact]
    public void InitiallyActive_PulsesWithoutUpgrade()
    {
        var game = NewGame();
        var radiator = game.SpawnObject("AlwaysOnRadiator", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(radiator);

        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }

        Assert.True(module.TryConsumePulse(out var victims));
        Assert.NotEmpty(victims);
    }

    // 5. Pulse-cadence exactness: gaps between consecutive pulse frames equal
    // EmotionPulseInterval.Value exactly over 3 intervals, no drift/double-pulse.
    [Fact]
    public void PulseCadence_IsExact_OverThreeIntervals()
    {
        var game = NewGame();
        var radiator = game.SpawnObject("AlwaysOnRadiator", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(radiator);

        // EmotionPulseInterval = 1000ms; at the game's logic rate that's some fixed frame span.
        // Step well past 3 intervals, one frame at a time, recording every frame a pulse landed.
        var pulseFrames = new System.Collections.Generic.List<int>();
        for (var frame = 0; frame < 3200; frame++)
        {
            game.Step();
            if (module.TryConsumePulse(out _))
            {
                pulseFrames.Add(frame);
            }
        }

        Assert.True(pulseFrames.Count >= 3, $"expected at least 3 pulses, got {pulseFrames.Count}");

        // Compare exactly 3 consecutive gaps; all must be identical (no drift/double-pulse/skip).
        var gaps = new int[3];
        for (var i = 0; i < 3; i++)
        {
            gaps[i] = pulseFrames[i + 1] - pulseFrames[i];
        }
        Assert.Equal(gaps[0], gaps[1]);
        Assert.Equal(gaps[1], gaps[2]);
    }

    // 6. Filter-matched selection set is exactly the matching ids AND in ascending ObjectId order.
    [Fact]
    public void FilterMatchedSelection_IsExactSet_InAscendingObjectIdOrder()
    {
        var game = NewGame();
        var radiator = game.SpawnObject("AlwaysOnRadiator", game.CivilianPlayer, Vector3.Zero);
        var grunt1 = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(20, 0, 0));
        var grunt2 = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(40, 0, 0));
        game.SpawnObject("Bunker", game.PlayerManager.NeutralPlayer, new Vector3(60, 0, 0));
        var module = ModuleOf(radiator);

        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }

        Assert.True(module.TryConsumePulse(out var victims));

        var expected = new[] { grunt1.Id, grunt2.Id }.OrderBy(id => id.Index).ToArray();
        Assert.Equal(expected, victims);
    }

    // 7. Radius boundary in/out.
    [Fact]
    public void RadiusBoundary_OnlyInRadiusCandidateSelected()
    {
        var game = NewGame();
        var radiator = game.SpawnObject("AlwaysOnRadiator", game.CivilianPlayer, Vector3.Zero);
        // EmotionPulseRadius = 200.
        var inside = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(5000, 0, 0));
        var module = ModuleOf(radiator);

        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }

        Assert.True(module.TryConsumePulse(out var victims));
        Assert.Equal(new[] { inside.Id }, victims);
    }

    // 8. Null VictimFilter accepts every live candidate (blackrider authored shape).
    [Fact]
    public void NullVictimFilter_AcceptsEveryLiveCandidate()
    {
        var game = NewGame();
        var radiator = game.SpawnObject("UnfilteredRadiator", game.CivilianPlayer, Vector3.Zero);
        var grunt = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var bunker = game.SpawnObject("Bunker", game.PlayerManager.NeutralPlayer, new Vector3(60, 0, 0));
        var module = ModuleOf(radiator);

        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }

        Assert.True(module.TryConsumePulse(out var victims));
        var expected = new[] { grunt.Id, bunker.Id }.OrderBy(id => id.Index).ToArray();
        Assert.Equal(expected, victims);
    }

    // 9. Dead/destroyed candidates excluded.
    [Fact]
    public void DeadCandidates_AreExcluded()
    {
        var game = NewGame();
        var radiator = game.SpawnObject("AlwaysOnRadiator", game.CivilianPlayer, Vector3.Zero);
        var alive = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var dead = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(60, 0, 0));
        var module = ModuleOf(radiator);

        PortedModuleTestKit.TriggerDeath(dead);

        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }

        Assert.True(module.TryConsumePulse(out var victims));
        Assert.Equal(new[] { alive.Id }, victims);
    }

    // 10. TryConsumePulse clears until the next pulse.
    [Fact]
    public void TryConsumePulse_ClearsUntilNextPulse()
    {
        var game = NewGame();
        var radiator = game.SpawnObject("AlwaysOnRadiator", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(radiator);

        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }

        Assert.True(module.TryConsumePulse(out _));
        Assert.False(module.TryConsumePulse(out _));

        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }

        Assert.True(module.TryConsumePulse(out _));
    }

    // 11. An unconsumed pulse is replaced wholesale, not accumulated.
    [Fact]
    public void UnconsumedPulse_IsReplacedWholesale_NotAccumulated()
    {
        var game = NewGame();
        var radiator = game.SpawnObject("AlwaysOnRadiator", game.CivilianPlayer, Vector3.Zero);
        var first = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(radiator);

        // Let one pulse land without consuming it.
        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }
        Assert.Contains(first.Id, module.LastPulseVictimIds);

        // Move a second candidate into radius, step a full interval, then consume.
        var second = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(60, 0, 0));
        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }

        Assert.True(module.TryConsumePulse(out var victims));
        var expected = new[] { first.Id, second.Id }.OrderBy(id => id.Index).ToArray();
        Assert.Equal(expected, victims);
        // No duplicates from the first pulse.
        Assert.Equal(victims.Length, victims.Distinct().Count());
    }

    // 13. Shadow-copy base test.
    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var radiatorHost = game.SpawnObject("AlwaysOnRadiator", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));

        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }
        var live = ModuleOf(radiatorHost);

        var shadowHost = game.SpawnObject("AlwaysOnRadiator", game.CivilianPlayer, new Vector3(2000, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    // 12. Mid-behavior save/load round-trip, including a round-trip taken while an unconsumed
    // victim set is pending.
    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_WithPendingVictimsSet_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 40); // mid-run, after the first pulse landed and before it's consumed
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static (int Count, bool PulsedThisFrame)[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D2);
        var radiator = game.SpawnObject("RoundTripRadiator", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(radiator);

        var trajectory = new (int Count, bool PulsedThisFrame)[60];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk; // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            var beforeCount = module.LastPulseVictimIds.Count;
            game.Step();
            var afterCount = module.LastPulseVictimIds.Count;

            trajectory[i] = (afterCount, afterCount > 0 && beforeCount == 0);
        }

        return trajectory;
    }
}
