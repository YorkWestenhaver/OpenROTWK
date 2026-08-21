// Contract tests for R13's oracle exp-001 finding #1 fix: HeadlessSimGame.SpawnObject snaps
// a map-spawned object's z to authored-z-plus-terrain-height at (x,y), instead of leaving z at
// the raw authored value. This mirrors GameLogic.cpp's non-bridge/non-road MapObject load loop
// (GPL): `pos.z += TheTerrainLogic->getGroundHeight(pos.x, pos.y)` runs unconditionally for
// every spawned object - no KindOf/airborne gating - so the fix here is unconditional too.
//
// HeadlessSimGame's default terrain (TerrainLogic.cs ctor) is a flat 2x2 all-zero heightmap,
// so every other contract test that calls SpawnObject at an authored z (e.g. the parachute
// tests' z=200 "in the air" spawns) is unaffected: 200 + 0 == 200. These tests build a
// non-flat heightmap to make the snap observable, and separately assert the additive (not
// overwriting) semantics against that same non-flat terrain so a future change can't silently
// turn `+=` into `=` and still pass a flat-terrain-only test.

using System.Numerics;
using OpenSage.Data.Map;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Sim;

public class SpawnTerrainSnapContractTests
{
    private const string Definitions = @"
Object TestUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    // 2x2 grid, every corner the same elevation, so GetHeight(x,y) is constant across the
    // bilinear interpolation regardless of exactly where (x,y) lands in the cell - the test
    // isolates "does the snap happen and is it additive", not heightmap sampling itself.
    // Elevation 8 * the un-scaled (Version 0) VerticalScale 0.625 = ground height 5.0.
    private const float GroundHeight = 5.0f;

    private static HeadlessSimGame NewGame(uint seed = 0x5A31)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        game.TerrainLogic.SetHeightMapData(
            HeightMapData.Create(0, new ushort[2, 2] { { 8, 8 }, { 8, 8 } }));
        return game;
    }

    [Fact]
    public void SpawnObject_SnapsAuthoredZeroZToGroundHeight()
    {
        var game = NewGame();

        var obj = game.SpawnObject("TestUnit", game.CivilianPlayer, new Vector3(5, 5, 0));

        Assert.Equal(GroundHeight, obj.Translation.Z, 3);
    }

    [Fact]
    public void SpawnObject_AddsGroundHeightToNonzeroAuthoredZ()
    {
        // GPL's `pos.z += getGroundHeight(...)` is additive, not an overwrite: an object
        // authored with a nonzero z offset (e.g. a deliberately elevated placement) keeps
        // that offset on top of the terrain height, it isn't replaced by it.
        var game = NewGame();

        var obj = game.SpawnObject("TestUnit", game.CivilianPlayer, new Vector3(5, 5, 200));

        Assert.Equal(200f + GroundHeight, obj.Translation.Z, 3);
    }

    [Fact]
    public void SpawnObject_OnFlatDefaultTerrain_LeavesAuthoredZUnchanged()
    {
        // Regression guard for the existing airborne-spawn contract tests (parachute etc.):
        // HeadlessSimGame's default heightmap is flat zero, so authored z=200 must stay 200,
        // not silently pick up a nonzero snap from some other default.
        var game = new HeadlessSimGame(SageGame.Bfme2);
        game.LoadIniText(Definitions);

        var obj = game.SpawnObject("TestUnit", game.CivilianPlayer, new Vector3(0, 0, 200));

        Assert.Equal(200f, obj.Translation.Z, 3);
    }

    [Fact]
    public void SpawnObject_PreservesXAndY()
    {
        var game = NewGame();

        var obj = game.SpawnObject("TestUnit", game.CivilianPlayer, new Vector3(5, 5, 0));

        Assert.Equal(5f, obj.Translation.X, 3);
        Assert.Equal(5f, obj.Translation.Y, 3);
    }
}
