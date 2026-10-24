using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Lights;
using VRageMath;

namespace ClientPlugin.Effects;

/// <summary>
/// The light of the discharges on the ship of a camera in the void, where no sun shines (see SunPatches): a few point
/// lights of the flash colour on the brightest discharges around the camera this frame, so the hull flickers with the
/// smoke around it
/// </summary>
public static class FlashLights
{
    // This many point lights at most, reaching this far beyond the region the discharge lights, at this intensity for
    // a discharge at its peak
    private const int Count = 6;
    private const float Reach = 8f;
    private const float Intensity = 6f;

    // A light whose discharge is gone (a bolt ends at once) fades out over about this long (s) instead
    private const double Afterglow = 0.1;

    private static readonly MyLight[] Lights = new MyLight[Count];

    // The lights of the last frame, and those fading out
    private static readonly List<(Vector3D Position, float Radius, float Brightness)> Shown = new List<(Vector3D, float, float)>();
    private static readonly List<(Vector3D Position, float Radius, float Brightness)> Fading = new List<(Vector3D, float, float)>();
    private static double lastUpdateAt = double.NaN;

    /// <summary>This frame's discharges: where, how far they light the smoke, how bright now</summary>
    public static readonly List<(Vector3D Position, float Radius, float Brightness)> Sources = new List<(Vector3D, float, float)>();

    /// <summary>Main thread, once a frame after <see cref="Sources"/> was filled</summary>
    public static void Update(double now)
    {
        var tint = Palette.Flash;
        var color = new Vector4(tint / Math.Max(1e-3f, Math.Max(tint.X, Math.Max(tint.Y, tint.Z))), 1f);

        // Last frame's lights whose discharge is gone this frame fade out, the fading ones further
        var decay = double.IsNaN(lastUpdateAt) ? 0f : (float)Math.Exp(-MathHelper.Clamp(now - lastUpdateAt, 0, 0.1) / Afterglow);
        lastUpdateAt = now;
        for (var i = Fading.Count - 1; i >= 0; i--)
        {
            var (position, radius, brightness) = Fading[i];
            Fading[i] = (position, radius, brightness * decay);
            if (brightness * decay <= 0.02f)
                Fading.RemoveAt(i);
        }

        foreach (var (position, radius, brightness) in Shown)
        {
            if (!Sources.Any(s => Vector3D.DistanceSquared(s.Position, position) < 0.25))
                Fading.Add((position, radius, brightness * decay));
        }

        var brightest = Sources.Concat(Fading).Where(f => f.Brightness > 0.02f).OrderByDescending(f => f.Brightness).Take(Count).ToList();
        Sources.Clear();
        Shown.Clear();
        Shown.AddRange(brightest.Where(f => !Fading.Contains(f)));
        for (var i = 0; i < Lights.Length; i++)
        {
            var light = Lights[i];
            if (i >= brightest.Count)
            {
                if (light != null && light.LightOn)
                {
                    light.LightOn = false;
                    light.UpdateLight();
                }

                continue;
            }

            if (light == null)
            {
                light = Lights[i] = MyLights.AddLight();
                if (light == null)
                    return;

                light.Start($"{Plugin.Name} flash");
            }

            var (position, radius, brightness) = brightest[i];
            light.Position = position;
            light.Color = color;
            light.Range = radius + Reach;
            light.Intensity = Intensity * brightness;
            light.LightOn = true;
            light.UpdateLight();
        }
    }

    public static void Clear()
    {
        Sources.Clear();
        Shown.Clear();
        Fading.Clear();
        lastUpdateAt = double.NaN;
        for (var i = 0; i < Lights.Length; i++)
        {
            if (Lights[i] == null)
                continue;

            MyLights.RemoveLight(Lights[i]);
            Lights[i] = null;
        }
    }
}
