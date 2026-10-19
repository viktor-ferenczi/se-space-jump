using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClientPlugin.Core;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace ClientPlugin.Effects;

/// <summary>
/// Snapshots the blocks of a grid group on the main thread, then builds the air maps and samples the puffs on a worker.
/// </summary>
public static class ShapeBuilder
{
    // Above this many cells (padding included) a grid is mapped at reduced resolution, see CoarseAirCells
    private const long MaxFineCells = 2_000_000;

    private const int MaxGroupGrids = 32;

    private const int MaxObstacleGrids = 16;

    // Beyond the portal: the largest puff, its spreading and drifting while the cloud lingers, the leaking fog
    private const double ObstacleMargin = 14 + 40;

    public static Task<CloudShape> Start(MyCubeGrid main, int seed)
    {
        var grids = new List<MyCubeGrid> { main };
        var group = MyCubeGridGroups.Static.Physical.GetGroup(main);
        if (group != null)
        {
            grids.AddRange(group.Nodes
                .Select(node => node.NodeData)
                .Where(grid => grid != null && grid != main && !grid.MarkedForClose)
                .OrderByDescending(grid => grid.BlocksCount)
                .Take(MaxGroupGrids - 1));
        }

        var mainWorld = main.WorldMatrix;
        var jobs = new List<(IAirCells Cells, int Pad, MatrixD MainToMap)>();
        foreach (var grid in grids)
        {
            var mainToMap = mainWorld * MatrixD.Invert(grid.WorldMatrix);
            var (cells, pad) = Snapshot(grid);
            jobs.Add((cells, pad, mainToMap));
        }

        var settings = new CloudSettings
        {
            PuffBudget = Math.Max(50, Config.Current.PuffBudget),
            MinRadius = Math.Max(0.35f, main.GridSize * 0.3f),
            Scale = MathHelper.Clamp(Config.Current.CloudScale, 0.25f, 8f),
        };

        return Task.Run(() =>
        {
            var shape = new CloudShape();
            foreach (var (cells, pad, mainToMap) in jobs)
                shape.AddMap(AirMap.Build(cells, pad), mainToMap);

            shape.SamplePuffs(settings, new Random(seed));
            return shape;
        });
    }

    /// <summary>
    /// Air maps of the grids around a cloud that are not part of the jumping group, so the cloud can keep its smoke
    /// out of their sealed rooms too: a station or carrier the ship jumps out of or next to.
    /// </summary>
    public static Task<List<Obstacle>> StartObstacles(MyCubeGrid main, ICollection<long> groupIds)
    {
        var box = BoundingBoxD.CreateInvalid();
        var group = MyCubeGridGroups.Static.Physical.GetGroup(main);
        if (group != null)
        {
            foreach (var node in group.Nodes)
            {
                if (node.NodeData != null)
                    box.Include(node.NodeData.PositionComp.WorldAABB);
            }
        }
        else
        {
            box.Include(main.PositionComp.WorldAABB);
        }

        // The portal's radius (see CloudSettings), scaled, its exit 35 % larger, and the margin
        var center = box.Center;
        var scale = MathHelper.Clamp(Config.Current.CloudScale, 0.25f, 8f);
        var settings = new CloudSettings();
        box = new BoundingBoxD(center, center).GetInflated((box.HalfExtents.Length() * settings.BubbleScale + settings.BubbleMargin) * scale * 1.35 + ObstacleMargin * scale);

        var entities = new List<MyEntity>();
        MyGamePruningStructure.GetTopMostEntitiesInBox(ref box, entities);
        var grids = entities
            .OfType<MyCubeGrid>()
            .Where(grid => !groupIds.Contains(grid.EntityId) && !grid.MarkedForClose && !grid.IsPreview && grid.Physics != null)
            .OrderBy(grid => Vector3D.DistanceSquared(grid.PositionComp.WorldAABB.Center, center))
            .Take(MaxObstacleGrids)
            .ToList();

        var jobs = grids.Select(grid => (Grid: grid, Snapshot: Snapshot(grid))).ToList();
        if (jobs.Count != 0)
            Log.Debug($"Obstacles around {main.DisplayName}: {string.Join(", ", grids.Select(g => g.DisplayName))}");

        return Task.Run(() => jobs.Select(job => new Obstacle(job.Grid, AirMap.Build(job.Snapshot.Cells, job.Snapshot.Pad))).ToList());
    }

    private static (IAirCells Cells, int Pad) Snapshot(MyCubeGrid grid)
    {
        var gridSize = grid.GridSize;
        var extent = (Vector3)(grid.Max - grid.Min + Vector3I.One) * gridSize;
        var padMeters = MathHelper.Clamp(extent.Max() * 0.12f, 6f, 30f);
        var pad = PadCells(padMeters, gridSize);

        if (AirMap.CountCells(grid.Min, grid.Max, pad) <= MaxFineCells)
        {
            var cells = new SnapshotAirCells(grid.Min, grid.Max, gridSize, IsWholeBlockAirtight, IsFaceAirtight);
            foreach (var block in grid.CubeBlocks)
            {
                for (var z = block.Min.Z; z <= block.Max.Z; z++)
                for (var y = block.Min.Y; y <= block.Max.Y; y++)
                for (var x = block.Min.X; x <= block.Max.X; x++)
                    cells.Set(new Vector3I(x, y, z), block);
            }

            return (cells, pad);
        }

        var step = 2;
        while (AirMap.CountCells(grid.Min / step, grid.Max / step, PadCells(padMeters, gridSize * step)) > MaxFineCells)
            step++;

        var coarse = new CoarseAirCells(grid.Min, grid.Max, gridSize, step);
        foreach (var block in grid.CubeBlocks)
            coarse.MarkGridCells(block.Min, block.Max);

        Log.Debug($"Grid {grid.DisplayName} mapped at 1/{step} resolution");
        return (coarse, PadCells(padMeters, gridSize * step));
    }

    private static int PadCells(float padMeters, float cellSize) => MathHelper.Clamp((int)Math.Ceiling(padMeters / cellSize) + 1, 2, 24);

    // MyCubeGridGasContext.IsAirtightBetweenPositions, same block case
    private static bool? IsWholeBlockAirtight(object block)
    {
        try
        {
            var slim = (MySlimBlock)block;
            if (slim.BlockDefinition is not MyCubeBlockDefinition definition)
                return false;

            return MyGridGasSystem.IsAirtightFromDefinition(definition, slim.BuildLevelRatio);
        }
        catch (Exception)
        {
            // The block changed under the worker, keep the smoke out
            return true;
        }
    }

    private static bool IsFaceAirtight(object block, Vector3I position, Vector3I normal)
    {
        try
        {
            return MyGridGasSystem.IsAirtightBlock((IMySlimBlock)block, position, normal);
        }
        catch (Exception)
        {
            return true;
        }
    }
}

/// <summary>A grid near a cloud that does not jump, with the air map of its own frame</summary>
public sealed class Obstacle
{
    public readonly MyCubeGrid Grid;
    public readonly AirMap Map;

    public Obstacle(MyCubeGrid grid, AirMap map)
    {
        Grid = grid;
        Map = map;
    }
}
