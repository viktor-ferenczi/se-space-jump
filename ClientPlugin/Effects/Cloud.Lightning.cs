using System;
using System.Collections.Generic;
using VRage.Game;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Effects;

// The discharges in the fog: lightning, flashes and sparks
public sealed partial class Cloud
{
    private float LightningIntensity(double now)
    {
        switch (Kind)
        {
            case CloudKind.Origin:
                if (Detached)
                    return 1f - SmoothStep((float)(now - detachedAt) / 4f);
                if (!double.IsNaN(abortedAt))
                    return AbortBurst * AbortGlow((float)(now - abortedAt));
                return SmoothStep((float)((now - (JumpAt - 7.0)) / 6.0));
            case CloudKind.Destination:
                return 1f - SmoothStep((float)(now - StartedAt - 0.5) / 4.5f);
            default:
                return 0.6f * (1f - SmoothStep((float)(now - StartedAt) / 3f));
        }
    }

    /// <summary>0..1, lightning in the cloud right now, flashes the overlay of a camera inside it</summary>
    public float Flash
    {
        get
        {
            var flash = 0f;
            foreach (var bolt in bolts)
                flash += bolt.Brightness;
            var now = lastDrawAt;
            foreach (var patch in flashes)
                flash += 0.5f * patch.Brightness(now);
            return Math.Min(1f, flash * 0.35f);
        }
    }

    private float Glow(Vector3D world, double now)
    {
        var glow = 0f;
        foreach (var patch in flashes)
        {
            var d = Vector3D.Distance(world, Vector3D.Transform(patch.Local, frame));
            if (d < patch.Radius)
            {
                var x = (float)(1.0 - d / patch.Radius);
                glow += patch.Brightness(now) * x * x;
            }
        }

        foreach (var bolt in bolts)
        {
            var d = Vector3D.Distance(world, bolt.Middle);
            if (d < bolt.Reach)
            {
                // Soft wide falloff, so a flash lights a region of the cloud rather than single puffs
                var x = (float)(1.0 - d / bolt.Reach);
                glow += bolt.Brightness * x * x;
            }
        }

        return Math.Min(glow, 1f);
    }

    /// <summary>A world position inside the visible part of the cloud now, near a random puff</summary>
    private bool RandomPointInCloud(double now, float minAlpha, out Vector3D world)
    {
        world = Vector3D.Zero;
        var puffs = Shape.Puffs;
        if (puffs.Count == 0)
            return false;

        var puff = puffs[random.Next(puffs.Count)];
        Evaluate(ref puff, now, out var alpha, out var radius, out var local);
        if (alpha < minAlpha)
            return false;

        var jitter = new Vector3D(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5) * radius;
        world = Vector3D.Transform(local + jitter, frame);
        return true;
    }

    private Vector3D RandomDirection()
    {
        while (true)
        {
            var v = new Vector3D(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);
            var lengthSquared = v.LengthSquared();
            if (lengthSquared > 1e-4 && lengthSquared <= 1.0)
                return v / Math.Sqrt(lengthSquared);
        }
    }

