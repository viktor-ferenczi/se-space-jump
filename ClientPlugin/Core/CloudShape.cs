using System;
using System.Collections.Generic;
using VRageMath;

namespace ClientPlugin.Core;

/// <summary>One smoke billboard of a cloud, in the local frame of the cloud (the main grid's local meters)</summary>
public struct Puff
{
    public Vector3 Position;
    public float Radius;

    /// <summary>0..1, when the puff appears while the cloud forms: the bubble's rim first, then inwards to the hull</summary>
    public float Order;

    /// <summary>Extra delay in seconds while the smoke flows into vented rooms through their openings</summary>
    public float FlowDelay;

    public float Angle;
    public float Spin;
    public float Alpha;
    public float Seed;
    public byte Material;

    /// <summary>Unit direction the puff drifts in when the cloud spreads</summary>
    public Vector3 Drift;

    /// <summary>Largest radius the puff may grow to while spreading without reaching into sealed air</summary>
    public float MaxRadius;

    /// <summary>Ellipsoidal radius in the bubble when sampled: 0 at the center, 1 on the surface; feathers the rim</summary>
    public float Rim;

    /// <summary>Fog leaking out of the portal: starts at the rim and flows outwards along <see cref="Drift"/>, over and over</summary>
    public bool Leak;

    /// <summary>Sampled on the air right at the hull: the layer that hides the ship</summary>
    public bool Hull;
}

public sealed class CloudSettings
{
    public int PuffBudget = 2200;

    // The portal: a sphere around the grids' box, its radius the box's half diagonal times this plus the margin
    public float BubbleScale = 1.3f;
    public float BubbleMargin = 8f;

    /// <summary>Size multiplier of the whole cloud (the CloudScale setting)</summary>
    public float Scale = 1f;

    /// <summary>Share of the puffs leaking out of the portal at its rim</summary>
    public float LeakShare = 0.12f;

    public float MinRadius = 0.6f;
    public float MaxRadius = 14f;
    public float FlowSpeed = 6f;
}

/// <summary>
/// Air maps of the grids of a jumping grid group, in the local frame of the main grid,
/// and the puffs sampled from them.
/// </summary>
public sealed class CloudShape
{
    private readonly List<(AirMap Map, MatrixD MainToMap)> maps = new List<(AirMap, MatrixD)>();

    /// <summary>Union of the grid boxes in the main grid's local frame</summary>
    public BoundingBoxD LocalBox = BoundingBoxD.CreateInvalid();

    public readonly List<Puff> Puffs = new List<Puff>();

    public Vector3D BubbleCenter;
    public Vector3D BubbleSemiAxes;

    public int MapCount => maps.Count;

    public void AddMap(AirMap map, MatrixD mainToMap)
    {
        maps.Add((map, mainToMap));

        // Corners of the map's inner box, moved into the main grid's frame
        var half = map.CellSize * 0.5;
        var min = map.CellCenter(map.InnerMin) - new Vector3D(half);
        var max = map.CellCenter(map.InnerMax) + new Vector3D(half);
        var mapToMain = MatrixD.Invert(mainToMap);
        LocalBox.Include(new BoundingBoxD(min, max).TransformFast(ref mapToMain));
    }

    public float AllowedRadius(Vector3D local)
    {
        var allowed = float.MaxValue;
        foreach (var (map, mainToMap) in maps)
        {
            allowed = Math.Min(allowed, map.AllowedRadius(Vector3D.Transform(local, mainToMap)));
            if (allowed <= 0f)
                return 0f;
        }

        return allowed;
    }

    public bool IsOpen(Vector3D local)
    {
        foreach (var (map, mainToMap) in maps)
        {
            if (!map.IsOpen(Vector3D.Transform(local, mainToMap)))
                return false;
        }

        return true;
    }

    /// <summary>Detour in meters the smoke takes to get to the position, see <see cref="AirMap.Detour"/></summary>
    public float DetourMeters(Vector3D local)
    {
        var detour = 0f;
        foreach (var (map, mainToMap) in maps)
            detour = Math.Max(detour, map.Detour(Vector3D.Transform(local, mainToMap)) * map.CellSize);
        return detour;
    }

    /// <summary>Ellipsoidal radius of a point: 0 at the bubble's center, 1 on its surface</summary>
    public double BubbleRadius(Vector3D local)
    {
        var d = (local - BubbleCenter) / BubbleSemiAxes;
        return d.Length();
    }

