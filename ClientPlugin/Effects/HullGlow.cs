using System;
using Sandbox.Game.Entities;
using Sandbox.Game.Lights;
using Sandbox.Game.World;
using VRage.Game.Utils;
using VRageMath;

namespace ClientPlugin.Effects;

/// <summary>
/// The discharges' light on the hull of a ship seen from its third person camera in the void, where no sun shines: a
/// few wide point lights in the smoke just off the hull, each coming up and going out again smoothly over about a
/// second, at varied strength, on the part of the hull the camera looks at. The discharges themselves flash too fast
/// for that: as point lights they lit the hull's faces brighter than the smoke in front of them, with hard edges
/// (#1435). These light broad, soft patches, with the smoke's glow drawn over them. A point light's own light falls off
/// fast with distance: lights out at the ship's bounding sphere, or on its bounding box, did not reach the hull
/// </summary>
public static class HullGlow
{
    private const int Count = 8;

    // Intensity at the peak of the brightest glow at the least height, with the discharges at full strength and the void
    // all dark: about what lights a hull face in view (8 and 30 did not show on the hull, 80 lit a face, measured)
    private const float Intensity = 70f;

    // A light's height over the hull, towards the camera: times the ship's radius, at least this many metres. A point
    // light lights a patch about as wide as its height, so a large ship's lights stand higher, and as bright on the hull,
    // their light falling off with the square of the distance. Their reach: times the ship's radius, at least this many
    // metres
    private const double Height = 0.06;
    private const double MinHeight = 3;
    private const float Reach = 0.25f;
    private const float MinReach = 12f;

    // The glows land where rays from the camera meet the hull, within this part of the view's half width and height
    private const double ViewSpread = 0.6;
    private const int Tries = 4;

    // A glow takes this long to come up and go out again (s), with a pause before the next one in its place
    private const double MinDuration = 0.8;
    private const double MaxDuration = 1.6;
    private const double MaxPause = 0.5;

    private struct Glow
    {
        public double StartsAt;
        public double Duration;
        public float Peak;
        // The light's place, in the grid's own coordinates (m): a point of the hull, and from there towards the camera
        public Vector3D Local;
        public Vector3D Normal;
    }

    private static readonly MyLight[] Lights = new MyLight[Count];
    private static readonly Glow[] Glows = new Glow[Count];
    private static readonly Random Random = new Random();
    private static bool started;

    /// <summary>Main thread, once a frame: the ship's cloud of a third person camera in the void, else null</summary>
    public static void Update(double now, Cloud cloud, float darkness)
    {
        var grid = cloud?.Grid;
        var view = MySector.MainCamera;
        var strength = cloud != null && grid != null && !grid.MarkedForClose && view != null ? cloud.LightningIntensity(now) * darkness : 0f;
        if (strength <= 0.01f)
        {
            Off();
            return;
        }

        var matrix = grid.WorldMatrix;
        var radius = grid.PositionComp.WorldVolume.Radius;
        var reach = Math.Max(MinReach, (float)radius * Reach);
        var height = Math.Max(MinHeight, radius * Height);
        var intensity = Intensity * (float)(height * height / (MinHeight * MinHeight));
        if (!started)
        {
            // The first glows come up at different moments
            for (var i = 0; i < Count; i++)
                Next(ref Glows[i], now - Random.NextDouble() * MaxDuration, grid, view);
            started = true;
        }

        var tint = Palette.Flash;
        var color = new Vector4(tint / Math.Max(1e-3f, Math.Max(tint.X, Math.Max(tint.Y, tint.Z))), 1f);
        for (var i = 0; i < Count; i++)
        {
            ref var glow = ref Glows[i];
            while (now >= glow.StartsAt + glow.Duration)
                Next(ref glow, glow.StartsAt + glow.Duration + Random.NextDouble() * MaxPause, grid, view);

            // Up and down again on a smooth curve, no step anywhere
            var x = MathHelper.Clamp((float)((now - glow.StartsAt) / glow.Duration), 0f, 1f);
            var envelope = (float)Math.Pow(Math.Sin(Math.PI * x), 2);
            var light = Lights[i];
            if (light == null)
            {
                light = Lights[i] = MyLights.AddLight();
                if (light == null)
                    return;

                light.Start($"{Plugin.Name} hull glow");
            }

            light.Position = Vector3D.Transform(glow.Local + glow.Normal * (grid.GridSizeHalf + height), matrix);
            light.Color = color;
            light.Range = reach;
            light.Intensity = intensity * glow.Peak * envelope * strength;
            light.LightOn = light.Intensity > 0.01f;
            light.UpdateLight();
        }
    }

    // A new glow where a ray from the camera, somewhere in the middle of its view, meets the hull; none (no light) when
    // a few rays all miss it
    private static void Next(ref Glow glow, double startsAt, MyCubeGrid grid, MyCamera view)
    {
        glow.StartsAt = startsAt;
        glow.Duration = MinDuration + Random.NextDouble() * (MaxDuration - MinDuration);
        glow.Peak = 0f;

        var camera = view.WorldMatrix;
        var halfHeight = Math.Tan(view.FieldOfView * 0.5);
        var far = Vector3D.Distance(camera.Translation, grid.PositionComp.WorldVolume.Center) + 2 * grid.PositionComp.WorldVolume.Radius;
        var toLocal = MatrixD.Invert(grid.WorldMatrix);
        for (var i = 0; i < Tries; i++)
        {
            var direction = Vector3D.Normalize(camera.Forward
                + camera.Right * ((Random.NextDouble() * 2 - 1) * ViewSpread * halfHeight * view.AspectRatio)
                + camera.Up * ((Random.NextDouble() * 2 - 1) * ViewSpread * halfHeight));
            if (grid.RayCastBlocks(camera.Translation, camera.Translation + direction * far) is not Vector3I cell)
                continue;

            glow.Peak = 0.4f + 0.6f * (float)Random.NextDouble();
            glow.Local = (Vector3D)cell * grid.GridSize;
            glow.Normal = Vector3D.TransformNormal(-direction, toLocal);
            return;
        }
    }

    private static void Off()
    {
        started = false;
        foreach (var light in Lights)
        {
            if (light == null || !light.LightOn)
                continue;

            light.LightOn = false;
            light.UpdateLight();
        }
    }

    public static void Clear()
    {
        started = false;
        for (var i = 0; i < Lights.Length; i++)
        {
            if (Lights[i] == null)
                continue;

            MyLights.RemoveLight(Lights[i]);
            Lights[i] = null;
        }
    }
}
