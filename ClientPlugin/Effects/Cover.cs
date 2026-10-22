using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using VRageMath;

namespace ClientPlugin.Effects;

/// <summary>
/// The lights of a ship that no smoke hides, faded with the cloud instead. The renderer averages every transparent
/// billboard over a pixel (weighted blended order independent transparency), so a thruster flame behind the cloud is
/// mixed into the smoke's colour however dense the smoke in front of it, and the glares of lights and thrusters are drawn
/// on top of everything, tested only against the opaque scene. While a cloud hides its ship, the flames and glares of
/// the ship's thrusters and of its lights in open air (not those in sealed rooms) fade as the ship disappears; for a
/// camera inside the smoke every glare beyond the backdrop closing its view fades with the backdrop, and in the fog of a
/// third person camera (see CameraCloud) every flame and glare fades with its distance, its own ship's too. Built on the main
/// thread every frame, read by the render thread (see CoverPatches): the snapshot is replaced, never changed.
/// </summary>
public static class Cover
{
    private readonly struct Place
    {
        public readonly Vector3D Center;
        public readonly double RadiusSquared;
        public readonly float Keep;

        public Place(Vector3D center, double radius, float keep)
        {
            Center = center;
            RadiusSquared = radius * radius;
            Keep = keep;
        }
    }

    private sealed class Snapshot
    {
        public Place[] Places = Array.Empty<Place>();
        public Vector3D Camera;
        public double BeyondSquared = double.MaxValue;
        public float BeyondKeep = 1f;
        public float Fog;
    }

    private static volatile Snapshot current = new Snapshot();

    // A thruster's light and glare sit in its flame, a light's in front of the block: this far around the block
    private const double Reach = 2.5;

    private static readonly List<Place> Building = new List<Place>();

    /// <summary>Render thread: 0..1, how much of a glare or flame at this position is left</summary>
    public static float Keep(Vector3D position)
    {
        var snapshot = current;
        var keep = 1f;
        foreach (var place in snapshot.Places)
        {
            if (place.Keep < keep && Vector3D.DistanceSquared(position, place.Center) <= place.RadiusSquared)
                keep = place.Keep;
        }

        if (snapshot.BeyondKeep < 1f && Vector3D.DistanceSquared(position, snapshot.Camera) >= snapshot.BeyondSquared)
            keep *= snapshot.BeyondKeep;
        if (snapshot.Fog > 0f)
            keep *= CameraCloud.Transmission(Vector3D.Distance(position, snapshot.Camera), snapshot.Fog);
        return keep;
    }

    public static bool Active => current.Places.Length != 0 || current.BeyondKeep < 1f || current.Fog > 0f;

    /// <summary>Main thread, every frame the clouds are drawn</summary>
    /// <param name="inFog">The cloud whose ship is in the camera's fog with it, null for none: it does not cover its ship</param>
    /// <param name="fog">For a third person camera in its fog: 0..1, how far that has formed (see CameraCloud)</param>
    /// <param name="beyond">For a camera inside the smoke: how far its backdrop is</param>
    /// <param name="beyondKeep">0..1, how much is left beyond it of what the backdrop hides</param>
    public static void Update(List<Cloud> clouds, double now, Vector3D camera, Cloud inFog, float fog, double beyond, float beyondKeep)
    {
        Building.Clear();
        foreach (var cloud in clouds)
        {
            var cover = cloud == inFog ? 0f : cloud.ShipCover(now);
            if (cover <= 0.001f)
                continue;

            foreach (var id in cloud.GroupIds)
            {
                if (!MyEntities.TryGetEntityById(id, out MyCubeGrid grid) || grid.MarkedForClose)
                    continue;

                foreach (var block in grid.GetFatBlocks())
                {
                    var thrust = block is MyThrust;
                    if (!thrust && block is not MyLightingBlock)
                        continue;

                    var center = block.PositionComp.WorldAABB.Center;
                    if (!thrust && !InOpenAir(cloud, grid, center))
                        continue;

                    Building.Add(new Place(center, block.PositionComp.LocalVolume.Radius + Reach, 1f - cover));
                }
            }
        }

        current = new Snapshot
        {
            Places = Building.Count == 0 ? Array.Empty<Place>() : Building.ToArray(),
            Camera = camera,
            BeyondSquared = beyondKeep < 1f ? beyond * beyond : double.MaxValue,
            BeyondKeep = beyondKeep,
            Fog = fog,
        };
    }

    public static void Clear()
    {
        current = new Snapshot();
    }

    // A light shines into open air (the smoke reaches it) if any side of its block is open
    private static bool InOpenAir(Cloud cloud, MyCubeGrid grid, Vector3D center)
    {
        var m = grid.WorldMatrix;
        var step = grid.GridSize;
        return cloud.IsOpenAt(center + m.Up * step) || cloud.IsOpenAt(center - m.Up * step) || cloud.IsOpenAt(center + m.Right * step) ||
               cloud.IsOpenAt(center - m.Right * step) || cloud.IsOpenAt(center + m.Forward * step) || cloud.IsOpenAt(center - m.Forward * step);
    }
}
