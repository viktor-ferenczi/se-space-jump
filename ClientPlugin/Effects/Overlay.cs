using System;
using System.Collections.Generic;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Effects;

/// <summary>
/// Smoke right in front of a camera that jumps with the ship, independent of the cloud around the ship: a dark base,
/// slowly drifting lit and dark patches (flashing with the cloud's lightning) and sparks streaking past.
/// </summary>
public static class Overlay
{
    private static MyStringId? material;
    private static MyStringId Material => material ??= UnlitMaterials.Get("Smoke_Ignore_Depth");
    private static readonly MyStringId SparkMaterial = MyStringId.GetOrCompute("WhiteSpark");

    private static readonly Vector3 BaseColor = new Vector3(0.004f, 0.003f, 0.008f);

    // Brightness of the configured tints (see Palette) in the lit patches and the sparks
    private const float FlashBrightness = 0.66f;
    private const float LightningBrightness = 0.58f;
    private const float SparkBrightness = 9f;
    private const float PatchBrightness = 0.22f;

    private const int PatchCount = 14;
    private static readonly Patch[] Patches = new Patch[PatchCount];

    private static readonly List<Spark> Sparks = new List<Spark>();
    private static readonly Random Random = new Random();
    private static double nextSparkAt;
    private static double lastDrawAt = double.NaN;

    private struct Patch
    {
        public float U, V, Depth, Size, Hue, Phase, Speed, Orbit;
        public bool Dark;
    }

    private sealed class Spark
    {
        // Camera space: x right, y up, z forward
        public Vector3 Position;
        public Vector3 Velocity;
        public float Length;
        public Vector3 Color;
        public double StartsAt;
        public double EndsAt;
    }

    static Overlay()
    {
        var random = new Random(1097);
        for (var i = 0; i < PatchCount; i++)
        {
            Patches[i] = new Patch
            {
                U = (float)(random.NextDouble() * 2.4 - 1.2),
                V = (float)(random.NextDouble() * 2.0 - 1.0),
                Depth = (float)(0.7 + random.NextDouble() * 0.9),
                Size = (float)(0.45 + random.NextDouble() * 0.5),
                Hue = (float)random.NextDouble(),
                Phase = (float)(random.NextDouble() * Math.PI * 2),
                Speed = (float)((0.15 + random.NextDouble() * 0.35) * (random.Next(2) == 0 ? -1 : 1)),
                Orbit = (float)(0.15 + random.NextDouble() * 0.3),
                Dark = i % 5 == 1 || i % 5 == 3,
            };
        }
    }

    public static void Clear()
    {
        Sparks.Clear();
        lastDrawAt = double.NaN;
    }

    /// <param name="intensity">0..1, how deep in the smoke the camera is</param>
    /// <param name="flash">0..1, lightning in the cloud right now</param>
    public static void Draw(float intensity, float flash, double now)
    {
        var camera = MySector.MainCamera;
        var matrix = camera.WorldMatrix;
        var halfHeight = (float)Math.Tan(camera.FieldOfView * 0.5);
        var halfWidth = halfHeight * Math.Max(1f, camera.AspectRatio);
        var halfDiagonal = (float)Math.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight);
        var time = (float)now;
        var flashTint = Palette.Flash * FlashBrightness;
        var lightningTint = Palette.Lightning * LightningBrightness;

        // Base: wide layers, together hiding the outside completely at full intensity. The smoke texture is wispy, about
        // 0.6 opaque where it is dense, so each layer's alpha makes up for that. Coverage works on linear light, which the
        // screen's gamma and the eye adaptation brighten again: the outside should look dimmed by the intensity.
        const int baseLayers = 8;
        const float textureOpacity = 0.6f;
        var baseCoverage = Math.Min(1f - (float)Math.Pow(1f - intensity, 2.2), 0.99f);
        var baseAlpha = Math.Min(1f, (1f - (float)Math.Pow(1f - baseCoverage, 1.0 / baseLayers)) / textureOpacity);
        for (var i = 0; i < baseLayers; i++)
        {
            var distance = 0.5f + 0.15f * i;
            var angle = time * (i % 2 == 0 ? 0.05f : -0.04f) + i * 1.3f;
            AddCameraBillboard(ref matrix, 0, 0, distance, distance * halfDiagonal * 2.6f, angle, new Vector4(BaseColor * baseAlpha, baseAlpha));
        }

