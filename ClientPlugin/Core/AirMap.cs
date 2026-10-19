using System;
using System.Collections.Generic;
using VRageMath;

namespace ClientPlugin.Core;

/// <summary>
/// Cell lookup an <see cref="AirMap"/> is built from, in the integer coordinates of the map's cells.
/// Implementations must be safe to read from a worker thread: build them from a snapshot.
/// </summary>
public interface IAirCells
{
    /// <summary>First occupied cell (inclusive)</summary>
    Vector3I Min { get; }

    /// <summary>Last occupied cell (inclusive)</summary>
    Vector3I Max { get; }

    /// <summary>Edge length of one cell in meters</summary>
    float CellSize { get; }

    /// <summary>Local position of the center of cell (0,0,0), in grid local meters</summary>
    Vector3D Origin { get; }

    /// <summary>Block occupying the cell, null if the cell is empty or outside Min..Max</summary>
    object GetBlock(Vector3I position);

    /// <summary>Same as MyGridGasSystem.IsAirtightFromDefinition: true or false for the whole block, null if it depends on the face</summary>
    bool? IsWholeBlockAirtight(object block);

    /// <summary>Same as MyGridGasSystem.IsAirtightBlock: whether the block seals the face of the given cell towards the normal</summary>
    bool IsFaceAirtight(object block, Vector3I position, Vector3I normal);
}

/// <summary>
/// Which cells of a grid (plus padding around it) are reachable by smoke from open space through faces
/// that are not airtight, using the same rule as the game's oxygen rooms (MyCubeGridGasContext.IsAirtightBetweenPositions).
/// Cells that are not reachable and are not filled by an airtight block are "sealed air": the inside of
/// pressurized rooms. Smoke must never be drawn there, so every reachable cell also stores its clearance,
/// the Chebyshev distance in cells to the nearest sealed air cell.
/// </summary>
public sealed class AirMap
{
    public const int MaxClearance = 64;

    /// <summary>Padded box: first cell</summary>
    public readonly Vector3I Min;

    /// <summary>Padded box: number of cells along each axis</summary>
    public readonly Vector3I Size;

    /// <summary>Unpadded box, every sealed air cell is inside it</summary>
    public readonly Vector3I InnerMin, InnerMax;

    public readonly float CellSize;
    public readonly Vector3D Origin;
    public readonly int Pad;

    // BFS steps from the padded boundary, -1 for cells smoke cannot reach
    private readonly short[] depth;

    // Chebyshev distance in cells to the nearest sealed air cell, 0 for sealed air
    private readonly byte[] clearance;

    /// <summary>Reachable cells inside the unpadded box or close to the hull, used to place puffs near the ship</summary>
    public readonly List<Vector3I> NearCells = new List<Vector3I>();

    public int ReachedCount { get; private set; }
    public int SealedCount { get; private set; }

    public int CellCount => Size.X * Size.Y * Size.Z;

    private AirMap(IAirCells cells, int pad)
    {
        Pad = pad;
        CellSize = cells.CellSize;
        Origin = cells.Origin;
        InnerMin = cells.Min;
        InnerMax = cells.Max;
        Min = cells.Min - new Vector3I(pad);
        Size = cells.Max - cells.Min + new Vector3I(2 * pad + 1);
        depth = new short[CellCount];
        clearance = new byte[CellCount];
    }

    /// <summary>Number of cells a map of these cells would have</summary>
    public static long CountCells(Vector3I min, Vector3I max, int pad)
    {
        var size = max - min + new Vector3I(2 * pad + 1);
        return (long)size.X * size.Y * size.Z;
    }

    public static AirMap Build(IAirCells cells, int pad)
    {
        if (pad < 1)
            throw new ArgumentOutOfRangeException(nameof(pad));

        var map = new AirMap(cells, pad);
        map.FloodFromOutside(cells);
        map.ComputeClearance(cells);
        map.CollectNearCells();
        return map;
    }

