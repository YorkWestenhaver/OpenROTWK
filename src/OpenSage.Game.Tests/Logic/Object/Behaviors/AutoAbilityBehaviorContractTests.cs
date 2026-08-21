// Mocked-game unit tests for the AutoAbilityBehavior port (R13; api-freeze-v1 §6 fitness item
// 4): one test per behavior branch, [create -> tick -> observable], plus the mid-behavior
// save/load round-trip and the shadow-copy base test. Object definitions are parsed from INI
// text through the real parser, so the corrected MinScanRange/MaxScanRange ParseFix64 parse is
// on the tested path.
//
// The observable is PendingActivationTargetId / TryConsumePendingActivation - the driven,
// Xfer'd decision seam the module exposes since no landed caller into the pre-SimCore
// SpecialPowerModule float system exists yet (see the module's file-header gap note).
//
// The sleepy-update caveat, applied: a freshly spawned module's first Update() call happens on
// the object's SECOND HeadlessSimGame.Step(), not the first (UpdateModule's ctor leaves
// _nextUpdateFrame at its default "already due" value, but the object's own first opportunity
// is the following SimPhase.ModuleUpdate pass after spawn). Every test below steps well past
// that margin before asserting "no candidate yet / first scan pending".

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class AutoAbilityBehaviorContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_Never
  Type = PLAYER
End

Object GatedScanner
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoAbilityBehavior ModuleTag_Auto
    TriggeredBy = Upgrade_Never
    SpecialAbility = SomeSpecialAbility
    MaxScanRange = 100
    MinScanRange = 20
    Query = 0 +INFANTRY
  End
End

Object Scanner
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoAbilityBehavior ModuleTag_Auto
    StartsActive = Yes
    SpecialAbility = SomeSpecialAbility
    MaxScanRange = 100
    MinScanRange = 20
    AllowSelf = No
    Query = 0 +INFANTRY
  End
End

Object ForbiddenStatusScanner
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoAbilityBehavior ModuleTag_Auto
    StartsActive = Yes
    SpecialAbility = SomeSpecialAbility
    MaxScanRange = 100
    MinScanRange = 0
    ForbiddenStatus = UNSELECTABLE
    Query = 0 +INFANTRY
  End
End

Object SelfScanner
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoAbilityBehavior ModuleTag_Auto
    StartsActive = Yes
    SpecialAbility = SomeSpecialAbility
    MaxScanRange = 100
    MinScanRange = 20
    AllowSelf = Yes
    Query = 0 +INFANTRY
  End
End

Object LeashedScanner
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoAbilityBehavior ModuleTag_Auto
    StartsActive = Yes
    SpecialAbility = SomeSpecialAbility
    MaxScanRange = 50
    MinScanRange = 0
    BaseMaxRangeFromStartPos = Yes
    Query = 0 +INFANTRY
  End
End