        // Patches: lit regions of the other side glowing through, darker knots drifting between them
        for (var i = 0; i < PatchCount; i++)
        {
            ref var patch = ref Patches[i];
            var t = patch.Phase + patch.Speed * time;
            var u = patch.U + patch.Orbit * (float)Math.Sin(t);
            var v = patch.V + patch.Orbit * (float)Math.Cos(t * 0.8f);
            var distance = patch.Depth;
            var radius = distance * halfHeight * 2f * patch.Size;
            var angle = patch.Phase + time * patch.Speed * 0.6f;

            Vector4 color;
            if (patch.Dark)
            {
                var alpha = 0.35f * intensity;
                color = new Vector4(BaseColor * alpha, alpha);
            }
            else
            {
                var pulse = 0.35f + 0.65f * Math.Max(0f, (float)Math.Sin(t * 1.7f + patch.Hue * 4f));
                var alpha = 0.2f * intensity;
                var light = PatchBrightness * pulse * (1f + 3f * flash);
                color = new Vector4(Vector3.Lerp(flashTint, lightningTint, patch.Hue) * (light * alpha), alpha);
            }

            AddCameraBillboard(ref matrix, u * halfWidth * distance, v * halfHeight * distance, distance, radius, angle, color);
        }

        DrawSparks(ref matrix, intensity, halfWidth, halfHeight, now);
    }

    private static void AddCameraBillboard(ref MatrixD camera, float x, float y, float z, float radius, float angle, Vector4 color)
    {
        var position = camera.Translation + camera.Right * x + camera.Up * y + camera.Forward * z;
        var rotation = Quaternion.CreateFromAxisAngle(camera.Forward, angle);
        MyTransparentGeometry.AddBillboardOriented(Material, color, position,
            Vector3.Transform(camera.Left, rotation),
            Vector3.Transform(camera.Up, rotation),
            radius, MyBillboard.BlendTypeEnum.Standard);
    }

    private static void DrawSparks(ref MatrixD camera, float intensity, float halfWidth, float halfHeight, double now)
    {
        var dt = double.IsNaN(lastDrawAt) ? 0f : (float)MathHelper.Clamp(now - lastDrawAt, 0, 0.1);
        lastDrawAt = now;

        Sparks.RemoveAll(s => now > s.EndsAt);
        foreach (var spark in Sparks)
            spark.Position += spark.Velocity * dt;

        var rate = 5.0 * intensity;
        if (rate > 0.01 && nextSparkAt < now - 1)
            nextSparkAt = now;
        while (rate > 0.01 && now >= nextSparkAt && Sparks.Count < 10)
        {
            nextSparkAt += -Math.Log(1.0 - Random.NextDouble()) / rate;
            var depth = (float)(1.5 + Random.NextDouble() * 2.5);
            var angle = Random.NextDouble() * Math.PI * 2;
            Sparks.Add(new Spark
            {
                Position = new Vector3((float)(Random.NextDouble() * 1.6 - 0.8) * halfWidth * depth, (float)(Random.NextDouble() * 1.6 - 0.8) * halfHeight * depth, depth),
                Velocity = new Vector3((float)Math.Cos(angle), (float)Math.Sin(angle), (float)(Random.NextDouble() - 0.5)) * (float)(3 + Random.NextDouble() * 6),
                Length = (float)(0.15 + Random.NextDouble() * 0.35),
                Color = (Random.Next(2) == 0 ? Palette.Lightning : Palette.Flash) * SparkBrightness,
                StartsAt = now,
                EndsAt = now + 0.2 + Random.NextDouble() * 0.25,
            });
        }

        foreach (var spark in Sparks)
        {
            var speed = spark.Velocity.Length();
            if (speed < 1e-3f || spark.Position.Z < 0.3f)
                continue;

            var fade = 1f - (float)((now - spark.StartsAt) / (spark.EndsAt - spark.StartsAt));
            var alpha = 0.6f * MathHelper.Clamp(fade, 0f, 1f) * intensity;
            var head = camera.Translation + camera.Right * spark.Position.X + camera.Up * spark.Position.Y + camera.Forward * spark.Position.Z;
            var velocity = camera.Right * spark.Velocity.X + camera.Up * spark.Velocity.Y + camera.Forward * spark.Velocity.Z;
            var direction = (Vector3)(-velocity / speed);
            MyTransparentGeometry.AddLineBillboard(SparkMaterial, new Vector4(spark.Color * alpha, alpha), head, direction, spark.Length, 0.012f * spark.Position.Z, MyBillboard.BlendTypeEnum.Standard);
        }
    }
}
