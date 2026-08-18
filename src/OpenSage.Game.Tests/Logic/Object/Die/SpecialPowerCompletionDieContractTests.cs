// Mocked-game unit tests for the SpecialPowerCompletionDie port (api-freeze-v1 §6 fitness
// item 4): one test per INI-configurable branch, each of the shape
// [create -> trigger death -> observable effect], plus the shadow-copy base test taken
// mid-behavior and a mid-behavior save/load continuation test.
//
// The death trigger is the batch helper built once in PortedModuleTestKit
// (experiment-round-4 §4.1 DoD item 4), so the DeathTypes filter is exercised through the
// real ActiveBody >0 -> <=0 crossing rather than a direct call to the module.
//
// The observable effect is the completed-special-power log the module reports into: the
// SimContext adapter holds it until the script engine ports (SPCD-1), and its GPL-shaped
// scan (IsComplete) is what a PlayerCompletedSpecialPower script condition will read.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class SpecialPowerCompletionDieContractTests
{
    private const string Definitions = @"
Object CompletionBeacon
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialPowerCompletionDie ModuleTag_Die
    SpecialPowerTemplate = SpecialPowerAthelas
  End
End

Object BurnedCompletionBeacon
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialPowerCompletionDie ModuleTag_Die
    SpecialPowerTemplate = SpecialPowerBalrogAlly
    DeathTypes = NONE +BURNED
  End
End

Object TemplatelessBeacon
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialPowerCompletionDie ModuleTag_Die
  End
End

Object Bystander
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xD1E)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static SpecialPowerCompletionDie DieModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SpecialPowerCompletionDie>().Single();

    private static CompletedSpecialPowerLog LogOf(HeadlessSimGame game) =>
        ((SimContext)game.GameEngine.SimContext).CompletedSpecialPowers;

    // ------------------------------------------------------------------
    // Branch: creator assigned -> death reports.
    // ------------------------------------------------------------------
    [Fact]
    public void DeathReportsCompletion_WhenCreatorWasAssigned()
    {
        var game = NewGame();
        var creator = game.SpawnObject("Bystander", game.CivilianPlayer, Vector3.Zero);
        var beacon = game.SpawnObject("CompletionBeacon", game.CivilianPlayer, new Vector3(10, 0, 0));

        DieModuleOf(beacon).SetCreator(creator.Id);
        Assert.Empty(LogOf(game).Entries);

        PortedModuleTestKit.TriggerDeath(beacon);

        var entry = Assert.Single(LogOf(game).Entries);
        Assert.Equal("SpecialPowerAthelas", entry.Name);
        Assert.Equal(creator.Id, entry.SourceObjectId);
        Assert.Equal(game.PlayerManager.GetPlayerIndex(game.CivilianPlayer), entry.PlayerIndex);

        // ...and the scan a script condition performs finds it, source-filtered.
        Assert.True(LogOf(game).IsComplete(entry.PlayerIndex, "SpecialPowerAthelas", creator.Id, removeFromList: false));
        Assert.False(LogOf(game).IsComplete(entry.PlayerIndex, "SpecialPowerAthelas", beacon.Id, removeFromList: false));
    }

    // ------------------------------------------------------------------
    // Branch: no creator ever assigned -> death is silent.
    // ------------------------------------------------------------------
    [Fact]
    public void DeathIsSilent_WhenNoCreatorWasAssigned()
    {
        var game = NewGame();
        var beacon = game.SpawnObject("CompletionBeacon", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(beacon);

        Assert.Empty(LogOf(game).Entries);
    }

    // ------------------------------------------------------------------
    // Branch: the latch. First assignment wins forever - including an assignment of the
    // invalid id, which is how the payload/formation spawn sites silence all but one.
    // ------------------------------------------------------------------
    [Fact]
    public void SetCreatorLatches_InvalidFirstAssignmentSilencesPermanently()
    {
        var game = NewGame();
        var creator = game.SpawnObject("Bystander", game.CivilianPlayer, Vector3.Zero);
        var beacon = game.SpawnObject("CompletionBeacon", game.CivilianPlayer, new Vector3(10, 0, 0));

        var die = DieModuleOf(beacon);
        die.SetCreator(ObjectId.Invalid);
        die.SetCreator(creator.Id);      // ignored: the latch is closed

        PortedModuleTestKit.TriggerDeath(beacon);

        Assert.Empty(LogOf(game).Entries);
    }

    [Fact]
    public void SetCreatorLatches_LaterAssignmentCannotStealCredit()
    {
        var game = NewGame();
        var first = game.SpawnObject("Bystander", game.CivilianPlayer, Vector3.Zero);
        var second = game.SpawnObject("Bystander", game.CivilianPlayer, new Vector3(5, 0, 0));
        var beacon = game.SpawnObject("CompletionBeacon", game.CivilianPlayer, new Vector3(10, 0, 0));

        var die = DieModuleOf(beacon);
        die.SetCreator(first.Id);
        die.SetCreator(second.Id);

        PortedModuleTestKit.TriggerDeath(beacon);

        Assert.Equal(first.Id, Assert.Single(LogOf(game).Entries).SourceObjectId);
    }

    // ------------------------------------------------------------------
    // Branch: DeathTypes filter (inherited DieLogicData vocabulary).
    // ------------------------------------------------------------------
    [Fact]
    public void DeathTypesFilter_ReportsOnMatchingDeathOnly()
    {
        var game = NewGame();
        var creator = game.SpawnObject("Bystander", game.CivilianPlayer, Vector3.Zero);

        var normalDeath = game.SpawnObject("BurnedCompletionBeacon", game.CivilianPlayer, new Vector3(10, 0, 0));
        DieModuleOf(normalDeath).SetCreator(creator.Id);
        PortedModuleTestKit.TriggerDeath(normalDeath, DeathType.Normal);
        Assert.Empty(LogOf(game).Entries);

        var burnedDeath = game.SpawnObject("BurnedCompletionBeacon", game.CivilianPlayer, new Vector3(20, 0, 0));
        DieModuleOf(burnedDeath).SetCreator(creator.Id);
        PortedModuleTestKit.TriggerDeath(burnedDeath, DeathType.Burned);

        Assert.Equal("SpecialPowerBalrogAlly", Assert.Single(LogOf(game).Entries).Name);
    }

    // ------------------------------------------------------------------
    // Branch: SpecialPowerTemplate absent from the INI. GPL dereferences a null template
    // here; we skip the report instead (recorded deviation).
    // ------------------------------------------------------------------
    [Fact]
    public void MissingTemplate_ReportsNothingAndDoesNotThrow()
    {
        var game = NewGame();
        var creator = game.SpawnObject("Bystander", game.CivilianPlayer, Vector3.Zero);
        var beacon = game.SpawnObject("TemplatelessBeacon", game.CivilianPlayer, new Vector3(10, 0, 0));

        DieModuleOf(beacon).SetCreator(creator.Id);
        PortedModuleTestKit.TriggerDeath(beacon);

        Assert.Empty(LogOf(game).Entries);
    }

    // ------------------------------------------------------------------
    // Branch: NotifyScriptEngine called directly (the Weapon projectile path reports at
    // fire time, not at death), and the death that follows reports again.
    // ------------------------------------------------------------------
    [Fact]
    public void DirectNotify_ReportsWithoutDeath()
    {
        var game = NewGame();
        var creator = game.SpawnObject("Bystander", game.CivilianPlayer, Vector3.Zero);
        var beacon = game.SpawnObject("CompletionBeacon", game.CivilianPlayer, new Vector3(10, 0, 0));

        var die = DieModuleOf(beacon);
        die.SetCreator(creator.Id);
        die.NotifyScriptEngine();

        Assert.Single(LogOf(game).Entries);
        Assert.False(beacon.IsDestroyed);

        PortedModuleTestKit.TriggerDeath(beacon);
        Assert.Equal(2, LogOf(game).Entries.Count);
    }

    // ------------------------------------------------------------------
    // Sub-lethal damage must not fire a Die module.
    // ------------------------------------------------------------------
    [Fact]
    public void FleshWound_DoesNotReport()
    {
        var game = NewGame();
        var creator = game.SpawnObject("Bystander", game.CivilianPlayer, Vector3.Zero);
        var beacon = game.SpawnObject("CompletionBeacon", game.CivilianPlayer, new Vector3(10, 0, 0));

        DieModuleOf(beacon).SetCreator(creator.Id);

        var result = PortedModuleTestKit.ApplyDamage(beacon, 30f);
        Assert.False(result.Died);
        Assert.Empty(LogOf(game).Entries);
    }

    // ------------------------------------------------------------------
    // The shadow-copy base test, taken MID-BEHAVIOR: the latch is closed and carries a
    // real creator id, and the shadow starts in a different state (its own latch open).
    // ------------------------------------------------------------------
    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var creator = game.SpawnObject("Bystander", game.CivilianPlayer, Vector3.Zero);
        var beacon = game.SpawnObject("CompletionBeacon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var live = DieModuleOf(beacon);

        live.SetCreator(creator.Id);
        game.Step();

        var shadowHost = game.SpawnObject("CompletionBeacon", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = DieModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    /// <summary>
    /// The latch is the whole point of the walk: a shadow whose latch is already closed on
    /// a DIFFERENT creator must be overwritten by Load, not merged with.
    /// </summary>
    [Fact]
    public void ShadowCopy_OverwritesADifferentlyLatchedShadow()
    {
        var game = NewGame();
        var first = game.SpawnObject("Bystander", game.CivilianPlayer, Vector3.Zero);
        var second = game.SpawnObject("Bystander", game.CivilianPlayer, new Vector3(5, 0, 0));

        var beacon = game.SpawnObject("CompletionBeacon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var live = DieModuleOf(beacon);
        live.SetCreator(first.Id);

        var shadowHost = game.SpawnObject("CompletionBeacon", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = DieModuleOf(shadowHost);
        shadow.SetCreator(second.Id);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);

        // ...and the loaded state is behaviorally the live one: killing the shadow's host
        // credits the LIVE creator.
        PortedModuleTestKit.TriggerDeath(shadowHost);
        Assert.Equal(first.Id, Assert.Single(LogOf(game).Entries).SourceObjectId);
    }

    // ------------------------------------------------------------------
    // The walk itself, pinned to bytes. The shadow-copy test structurally cannot catch a
    // field dropped from Xfer (template v1.1 delta D-8); a byte-exact assertion on a stream
    // whose every field is known can, and it also pins declaration order (F9: OURS).
    // ------------------------------------------------------------------
    [Fact]
    public void Xfer_WalksVersionThenCreatorIdThenLatch_InDeclarationOrder()
    {
        var game = NewGame();
        var creator = game.SpawnObject("Bystander", game.CivilianPlayer, Vector3.Zero);
        var beacon = game.SpawnObject("CompletionBeacon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = DieModuleOf(beacon);

        // Fresh: version 1, ObjectId.Invalid (4 LE bytes of zero), latch open.
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00 }, PortedModuleTestKit.Save(module));

        module.SetCreator(creator.Id);

        // Latched: the creator's index appears LE in the middle, and the flag flips last.
        var expected = new byte[] { 0x01, 0, 0, 0, 0, 0x01 };
        System.BitConverter.GetBytes(creator.Id.Index).CopyTo(expected, 1);
        Assert.Equal(expected, PortedModuleTestKit.Save(module));
    }

    [Fact]
    public void Xfer_RejectsAFutureVersion()
    {
        var game = NewGame();
        var beacon = game.SpawnObject("CompletionBeacon", game.CivilianPlayer, Vector3.Zero);
        var module = DieModuleOf(beacon);

        var future = PortedModuleTestKit.Save(module);
        future[0] = 0x02;

        Assert.ThrowsAny<System.Exception>(() => PortedModuleTestKit.Load(module, future));
    }

    // ------------------------------------------------------------------
    // Mid-behavior save/load continuation: two identical games, identical script; game B
    // round-trips the module state through Save->Load at frame 3. If the walk lost or
    // misread anything, B's report differs from A's.
    // ------------------------------------------------------------------
    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        Assert.Equal(RunScenario(roundTripAtFrame: -1), RunScenario(roundTripAtFrame: 3));
    }

    private static string[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEED);
        var creator = game.SpawnObject("Bystander", game.CivilianPlayer, Vector3.Zero);
        var beacon = game.SpawnObject("CompletionBeacon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = DieModuleOf(beacon);

        // Real state driven in before the round trip: the latch closes at frame 1.
        var log = new string[8];
        for (var i = 0; i < log.Length; i++)
        {
            if (i == 1)
            {
                module.SetCreator(creator.Id);
            }

            if (i == roundTripAtFrame)
            {
                // No S6 wake frame to preserve here: a Die module is not an UpdateModule,
                // so the walk is the module's entire restorable state.
                PortedModuleTestKit.Load(module, PortedModuleTestKit.Save(module));
            }

            if (i == 5)
            {
                PortedModuleTestKit.TriggerDeath(beacon);
            }

            game.Step();

            log[i] = string.Join(
                "|",
                LogOf(game).Entries.Select(e => $"{e.PlayerIndex}:{e.Name}:{e.SourceObjectId.Index}"));
        }

        return log;
    }
}
