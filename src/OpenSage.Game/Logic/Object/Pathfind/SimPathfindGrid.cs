// S5 pathfinding - the pathfind grid (GPL Pathfinder's m_map of PathfindCell).
//
// Behavioral reference (clean-room, semantics only): AIPathfind.cpp/h -
// PathfindCell::CellType, classifyMap/classifyMapCell (pinched expansion around cliffs),
// validLocomotorSurfacesForCellType, validMovementPosition, worldToCell (floor /10 with
// clamp-and-report-overflow), setTypeAsObstacle/removeObstacle.
//
// All state is int/Fix64; cell size is the rate-free constant 10 world units.
// Classification note (PATH-F9, design note): the Fix64 terrain seam exposes ground
// height only (no cliff/water classifiers yet), and the headless map is flat - so cells
// classify CLEAR by default, structures stamp OBSTACLE footprints, and tests may stamp
// terrain types directly. Cliff/water classification lands with the terrain Fix64 port.

using System;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object.Pathfind;

/// <summary>GPL PathfindCell::CellType.</summary>
public enum SimPathfindCellType : byte
{
    Clear = 0,
    Water = 1,
    Cliff = 2,
    Rubble = 3,
    Obstacle = 4,
    BridgeImpassable = 5,
    Impassable = 6,
}

[SimState]
public sealed class SimPathfindGrid
{
    /// <summary>GPL PATHFIND_CELL_SIZE = 10 world units (rate-free).</summary>
    public const int CellSize = 10;

    private static readonly Fix64 CellSizeFix = Fix64.FromRaw(10L << 32);

    // Inclusive cell-index bounds (GPL m_extent.lo/hi).
    private readonly int _loX;
    private readonly int _loY;
    private readonly int _hiX;
    private readonly int _hiY;
    private readonly int _width;
    private readonly int _height;

    // Per-cell terrain type (before obstacle overlay), pinched flag, obstacle occupancy.
    // Obstacle overlay is a separate field so removal restores the terrain type exactly
    // (GPL removeObstacle reclassifies; the terrain inputs here are static).
    private readonly byte[] _terrainType;
    private readonly bool[] _pinched;
    private readonly uint[] _obstacleId;

    public SimPathfindGrid(int loX, int loY, int hiX, int hiY)
    {
        if (hiX < loX || hiY < loY)
        {
            throw new ArgumentException("Degenerate pathfind extent");
        }
        _loX = loX;
        _loY = loY;
        _hiX = hiX;
        _hiY = hiY;
        _width = hiX - loX + 1;
        _height = hiY - loY + 1;
        _terrainType = new byte[_width * _height];
        _pinched = new bool[_width * _height];
        _obstacleId = new uint[_width * _height];
    }

    public int LoX => _loX;
    public int LoY => _loY;
    public int HiX => _hiX;
    public int HiY => _hiY;
    public int CellCount => _width * _height;

    public bool Contains(int x, int y) => x >= _loX && x <= _hiX && y >= _loY && y <= _hiY;

    /// <summary>Flat index for search bookkeeping arrays; requires Contains(x,y).</summary>
    public int CellIndex(int x, int y) => (y - _loY) * _width + (x - _loX);

    public int CellXOf(int cellIndex) => _loX + (cellIndex % _width);
    public int CellYOf(int cellIndex) => _loY + (cellIndex / _width);

    private static int FloorToInt(Fix64 value) => (int)(value.RawValue >> 32);

    /// <summary>
    /// GPL Pathfinder::worldToCell - floor(pos/10) clamped into the extent; returns TRUE
    /// when the position was outside (the overflow flag).
    /// </summary>
    public bool WorldToCell(in FixVector3 pos, out int x, out int y)
    {
        x = FloorToInt(pos.X / CellSizeFix);
        y = FloorToInt(pos.Y / CellSizeFix);
        var overflow = false;
        if (x < _loX) { overflow = true; x = _loX; }
        if (y < _loY) { overflow = true; y = _loY; }
        if (x > _hiX) { overflow = true; x = _hiX; }
        if (y > _hiY) { overflow = true; y = _hiY; }
        return overflow;
    }

    /// <summary>Cell center in world units (GPL adjustCoordToCell, centerInCell=true).</summary>
    public static FixVector3 CellCenter(int x, int y)
    {
        var half = Fix64.FromRaw(5L << 32);
        return new FixVector3(
            Fix64.FromRaw((long)x * CellSize << 32) + half,
            Fix64.FromRaw((long)y * CellSize << 32) + half,
            Fix64.Zero);
    }

    /// <summary>Cell corner in world units (GPL adjustCoordToCell, centerInCell=false).</summary>
    public static FixVector3 CellCorner(int x, int y)
    {
        return new FixVector3(
            Fix64.FromRaw((long)x * CellSize << 32),
            Fix64.FromRaw((long)y * CellSize << 32),
            Fix64.Zero);
    }

    /// <summary>Effective type: obstacle overlay wins over terrain (GPL setTypeAsObstacle).</summary>
    public SimPathfindCellType GetCellType(int x, int y)
    {
        var i = CellIndex(x, y);
        return _obstacleId[i] != 0
            ? SimPathfindCellType.Obstacle
            : (SimPathfindCellType)_terrainType[i];
    }

    public uint GetObstacleId(int x, int y) => _obstacleId[CellIndex(x, y)];