    private int Index(int x, int y, int z) => (z * Size.Y + y) * Size.X + x;

    private Vector3I Cell(int index)
    {
        var x = index % Size.X;
        var yz = index / Size.X;
        return new Vector3I(x, yz % Size.Y, yz / Size.Y);
    }

    private static readonly Vector3I[] Directions =
    {
        new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0),
        new Vector3I(0, 1, 0), new Vector3I(0, -1, 0),
        new Vector3I(0, 0, 1), new Vector3I(0, 0, -1),
    };

    private void FloodFromOutside(IAirCells cells)
    {
        for (var i = 0; i < depth.Length; i++)
            depth[i] = -1;

        // The padding is empty, so the whole boundary of the padded box is open space
        var queue = new Queue<int>();
        for (var z = 0; z < Size.Z; z++)
        for (var y = 0; y < Size.Y; y++)
        for (var x = 0; x < Size.X; x++)
        {
            if (x != 0 && y != 0 && z != 0 && x != Size.X - 1 && y != Size.Y - 1 && z != Size.Z - 1)
                continue;

            var index = Index(x, y, z);
            depth[index] = 0;
            queue.Enqueue(index);
        }

        while (queue.Count != 0)
        {
            var index = queue.Dequeue();
            var local = Cell(index);
            var position = local + Min;
            var block = cells.GetBlock(position);
            var next = (short)Math.Min(depth[index] + 1, short.MaxValue);

            foreach (var direction in Directions)
            {
                var neighbor = local + direction;
                if (neighbor.X < 0 || neighbor.Y < 0 || neighbor.Z < 0 || neighbor.X >= Size.X || neighbor.Y >= Size.Y || neighbor.Z >= Size.Z)
                    continue;

                var neighborIndex = Index(neighbor.X, neighbor.Y, neighbor.Z);
                if (depth[neighborIndex] >= 0)
                    continue;

                var neighborBlock = cells.GetBlock(position + direction);
                if (IsAirtightBetween(cells, block, position, neighborBlock, position + direction, direction))
                    continue;

                depth[neighborIndex] = next;
                queue.Enqueue(neighborIndex);
            }
        }
    }

    /// <summary>MyCubeGridGasContext.IsAirtightBetweenPositions on snapshot data</summary>
    private static bool IsAirtightBetween(IAirCells cells, object block, Vector3I position, object neighborBlock, Vector3I neighborPosition, Vector3I direction)
    {
        if (ReferenceEquals(block, neighborBlock))
            return block != null && cells.IsWholeBlockAirtight(block) == true;

        if (block != null && cells.IsFaceAirtight(block, position, direction))
            return true;

        return neighborBlock != null && cells.IsFaceAirtight(neighborBlock, neighborPosition, -direction);
    }

    private void ComputeClearance(IAirCells cells)
    {
        // Multi-source BFS from the sealed air cells over all 26 neighbors (Chebyshev distance)
        var queue = new Queue<int>();
        var reached = 0;
        var sealedAir = 0;
        for (var index = 0; index < depth.Length; index++)
        {
            clearance[index] = byte.MaxValue;
            if (depth[index] >= 0)
            {
                reached++;
                continue;
            }

            var block = cells.GetBlock(Cell(index) + Min);
            if (block != null && cells.IsWholeBlockAirtight(block) == true)
                continue;

            clearance[index] = 0;
            sealedAir++;
            queue.Enqueue(index);
        }

        ReachedCount = reached;
        SealedCount = sealedAir;

        while (queue.Count != 0)
        {
            var index = queue.Dequeue();
            var next = clearance[index] + 1;
            if (next > MaxClearance)
                continue;

            var local = Cell(index);
            for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var x = local.X + dx;
                var y = local.Y + dy;
                var z = local.Z + dz;
                if (x < 0 || y < 0 || z < 0 || x >= Size.X || y >= Size.Y || z >= Size.Z)
                    continue;

                var neighborIndex = Index(x, y, z);
                if (clearance[neighborIndex] <= next)
                    continue;

                clearance[neighborIndex] = (byte)next;
                queue.Enqueue(neighborIndex);
            }
        }
    }

    private void CollectNearCells()
    {
        for (var index = 0; index < depth.Length; index++)
        {
            if (depth[index] < 0)
                continue;

            var position = Cell(index) + Min;
            var inside = position.X >= InnerMin.X && position.Y >= InnerMin.Y && position.Z >= InnerMin.Z &&
                         position.X <= InnerMax.X && position.Y <= InnerMax.Y && position.Z <= InnerMax.Z;
            if (inside || clearance[index] <= 3)
                NearCells.Add(position);
        }
    }

    /// <summary>Cell containing a grid local position</summary>
    public Vector3I CellAt(Vector3D local)
    {
        var p = (local - Origin) / CellSize;
        return new Vector3I((int)Math.Round(p.X), (int)Math.Round(p.Y), (int)Math.Round(p.Z));
    }

    public Vector3D CellCenter(Vector3I position) => Origin + (Vector3D)position * CellSize;

    private bool TryIndex(Vector3I position, out int index)
    {
        var local = position - Min;
        if (local.X < 0 || local.Y < 0 || local.Z < 0 || local.X >= Size.X || local.Y >= Size.Y || local.Z >= Size.Z)
        {
            index = -1;
            return false;
        }

        index = Index(local.X, local.Y, local.Z);
        return true;
    }

    /// <summary>Whether smoke can reach the position from open space</summary>
    public bool IsOpen(Vector3D local)
    {
        return !TryIndex(CellAt(local), out var index) || depth[index] >= 0;
    }

    /// <summary>
    /// How much longer the smoke's way to this cell is than the straight way in from the padded boundary, in cells.
    /// 0 in open space, grows inside vented rooms reached through openings. -1 if smoke cannot reach the cell.
    /// </summary>
    public int Detour(Vector3D local)
    {
        var position = CellAt(local);
        if (!TryIndex(position, out var index))
            return 0;

        if (depth[index] < 0)
            return -1;

        var p = position - Min;
        var straight = Math.Min(Math.Min(Math.Min(p.X, p.Y), p.Z), Math.Min(Math.Min(Size.X - 1 - p.X, Size.Y - 1 - p.Y), Size.Z - 1 - p.Z));
        return Math.Max(0, depth[index] - straight);
    }

    /// <summary>Distance from the position to the unpadded box, a lower bound of the distance to any sealed air</summary>
    public double DistanceToInnerBox(Vector3D local)
    {
        var half = CellSize * 0.5;
        var min = CellCenter(InnerMin) - new Vector3D(half);
        var max = CellCenter(InnerMax) + new Vector3D(half);
        var clamped = Vector3D.Clamp(local, min, max);
        return Vector3D.Distance(local, clamped);
    }

    /// <summary>
    /// Largest radius of a sphere around the position that contains no sealed air, 0 if the position itself
    /// is not reachable by smoke. A camera facing billboard of at most this radius can never show inside a sealed room.
    /// </summary>
    public float AllowedRadius(Vector3D local)
    {
        var boxDistance = DistanceToInnerBox(local);
        var position = CellAt(local);
        if (!TryIndex(position, out var index))
            return (float)boxDistance;

        if (depth[index] < 0)
            return 0f;

        // Cells within Chebyshev distance k-1 contain no sealed air, together they form a cube of half size (k-0.5) cells
        var k = clearance[index];
        if (k == byte.MaxValue)
            k = MaxClearance + 1;

        var offset = Vector3D.Abs(local - CellCenter(position));
        var cubeBound = (k - 0.5) * CellSize - Math.Max(Math.Max(offset.X, offset.Y), offset.Z);
        return (float)Math.Max(Math.Max(cubeBound, boxDistance), 0.0);
    }
}
