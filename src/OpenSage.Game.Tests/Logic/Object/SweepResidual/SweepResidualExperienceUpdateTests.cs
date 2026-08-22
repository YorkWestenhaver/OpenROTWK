// R15 L1-11 (sweep ratchet), residual crash class 7ddb1597:
// "ExperienceUpdate.levelUp / ArgumentOutOfRangeException (List.RemoveAt)", 2 of the 9 stage-A
// failures in the frozen 20-map AotR sweep at main 9bde4556 ("map good isengard" and
// "map sp evil erebor"), reported by the crash-context line as
// `frame=1 | object=#23 RohanEntOak | module=ExperienceUpdate`.
//
// Root cause: ExperienceUpdate.Initialize drains the object's ExperienceLevel list with
//     while ((int)GameObject.Rank >= _nextLevel.Rank) levelUp();
// levelUp() pops the head of _experienceLevels but only advances _nextLevel while the list is
// non-empty, so once the list drains _nextLevel still points at the last level consumed and
// the rank test stays true. The next levelUp() then called _experienceLevels.RemoveAt(0) on an
// empty list. Any object that STARTS at or above the highest ExperienceLevel rank declared for
// it hits this - and GameObject adds the ExperienceUpdate helper module to every object it
// constructs (GameObject.cs "ModuleTag_ExperienceHelper"), so it needs no ExperienceUpdate
// block in the object's INI. The throw came out of the very first GameLogic.Update, i.e. the
// match ended on logic frame 1.
//
// Fixed behavior asserted here: the drain loop also stops when the list runs dry, levelUp()
// never pops an empty list, and the object simply ends up with no further level to gain.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.SweepResidual;

public class SweepResidualExperienceUpdateTests
{
    private const string Definitions = @"
Upgrade Upgrade_Veterancy_VETERAN
  Type = OBJECT
End

Upgrade Upgrade_Veterancy_ELITE
  Type = OBJECT
End

Upgrade Upgrade_Veterancy_HEROIC
  Type = OBJECT
End

; Starts at Heroic (rank 3) but only ONE ExperienceLevel is declared for it, at rank 1:
; the list drains on the first pass and the rank test is still satisfied. This is the
; RohanEntOak shape.
Object SweepOverLevelledEnt
  KindOf = INFANTRY SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ExperienceLevelCreate ModuleTag_Rank
    LevelToGrant = 3
  End
End

ExperienceLevel SweepEntLevel1
  TargetNames = SweepOverLevelledEnt
  RequiredExperience = 10
  Rank = 1
End

; Control: starts at Regular, with a level still ahead of it. The drain loop must not run.
Object SweepFreshRecruit
  KindOf = INFANTRY SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

ExperienceLevel SweepRecruitLevel1
  TargetNames = SweepFreshRecruit
  RequiredExperience = 25
  Rank = 1
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xE47D);
        game.LoadIniText(Definitions);
        return game;
    }

    [Fact]
    public void ObjectStartingAboveItsTopExperienceLevel_FirstUpdate_DoesNotThrow()
    {
        var game = NewGame();
        var ent = game.SpawnObject("SweepOverLevelledEnt", game.CivilianPlayer, Vector3.Zero);

        // The regression: ExperienceUpdate.Initialize runs on the first update, and its drain
        // loop used to call RemoveAt(0) on an already-empty list here.
        game.Step();

        Assert.Contains(ent, game.GameLogic.Objects);
    }

    [Fact]
    public void ObjectStartingAboveItsTopExperienceLevel_HasNoFurtherLevelToGain()
    {
        var game = NewGame();
        var ent = game.SpawnObject("SweepOverLevelledEnt", game.CivilianPlayer, Vector3.Zero);

        // T+1: SpawnObject registers the helper module for the frame it was created on, and
        // the sleepy list only reaches it on the following Step - so the first Step spawns and
        // the second is the one that runs ExperienceUpdate.Initialize. (INT-R2A: the packet
        // authored one Step here; measured afterOne=0, afterTwo=25 on the control shape.)
        game.Step();
        game.Step();

        // The list drained: levelUp's empty-list branch sets the "no next level" sentinel.
        Assert.Equal(int.MaxValue, ent.ExperienceRequiredForNextLevel);
    }

    [Fact]
    public void ObjectStartingAboveItsTopExperienceLevel_KeepsUpdatingOnLaterFrames()
    {
        var game = NewGame();
        var ent = game.SpawnObject("SweepOverLevelledEnt", game.CivilianPlayer, Vector3.Zero);

        // T+1 and beyond: the guard must hold every frame, not just the initialising one.
        game.Step();
        game.Step();
        game.Step();

        Assert.Contains(ent, game.GameLogic.Objects);
    }

    [Fact]
    public void ObjectBelowItsNextExperienceLevel_StillTracksThatLevelsRequirement()
    {
        var game = NewGame();
        var recruit = game.SpawnObject("SweepFreshRecruit", game.CivilianPlayer, Vector3.Zero);

        // T+1, same reason as above: Initialize lands on the second Step.
        game.Step();
        game.Step();

        // Control: the guard must not short-circuit ordinary progression.
        Assert.Equal(VeterancyLevel.Regular, recruit.Rank);
        Assert.Equal(25, recruit.ExperienceRequiredForNextLevel);
    }
}