    /// <summary>Short, frequent discharges all through the cloud, sized to it: deep in the fog they are seen as the glow they light</summary>
    private void UpdateBolts(double now, Vector3D cameraPosition)
    {
        bolts.RemoveAll(b => now > b.EndsAt);

        var intensity = LightningIntensity(now);
        if (intensity <= 0.01f || Shape.Puffs.Count < 2)
            return;

        var size = Shape.BubbleSemiAxes.AbsMax();
        var rate = (6.0 + Shape.Puffs.Count / 300.0) * Math.Max(1f, intensity);
        var spawned = 0;
        while (now >= nextBoltAt && spawned < 4)
        {
            nextBoltAt = Math.Max(nextBoltAt, now - 0.2) + -Math.Log(1.0 - random.NextDouble()) / rate;
            spawned++;
            // A weak intensity thins the events out rather than scheduling the next one seconds ahead
            if (random.NextDouble() > intensity)
                continue;

            if (!RandomPointInCloud(now, 0.08f, out var start))
                continue;

            // Right in front of the camera even a small discharge fills the view
            if (Vector3D.DistanceSquared(start, cameraPosition) < 8 * 8)
                continue;

            var length = (0.06 + 0.2 * random.NextDouble()) * size;
            var end = start + RandomDirection() * length;
            var points = Bolt.Jagged(start, end, random);
            var thickness = (float)MathHelper.Clamp(length * 0.03, 0.08, 0.5);

            var clear = true;
            foreach (var p in points)
            {
                if (WorldAllowed(p) < thickness * 3f)
                {
                    clear = false;
                    break;
                }
            }

            if (!clear)
                continue;

            var bolt = new Bolt(points, thickness, intensity * (0.5f + 0.5f * (float)random.NextDouble()), now + 0.05 + random.NextDouble() * 0.12, length * 2.5 + 10);
            bolts.Add(bolt);

            // Every bolt is heard where it strikes: a bigger and closer one louder, one deep in the fog (seen only as
            // the glow it lights) as a dull flash
            var depth = FogDepth(bolt.Middle, cameraPosition);
            var reach = (float)((length / size - 0.06) / 0.2);
            var distance = Vector3D.Distance(bolt.Middle, cameraPosition);
            var clarity = bolt.Brightness * (0.4f + 0.6f * reach) * (float)Math.Exp(-FogAttenuation * depth);
            var loudness = clarity * Near(distance);
            JumpSound slot;
            if (depth > DeepDischarge)
                slot = JumpSound.Flash;
            else if (loudness >= StrikeLoudness && now - lastStrikeAt >= StrikeSpacing)
            {
                slot = JumpSound.Strike;
                lastStrikeAt = now;
            }
            else
                slot = JumpSound.Snap;

            if (AbortSound(now, ref loudness))
                JumpSounds.PlayDischarge(bolt.Middle, slot, loudness * medium, clarity);
        }
    }

    /// <summary>
    /// Patches of the smoke lit from inside for a moment, flickering, mostly without a visible bolt: the discharges
    /// deep in the cloud, constantly while it is dense. Some of them are heard as lightning strikes.
    /// </summary>
    private void UpdateFlashes(double now, Vector3D cameraPosition)
    {
        flashes.RemoveAll(f => now > f.EndsAt);

        var intensity = LightningIntensity(now);
        if (intensity <= 0.01f || Shape.Puffs.Count < 2)
            return;

        var size = (float)Shape.BubbleSemiAxes.AbsMax();
        var rate = (3.0 + Shape.Puffs.Count / 1000.0) * Math.Max(1f, intensity);
        var spawned = 0;
        while (now >= nextFlashAt && spawned < 2)
        {
            nextFlashAt = Math.Max(nextFlashAt, now - 0.5) + -Math.Log(1.0 - random.NextDouble()) / rate;
            spawned++;
            // A weak intensity thins the events out rather than scheduling the next one seconds ahead
            if (random.NextDouble() > intensity)
                continue;

            var puff = Shape.Puffs[random.Next(Shape.Puffs.Count)];
            Evaluate(ref puff, now, out var alpha, out _, out var local);
            if (alpha < 0.05f)
                continue;

            var patch = new FlashPatch
            {
                Local = local,
                Radius = size * (0.2f + 0.2f * (float)random.NextDouble()),
                Peak = intensity * (0.5f + 0.5f * (float)random.NextDouble()),
                Phase = (float)(random.NextDouble() * Math.PI * 2),
                StartsAt = now,
                EndsAt = now + 0.4 + 0.8 * random.NextDouble(),
            };
            flashes.Add(patch);

            // A discharge deep in the fog: heard as a dull flash, the deeper and farther the quieter
            var world = Vector3D.Transform(local, frame);
            var distance = Vector3D.Distance(world, cameraPosition);
            var clarity = patch.Peak * (float)Math.Exp(-FogAttenuation * FogDepth(world, cameraPosition));
            var flashLoudness = clarity * Near(distance);
            if (AbortSound(now, ref flashLoudness))
                JumpSounds.PlayDischarge(world, JumpSound.Flash, flashLoudness * medium, clarity);
        }
    }

