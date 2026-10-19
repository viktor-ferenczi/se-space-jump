using System;
using VRageMath;

namespace ClientPlugin.Core;

/// <summary>
/// Full resolution snapshot of the blocks of a grid, one cell per grid cell.
/// Airtightness of the blocks is answered by the callbacks.
/// </summary>
public sealed class SnapshotAirCells : IAirCells
{
    private readonly object[] blocks;
    private readonly Vector3I size;
    private readonly Func<object, bool?> wholeBlockAirtight;
    private readonly Func<object, Vector3I, Vector3I, bool> faceAirtight;

    public Vector3I Min { get; }
    public Vector3I Max { get; }
    public float CellSize { get; }
    public Vector3D Origin => Vector3D.Zero;

    public SnapshotAirCells(Vector3I min, Vector3I max, float gridSize, Func<object, bool?> wholeBlockAirtight, Func<object, Vector3I, Vector3I, bool> faceAirtight)
    {
        Min = min;
        Max = max;
        CellSize = gridSize;
        size = max - min + Vector3I.One;
        blocks = new object[size.X * size.Y * size.Z];
        this.wholeBlockAirtight = wholeBlockAirtight;
        this.faceAirtight = faceAirtight;
    }

    private bool TryIndex(Vector3I position, out int index)
    {
        var p = position - Min;
        if (p.X < 0 || p.Y < 0 || p.Z < 0 || p.X >= size.X || p.Y >= size.Y || p.Z >= size.Z)
        {
            index = -1;
            return false;
        }

        index = (p.Z * size.Y + p.Y) * size.X + p.X;
        return true;
    }

    public void Set(Vector3I position, object block)
    {
        if (TryIndex(position, out var index))
            blocks[index] = block;
    }

    public object GetBlock(Vector3I position) => TryIndex(position, out var index) ? blocks[index] : null;

    public bool? IsWholeBlockAirtight(object block) => wholeBlockAirtight(block);

    public bool IsFaceAirtight(object block, Vector3I position, Vector3I normal) => faceAirtight(block, position, normal);
}

/// <summary>
/// Reduced resolution fallback for grids too large for a full resolution map: one cell per Step^3 grid cells,
/// and every cell holding any block is treated as an airtight wall. Conservative: openings narrower than a
/// coarse cell are closed, so smoke is kept out of rooms it might reach, never let into sealed ones.
/// </summary>
public sealed class CoarseAirCells : IAirCells
{
    private static readonly object Wall = new object();

    private readonly bool[] occupied;
    private readonly Vector3I size;

    public readonly int Step;
    public Vector3I Min { get; }
    public Vector3I Max { get; }
    public float CellSize { get; }
    public Vector3D Origin { get; }

    public CoarseAirCells(Vector3I gridMin, Vector3I gridMax, float gridSize, int step)
    {
        Step = step;
        Min = FloorDiv(gridMin, step);
        Max = FloorDiv(gridMax, step);
        CellSize = gridSize * step;
        Origin = new Vector3D((step - 1) * 0.5 * gridSize);
        size = Max - Min + Vector3I.One;
        occupied = new bool[size.X * size.Y * size.Z];
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    private static Vector3I FloorDiv(Vector3I a, int b) => new Vector3I(FloorDiv(a.X, b), FloorDiv(a.Y, b), FloorDiv(a.Z, b));

    private bool TryIndex(Vector3I position, out int index)
    {
        var p = position - Min;
        if (p.X < 0 || p.Y < 0 || p.Z < 0 || p.X >= size.X || p.Y >= size.Y || p.Z >= size.Z)
        {
            index = -1;
            return false;
        }

        index = (p.Z * size.Y + p.Y) * size.X + p.X;
        return true;
    }

    /// <summary>Marks the grid cells min..max (grid coordinates) as occupied</summary>
    public void MarkGridCells(Vector3I min, Vector3I max)
    {
        var cmin = FloorDiv(min, Step);
        var cmax = FloorDiv(max, Step);
        for (var z = cmin.Z; z <= cmax.Z; z++)
        for (var y = cmin.Y; y <= cmax.Y; y++)
        for (var x = cmin.X; x <= cmax.X; x++)
        {
            if (TryIndex(new Vector3I(x, y, z), out var index))
                occupied[index] = true;
        }
    }

    public object GetBlock(Vector3I position) => TryIndex(position, out var index) && occupied[index] ? Wall : null;

    public bool? IsWholeBlockAirtight(object block) => true;

    public bool IsFaceAirtight(object block, Vector3I position, Vector3I normal) => true;
}
