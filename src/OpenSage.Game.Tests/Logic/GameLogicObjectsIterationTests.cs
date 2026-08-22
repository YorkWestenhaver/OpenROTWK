// R15 FIX-1 guard tests for GameLogic.Objects.
//
// The AotR lithlad headed gate died with "Collection was modified; enumeration operation may
// not execute" inside PlayerManager.LogicTick: GameLogic.Objects used to hand every caller ONE
// shared List<GameObject> that it Cleared and refilled on each property access, so a RE-ENTRANT
// walk - LiveAiWorldView.EnsureSnapshot iterating Objects while CastleUnpackStamper
// .FindStructureOnPlot, called from its loop body, iterated Objects again - yanked the outer
// enumerator's backing store out from under it.
//
// These tests pin the two properties the sim actually depends on:
//   * a nested enumeration must not disturb the enclosing one (the crash), and
//   * an enumeration must stay a stable snapshot across spawns/destroys during the walk
//     (the property the shared buffer was there to provide in the first place).

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic;

public class GameLogicObjectsIterationTests
{
    private const string Definitions = @"
Object IterWidget
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(int objectCount)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xF1C1);
        game.LoadIniText(Definitions);

        for (var i = 0; i < objectCount; i++)
        {
            game.SpawnObject("IterWidget", game.CivilianPlayer, new Vector3(i * 10, 0, 0));
        }

        return game;
    }

    [Fact]
    public void NestedEnumeration_DoesNotDisturbTheOuterWalk()
    {
        var game = NewGame(objectCount: 5);
        var expected = game.GameLogic.Objects.Count();

        var outerSeen = 0;
        foreach (var _ in game.GameLogic.Objects)
        {
            // Exactly the shape that crashed: a full inner walk from inside the outer loop body.
            var innerSeen = game.GameLogic.Objects.Count(o => o != null);
            Assert.Equal(expected, innerSeen);
            outerSeen++;
        }

        Assert.Equal(expected, outerSeen);
    }

    [Fact]
    public void ThreeDeepNesting_EachLevelSeesTheWholeSet()
    {
        var game = NewGame(objectCount: 4);
        var expected = game.GameLogic.Objects.Count();

        var visits = 0;
        foreach (var _ in game.GameLogic.Objects)
        {
            foreach (var __ in game.GameLogic.Objects)
            {
                Assert.Equal(expected, game.GameLogic.Objects.Count());
                visits++;
            }
        }

        Assert.Equal(expected * expected, visits);
    }

    [Fact]
    public void SpawningDuringEnumeration_DoesNotThrowAndKeepsTheSnapshotStable()
    {
        var game = NewGame(objectCount: 3);
        var expected = game.GameLogic.Objects.Count();

        var seen = 0;
        foreach (var _ in game.GameLogic.Objects)
        {
            if (seen == 0)
            {
                game.SpawnObject("IterWidget", game.CivilianPlayer, new Vector3(999, 0, 0));
            }

            seen++;
        }

        // The walk in progress saw the pre-spawn snapshot; the next walk sees the new object.
        Assert.Equal(expected, seen);
        Assert.Equal(expected + 1, game.GameLogic.Objects.Count());
    }

    [Fact]
    public void AbandonedEnumeration_DoesNotCorruptTheNextWalk()
    {
        // A `break` disposes the enumerator early; the buffer must go back to the pool clean,
        // or the next caller inherits a half-filled snapshot.
        var game = NewGame(objectCount: 6);
        var expected = game.GameLogic.Objects.Count();

        foreach (var _ in game.GameLogic.Objects)
        {
            break;
        }

        Assert.Equal(expected, game.GameLogic.Objects.Count());
    }

    [Fact]
    public void ConcurrentEnumerators_AreIndependent()
    {
        var game = NewGame(objectCount: 4);
        var all = game.GameLogic.Objects.ToList();

        using var a = game.GameLogic.Objects.GetEnumerator();
        using var b = game.GameLogic.Objects.GetEnumerator();

        var fromA = new List<GameObject>();
        var fromB = new List<GameObject>();

        while (a.MoveNext() && b.MoveNext())
        {
            fromA.Add(a.Current);
            fromB.Add(b.Current);
        }

        Assert.Equal(all, fromA);
        Assert.Equal(all, fromB);
    }
}
