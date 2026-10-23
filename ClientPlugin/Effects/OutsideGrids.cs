using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace ClientPlugin.Effects;

/// <summary>
/// The grids that do not jump, seen from a camera that jumps with the ship. Once the smoke hides the ship from those
/// outside, those on board see nothing of the world outside either, other ships next to them included, which the
/// smoke's puffs or the camera's fog would still let through: as the view closes, a disc of smoke covers each of them
/// from the camera (its lights and flames fade with it, see <see cref="Cover"/>), then the grid is hidden, and as the
/// view opens again after the arrival the same backwards. Grids of the jumping group are never touched: they are in the
/// smoke with the camera.
/// </summary>
public static class OutsideGrids
{
    // Hidden from this much cover on; the disc covers it up to there
    private const float HideFrom = 0.97f;

    // The disc covers the grid's bounding sphere as seen from the camera, this much wider
    private const double Margin = 1.25;

    // How often the grids around are looked up again (s)
    private const double LookupInterval = 0.5;

    private static readonly List<MyCubeGrid> Near = new List<MyCubeGrid>();
    private static readonly HashSet<MyCubeGrid> HiddenByUs = new HashSet<MyCubeGrid>();
    private static readonly List<MyEntity> Found = new List<MyEntity>();
    private static double lookedUpAt = double.NegativeInfinity;

    /// <summary>0..1, how much the grids that do not jump are covered this frame</summary>
    public static float CoverNow { get; private set; }

    /// <summary>The grids that do not jump near the camera's cloud this frame (for their lights, see <see cref="Cover"/>)</summary>
    public static IReadOnlyList<MyCubeGrid> Grids => Near;

    /// <summary>Main thread, every frame the clouds are drawn</summary>
    /// <param name="cloud">The cloud the camera jumps with, null for none</param>
    /// <param name="cover">0..1, how much of the world outside the smoke the camera still sees: 1 for nothing</param>
    /// <param name="range">How far from the cloud's centre grids are covered</param>
    public static void Update(Cloud cloud, float cover, Vector3D camera, double range, double now)
    {
        if (cloud == null || cover <= 0.001f)
        {
            Clear();
            return;
        }

        CoverNow = cover;
        if (now - lookedUpAt >= LookupInterval || now < lookedUpAt)
        {
            lookedUpAt = now;
            LookUp(cloud, range);
        }

        foreach (var grid in Near)
        {
            if (grid.MarkedForClose)
                continue;

            if (cover >= HideFrom)
            {
                if (grid.Render.Visible)
                {
                    grid.Render.Visible = false;
                    HiddenByUs.Add(grid);
                    Log.Debug($"Outside grid hidden: {grid.DisplayName} ({grid.EntityId}), {Vector3D.Distance(camera, grid.PositionComp.WorldVolume.Center):0} m from the camera, cover {cover:0.00}");
                }

                continue;
            }

            Show(grid);
            DrawDisc(grid, camera, cover);
        }
    }

    /// <summary>Shows every grid hidden here again</summary>
    public static void Clear()
    {
        CoverNow = 0f;
        foreach (var grid in HiddenByUs)
        {
            if (!grid.MarkedForClose)
            {
                grid.Render.Visible = true;
                Log.Debug($"Outside grid shown: {grid.DisplayName} ({grid.EntityId}), no cover");
            }
        }

        HiddenByUs.Clear();
        Near.Clear();
        lookedUpAt = double.NegativeInfinity;
    }

    private static void Show(MyCubeGrid grid)
    {
        if (HiddenByUs.Remove(grid) && !grid.MarkedForClose)
        {
            grid.Render.Visible = true;
            Log.Debug($"Outside grid shown: {grid.DisplayName} ({grid.EntityId}), cover {CoverNow:0.00}");
        }
    }

    private static void LookUp(Cloud cloud, double range)
    {
        var sphere = new BoundingSphereD(cloud.Center, range);
        Found.Clear();
        MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, Found);

        // Those no longer near are shown again
        foreach (var grid in Near)
        {
            if (!Found.Contains(grid))
                Show(grid);
        }

        Near.Clear();
        foreach (var entity in Found)
        {
            // Grids hidden for another reason (an arrival not formed yet, a projection) stay as they are
            if (entity is MyCubeGrid grid && !grid.IsPreview && !cloud.GroupIds.Contains(grid.EntityId) && (grid.Render.Visible || HiddenByUs.Contains(grid)))
                Near.Add(grid);
        }
    }

    // A disc of the smoke's colour in front of the grid, as opaque as the cover, covering its bounding sphere
    private static void DrawDisc(MyCubeGrid grid, Vector3D camera, float cover)
    {
        var sphere = grid.PositionComp.WorldVolume;
        var toGrid = sphere.Center - camera;
        var distance = toGrid.Length();
        var depth = distance - sphere.Radius;
        if (depth < 0.5)
            return;

        var direction = toGrid / distance;
        var radius = (float)(depth * Math.Tan(Math.Asin(Math.Min(sphere.Radius / distance, 0.99))) * Margin / SoftDisc.OpaqueRadius);
        var up = Vector3D.CalculatePerpendicularVector(direction);
        var left = Vector3D.Cross(direction, up);
        MyTransparentGeometry.AddBillboardOriented(SoftDisc.Material, new Vector4(Cloud.SmokeColor * cover, cover), camera + direction * depth, (Vector3)left, (Vector3)up, radius,
            radius);
    }

    /// <summary>The lights and thrusters of the grids covered now, with how much of their glare is left</summary>
    public static void AddLights(List<(Vector3D Center, double Radius)> places)
    {
        if (CoverNow <= 0.001f)
            return;

        foreach (var grid in Near)
        {
            if (grid.MarkedForClose)
                continue;

            foreach (var block in grid.GetFatBlocks())
            {
                if (block is MyThrust || block is MyLightingBlock)
                    places.Add((block.PositionComp.WorldAABB.Center, block.PositionComp.LocalVolume.Radius));
            }
        }
    }
}
