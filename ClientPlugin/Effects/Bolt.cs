using System;
using System.Collections.Generic;
using VRage.Game;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Effects;

/// <summary>One lightning bolt: a jagged line in the lightning color with a faint halo, lighting the smoke around it</summary>
internal sealed class Bolt
{
    private static readonly MyStringId BoltMaterial = MyStringId.GetOrCompute("WeaponLaser");

    // HDR colors, unpremultiplied: the bolt covers little (low alpha) but is bright where it is
    private const float BoltBrightness = 30f;
    private const float BoltAlpha = 0.3f;
    private const float HaloBrightness = 5f;
    private const float HaloAlpha = 0.07f;

    private readonly Vector3D[] points;
    private readonly float thickness;
    public readonly float Brightness;
    public readonly double EndsAt;
    public readonly Vector3D Middle;
    public readonly double Reach;

    public Bolt(Vector3D[] points, float thickness, float brightness, double endsAt, double reach)
    {
        this.points = points;
        this.thickness = thickness;
        Brightness = brightness;
        EndsAt = endsAt;
        Middle = points[points.Length / 2];
        Reach = reach;
    }

    /// <summary>Midpoint displacement: a jagged line from a to b</summary>
    public static Vector3D[] Jagged(Vector3D a, Vector3D b, Random random)
    {
        var points = new List<Vector3D> { a, b };
        var offset = Vector3D.Distance(a, b) * 0.25;
        for (var level = 0; level < 3; level++)
        {
            var next = new List<Vector3D>(points.Count * 2);
            for (var i = 0; i < points.Count - 1; i++)
            {
                var middle = (points[i] + points[i + 1]) * 0.5;
                var jitter = new Vector3D(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5) * (2 * offset);
                next.Add(points[i]);
                next.Add(middle + jitter);
            }

            next.Add(points[points.Count - 1]);
            points = next;
            offset *= 0.5;
        }

        return points.ToArray();
    }

    /// <param name="fade">0..1, how much of the bolt shows</param>
    /// <param name="blend">Additive on top for a bolt that must show over the smoke in front of it</param>
    public void Draw(Random random, float fade = 1f, MyBillboard.BlendTypeEnum blend = MyBillboard.BlendTypeEnum.Standard)
    {
        var boltColor = Palette.Lightning * BoltBrightness;
        var haloColor = Palette.Lightning * HaloBrightness;
        // Flicker
        var flicker = (float)(0.4 + random.NextDouble() * 0.6) * Brightness * fade;
        var coreAlpha = BoltAlpha * flicker;
        var haloAlpha = HaloAlpha * flicker;
        var core = new Vector4(boltColor * coreAlpha, coreAlpha);
        var halo = new Vector4(haloColor * haloAlpha, haloAlpha);
        for (var i = 0; i < points.Length - 1; i++)
        {
            var delta = points[i + 1] - points[i];
            var length = (float)delta.Length();
            if (length < 1e-3f)
                continue;

            // Thin core and a faint halo around it
            var direction = (Vector3)(delta / length);
            MyTransparentGeometry.AddLineBillboard(BoltMaterial, core, points[i], direction, length, thickness, blend);
            MyTransparentGeometry.AddLineBillboard(BoltMaterial, halo, points[i], direction, length, thickness * 5f, blend);
        }
    }
}