    private sealed class FlashPatch
    {
        // Frame space: an origin patch moves with the ship
        public Vector3D Local;
        public float Radius;
        public float Peak;
        public float Phase;
        public double StartsAt;
        public double EndsAt;

        public float Brightness(double now)
        {
            var age = (float)(now - StartsAt);
            var left = (float)(EndsAt - now);
            if (age < 0 || left < 0)
                return 0f;

            var envelope = Math.Min(1f, age / 0.08f) * Math.Min(1f, left / (float)(EndsAt - StartsAt) * 1.5f);
            var flicker = 0.7f + 0.3f * (float)Math.Sin(age * 23f + Phase);
            return Peak * envelope * flicker;
        }
    }

    /// <summary>Occasional sparks shooting out of the smoke</summary>
    private void UpdateSparks(double now, double dt)
    {
        for (var i = sparks.Count - 1; i >= 0; i--)
        {
            var spark = sparks[i];
            spark.Head += spark.Velocity * dt;
            if (now > spark.EndsAt || WorldAllowed(spark.Head) < spark.Length)
                sparks.RemoveAt(i);
        }

        var intensity = LightningIntensity(now);
        if (intensity <= 0.01f || Shape.Puffs.Count < 2)
            return;

        var rate = 7.0 * Math.Max(1f, intensity);
        var spawned = 0;
        while (now >= nextSparkAt && spawned < 3)
        {
            nextSparkAt = Math.Max(nextSparkAt, now - 0.2) + -Math.Log(1.0 - random.NextDouble()) / rate;
            spawned++;
            // A weak intensity thins the events out rather than scheduling the next one seconds ahead
            if (random.NextDouble() > intensity)
                continue;

            if (!RandomPointInCloud(now, 0.1f, out var start))
                continue;

            var spark = new Spark
            {
                Head = start,
                Velocity = RandomDirection() * (8 + 20 * random.NextDouble()),
                Length = (float)(0.4 + random.NextDouble()),
                Thickness = (float)(0.06 + 0.06 * random.NextDouble()),
                Color = SparkColor() * (0.6f + 0.4f * intensity),
                StartsAt = now,
                EndsAt = now + 0.25 + random.NextDouble() * 0.4,
            };
            if (WorldAllowed(start) >= spark.Length)
                sparks.Add(spark);
        }
    }

    // The discharges have one color: the lightning color, some sparks with a white hot core
    private Vector3 SparkColor() => random.Next(3) == 0 ? Vector3.Lerp(Palette.Lightning * SparkBrightness, SparkWhite, 0.5f) : Palette.Lightning * SparkBrightness;

    private sealed class Spark
    {
        public Vector3D Head;
        public Vector3D Velocity;
        public float Length;
        public float Thickness;
        public Vector3 Color;
        public double StartsAt;
        public double EndsAt;

        public void Draw(double now)
        {
            var speed = Velocity.Length();
            if (speed < 1e-3)
                return;

            var fade = 1f - SmoothStep((float)((now - StartsAt) / (EndsAt - StartsAt)));
            var alpha = 0.55f * fade;
            if (alpha < 0.01f)
                return;

            // The texture's bright head at the line's origin, its tail fading out behind it
            var direction = (Vector3)(-Velocity / speed);
            MyTransparentGeometry.AddLineBillboard(SparkMaterial, new Vector4(Color * alpha, alpha), Head, direction, Length, Thickness, MyBillboard.BlendTypeEnum.Standard);
        }
    }

    private sealed class Bolt
    {
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

        public void Draw(Random random, ref Vector3 boltColor, ref Vector3 haloColor)
        {
            // Flicker
            var flicker = (float)(0.4 + random.NextDouble() * 0.6) * Brightness;
            var coreAlpha = BoltAlpha * flicker;
            var haloAlpha = BoltHaloAlpha * flicker;
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
                MyTransparentGeometry.AddLineBillboard(BoltMaterial, core, points[i], direction, length, thickness, MyBillboard.BlendTypeEnum.Standard);
                MyTransparentGeometry.AddLineBillboard(BoltMaterial, halo, points[i], direction, length, thickness * 5f, MyBillboard.BlendTypeEnum.Standard);
            }
        }
    }
}