    public bool GetPinched(int x, int y) => _pinched[CellIndex(x, y)];

    /// <summary>Terrain classification write (tests / future terrain port).</summary>
    public void SetTerrainType(int x, int y, SimPathfindCellType type)
        => _terrainType[CellIndex(x, y)] = (byte)type;

    /// <summary>
    /// Stamps a structure's footprint rectangle as OBSTACLE (GPL classifyObjectFootprint,
    /// insert=true). Cell coordinates are inclusive and clipped to the extent. The first
    /// stamper of a cell owns it (GPL keeps one obstacle id per cell).
    /// </summary>
    public void StampObstacle(uint objectId, int cellLoX, int cellLoY, int cellHiX, int cellHiY)
    {
        for (var y = (cellLoY > _loY ? cellLoY : _loY); y <= (cellHiY < _hiY ? cellHiY : _hiY); y++)
        {
            for (var x = (cellLoX > _loX ? cellLoX : _loX); x <= (cellHiX < _hiX ? cellHiX : _hiX); x++)
            {
                var i = CellIndex(x, y);
                if (_obstacleId[i] == 0)
                {
                    _obstacleId[i] = objectId;
                }
            }
        }
    }

    /// <summary>GPL classifyObjectFootprint, insert=false: only this object's cells clear.</summary>
    public void RemoveObstacle(uint objectId, int cellLoX, int cellLoY, int cellHiX, int cellHiY)
    {
        for (var y = (cellLoY > _loY ? cellLoY : _loY); y <= (cellHiY < _hiY ? cellHiY : _hiY); y++)
        {
            for (var x = (cellLoX > _loX ? cellLoX : _loX); x <= (cellHiX < _hiX ? cellHiX : _hiX); x++)
            {
                var i = CellIndex(x, y);
                if (_obstacleId[i] == objectId)
                {
                    _obstacleId[i] = 0;
                }
            }
        }
    }

    /// <summary>
    /// GPL classifyMap's post-pass: CLEAR cells 8-adjacent to a CLIFF cell are pinched
    /// (a cost penalty, not impassability). Run after terrain types settle.
    /// </summary>
    public void RecomputePinched()
    {
        Array.Clear(_pinched, 0, _pinched.Length);
        for (var y = _loY; y <= _hiY; y++)
        {
            for (var x = _loX; x <= _hiX; x++)
            {
                if ((SimPathfindCellType)_terrainType[CellIndex(x, y)] != SimPathfindCellType.Cliff)
                {
                    continue;
                }
                for (var ny = y - 1; ny <= y + 1; ny++)
                {
                    for (var nx = x - 1; nx <= x + 1; nx++)
                    {
                        if (!Contains(nx, ny))
                        {
                            continue;
                        }
                        var ni = CellIndex(nx, ny);
                        if ((SimPathfindCellType)_terrainType[ni] == SimPathfindCellType.Clear)
                        {
                            _pinched[ni] = true;
                        }
                    }
                }
            }
        }
    }

    /// <summary>GPL validLocomotorSurfacesForCellType - the cell-type/surface-mask table.</summary>
    public static Surfaces SurfacesForCellType(SimPathfindCellType type) => type switch
    {
        SimPathfindCellType.Clear => Surfaces.Ground | Surfaces.Air,
        SimPathfindCellType.Water => Surfaces.Water | Surfaces.Air,
        SimPathfindCellType.Rubble => Surfaces.Rubble | Surfaces.Air,
        SimPathfindCellType.Cliff => Surfaces.Cliff | Surfaces.Air,
        _ => Surfaces.Air, // OBSTACLE / IMPASSABLE / BRIDGE_IMPASSABLE
    };

    /// <summary>
    /// GPL validMovementPosition(isCrusher, acceptableSurfaces, toCell): the ignored
    /// obstacle escape, then the surface-mask intersection test. (Crusher-through-fence
    /// awaits fences.)
    /// </summary>
    public bool IsValidMovementCell(Surfaces acceptableSurfaces, int x, int y, uint ignoreObstacleId)
    {
        if (!Contains(x, y))
        {
            return false;
        }
        var i = CellIndex(x, y);
        var obstacle = _obstacleId[i];
        if (obstacle != 0 && obstacle == ignoreObstacleId)
        {
            return true;
        }
        var type = obstacle != 0 ? SimPathfindCellType.Obstacle : (SimPathfindCellType)_terrainType[i];
        return (SurfacesForCellType(type) & acceptableSurfaces) != 0;
    }

    /// <summary>
    /// Footprint validity for a unit of pathfind radius <paramref name="radius"/>
    /// (GPL checkDestination's i/j scan shape: cells [c-r, c+r) plus one when centered).
    /// </summary>
    public bool IsValidMovementFootprint(
        Surfaces acceptableSurfaces, int cellX, int cellY, int radius, bool centerInCell,
        uint ignoreObstacleId)
    {
        var numCellsAbove = centerInCell ? radius + 1 : radius;
        for (var x = cellX - radius; x < cellX + numCellsAbove; x++)
        {
            for (var y = cellY - radius; y < cellY + numCellsAbove; y++)
            {
                if (!IsValidMovementCell(acceptableSurfaces, x, y, ignoreObstacleId))
                {
                    return false;
                }
            }
        }
        return true;
    }
}