Object RoundTripScanner
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoAbilityBehavior ModuleTag_Auto
    StartsActive = Yes
    SpecialAbility = SomeSpecialAbility
    MaxScanRange = 100
    MinScanRange = 0
    Query = 0 +INFANTRY
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

    private static HeadlessSimGame NewGame(uint seed = 0xA11)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static AutoAbilityBehavior ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<AutoAbilityBehavior>().Single();

    /// <summary>Margin past the sleepy-update caveat's second-Step threshold.</summary>
    private static void StepPastFirstScan(HeadlessSimGame game)
    {
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }
    }

    [Fact]
    public void UpgradeNotTriggered_NeverScans()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("GatedScanner", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));

        StepPastFirstScan(game);

        Assert.False(ModuleOf(scanner).PendingActivationTargetId.IsValid);
    }

    [Fact]
    public void MatchingCandidateWithinBand_SetsPendingActivation()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        var candidate = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));

        StepPastFirstScan(game);

        Assert.True(ModuleOf(scanner).TryConsumePendingActivation(out var targetId));
        Assert.Equal(candidate.Id, targetId);
    }

    [Fact]
    public void CandidateInsideMinScanRange_IsRejected()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        // MinScanRange = 20: distance 10 is inside the dead zone.
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));

        StepPastFirstScan(game);

        Assert.False(ModuleOf(scanner).PendingActivationTargetId.IsValid);
    }

    [Fact]
    public void CandidateBeyondMaxScanRange_IsRejected()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        // MaxScanRange = 100: distance 500 is outside the scan band.
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(500, 0, 0));

        StepPastFirstScan(game);

        Assert.False(ModuleOf(scanner).PendingActivationTargetId.IsValid);
    }

    [Fact]
    public void AllowSelfNo_NoExternalCandidate_NoActivation()
    {
        var game = NewGame();
        // Scanner itself matches Query's +INFANTRY filter, but AllowSelf = No and no other
        // candidate is present.
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);

        StepPastFirstScan(game);

        Assert.False(ModuleOf(scanner).PendingActivationTargetId.IsValid);
    }

    [Fact]
    public void AllowSelfYes_NoExternalCandidate_ActivationTargetsSelf()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("SelfScanner", game.CivilianPlayer, Vector3.Zero);

        StepPastFirstScan(game);

        Assert.True(ModuleOf(scanner).TryConsumePendingActivation(out var targetId));
        Assert.Equal(scanner.Id, targetId);
    }

    [Fact]
    public void CandidateWithForbiddenStatus_IsRejected()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("ForbiddenStatusScanner", game.CivilianPlayer, Vector3.Zero);
        var candidate = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        candidate.SetObjectStatus(ObjectStatus.Unselectable, true);

        StepPastFirstScan(game);

        Assert.False(ModuleOf(scanner).PendingActivationTargetId.IsValid);
    }

    [Fact]
    public void CandidateMatchingNoQuery_IsRejected()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        // Bunker is STRUCTURE-kind; Scanner's Query only accepts +INFANTRY.
        game.SpawnObject("Bunker", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));

        StepPastFirstScan(game);

        Assert.False(ModuleOf(scanner).PendingActivationTargetId.IsValid);
    }

    [Fact]
    public void TryConsumePendingActivation_ClearsUntilNextScan()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        var candidate = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(scanner);

        StepPastFirstScan(game);

        Assert.True(module.TryConsumePendingActivation(out var targetId));
        Assert.Equal(candidate.Id, targetId);

        // Nothing left pending until the next scan re-arms it.
        Assert.False(module.TryConsumePendingActivation(out _));
    }

    [Fact]
    public void BaseMaxRangeFromStartPos_LeashSuppressesActivation_WhenMovedPastLeash()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("LeashedScanner", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(scanner);

        StepPastFirstScan(game);
        Assert.True(module.PendingActivationTargetId.IsValid); // sanity: activation available before the leash trips

        // Relocate the scanner itself well past MaxScanRange (50) from its recorded start
        // position, same idiom EnemyNearUpdateContractTests.RunScenario uses to move an object
        // mid-run (UpdateTransform + UpdateColliders so the partition/position read sees it).
        scanner.UpdateTransform(new Vector3(500, 0, 0));
        scanner.UpdateColliders();

        // Consume the stale pending activation, then step again: with the scanner beyond the
        // leash, a fresh scan (even with an in-range, filter-matching candidate relative to the
        // scanner's new position) must not set a new one.
        module.TryConsumePendingActivation(out _);
        StepPastFirstScan(game);

        Assert.False(module.PendingActivationTargetId.IsValid);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var scannerHost = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));

        StepPastFirstScan(game);
        var live = ModuleOf(scannerHost);

        var shadowHost = game.SpawnObject("Scanner", game.CivilianPlayer, new Vector3(1000, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_WithPendingActivationSet_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 3);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static bool[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var scanner = game.SpawnObject("RoundTripScanner", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(scanner);

        var trajectory = new bool[10];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;     // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();

            if (i == 5)
            {
                // Exercise the falling edge: consume the pending activation mid-run so both
                // trajectories show a real toggle (the next scan, one frame later, re-arms it).
                module.TryConsumePendingActivation(out _);
            }

            trajectory[i] = module.PendingActivationTargetId.IsValid;
        }

        return trajectory;
    }
}