    /// <summary>
    /// Samples the puffs of the cloud: uniformly in the portal, a sphere around the grids, and on the reachable cells
    /// near the hull, plus the fog leaking out at the portal's rim. Every puff's radius is capped by the distance to the
    /// nearest sealed air, so no puff can be seen inside a pressurized room; small puffs get a higher acceptance to fill
    /// the narrow places near the hull.
    /// </summary>
    public void SamplePuffs(CloudSettings settings, Random random)
    {
        Puffs.Clear();
        if (!LocalBox.Valid || maps.Count == 0)
            return;

        // A sphere looks the same size from every side: the portal has a fixed width on any camera
        BubbleCenter = LocalBox.Center;
        var portalRadius = (LocalBox.HalfExtents.Length() * settings.BubbleScale + settings.BubbleMargin) * settings.Scale;
        BubbleSemiAxes = new Vector3D(portalRadius);

        var largest = portalRadius;
        var maxRadius = (float)Math.Max(settings.MinRadius * 2, Math.Min(settings.MaxRadius * settings.Scale, largest * 0.22));
        var leaks = (int)(settings.PuffBudget * settings.LeakShare);
        var budget = settings.PuffBudget - leaks;
        var mainMap = maps[0].Map;
        var mapToMain = MatrixD.Invert(maps[0].MainToMap);
        var nearCells = mainMap.NearCells;

        var attempts = budget * 40;
        for (var attempt = 0; attempt < attempts && Puffs.Count < budget; attempt++)
        {
            Vector3D local;
            var near = nearCells.Count != 0 && random.NextDouble() < 0.4;
            if (near)
            {
                var cell = nearCells[random.Next(nearCells.Count)];
                var jitter = new Vector3D(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5) * (mainMap.CellSize * 0.5);
                local = Vector3D.Transform(mainMap.CellCenter(cell) + jitter, mapToMain);
            }
            else
            {
                local = BubbleCenter + RandomInUnitSphere(random) * BubbleSemiAxes;
            }

            var bubbleRadius = BubbleRadius(local);
            if (bubbleRadius > 1.0)
                continue;

            var allowed = AllowedRadius(local);
            if (allowed < settings.MinRadius)
                continue;

            var radius = Math.Min(allowed, maxRadius * (0.55f + 0.45f * (float)random.NextDouble()));
            var acceptance = Math.Min(1.0, Math.Max(0.06, Math.Pow(settings.MinRadius * 3.0 / radius, 1.3)));
            if (!near && random.NextDouble() > acceptance)
                continue;

            var detour = DetourMeters(local);
            var offset = local - BubbleCenter;
            var drift = offset.LengthSquared() > 1e-6 ? Vector3D.Normalize(offset) : Vector3D.Up;

            Puffs.Add(new Puff
            {
                Position = local,
                Radius = radius,
                MaxRadius = Math.Min(allowed, radius * 1.6f),
                Order = (float)MathHelper.Clamp((1.0 - bubbleRadius) * 0.75 + random.NextDouble() * 0.25, 0.0, 1.0),
                FlowDelay = detour / settings.FlowSpeed,
                Angle = (float)(random.NextDouble() * Math.PI * 2),
                Spin = (float)((random.NextDouble() - 0.5) * 0.4),
                Alpha = 0.55f + 0.45f * (float)random.NextDouble(),
                Seed = (float)random.NextDouble(),
                Material = (byte)random.Next(3),
                Drift = drift,
                Rim = (float)bubbleRadius,
                Hull = near,
            });
        }

        // The fog leaking out: at the rim, flowing outwards
        for (var attempt = 0; attempt < leaks * 20 && Puffs.Count < settings.PuffBudget; attempt++)
        {
            var direction = RandomInUnitSphere(random);
            if (direction.LengthSquared() < 1e-4)
                continue;
            direction.Normalize();

            var rim = 0.82 + 0.16 * random.NextDouble();
            var local = BubbleCenter + direction * (portalRadius * rim);
            var allowed = AllowedRadius(local);
            if (allowed < settings.MinRadius)
                continue;

            // Outwards, a little off the normal so the wisps do not look combed
            var drift = direction + RandomInUnitSphere(random) * 0.35;
            drift.Normalize();
            Puffs.Add(new Puff
            {
                Position = local,
                Radius = Math.Min(allowed, maxRadius * (0.5f + 0.4f * (float)random.NextDouble())),
                MaxRadius = Math.Min(allowed, maxRadius * 1.6f),
                Order = (float)(random.NextDouble() * 0.25),
                Angle = (float)(random.NextDouble() * Math.PI * 2),
                Spin = (float)((random.NextDouble() - 0.5) * 0.4),
                Alpha = 0.55f + 0.45f * (float)random.NextDouble(),
                Seed = (float)random.NextDouble(),
                Material = (byte)random.Next(3),
                Drift = drift,
                Rim = (float)rim,
                Leak = true,
            });
        }
    }

    private static Vector3D RandomInUnitSphere(Random random)
    {
        while (true)
        {
            var v = new Vector3D(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);
            if (v.LengthSquared() <= 1.0)
                return v;
        }
    }
}
