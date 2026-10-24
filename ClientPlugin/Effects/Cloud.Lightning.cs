using System;
using VRage.Game;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Effects;

// The discharges in the fog: lightning, flashes and sparks
public sealed partial class Cloud
{
    public float LightningIntensity(double now)
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
            case CloudKind.Camera:
                return gateOf.LightningIntensity(now) * gateFormation;
            default:
                return 0.6f * (1f - SmoothStep((float)(now - StartedAt) / 3f));
        }
    }

    /// <summary>This cloud's discharges now, for the light they cast on the ship (see <see cref="FlashLights"/>)</summary>
    public void AddLightSources(double now)
    {
        // A bolt's light is a flash of its own (UpdateBolts)
        foreach (var patch in flashes)
            AddLightSource(Vector3D.Transform(patch.Local, frame), patch.Radius, patch.Brightness(now));
    }

    private void AddLightSource(Vector3D position, float radius, float brightness)
    {
        // The gate's discharges are far out in its shell, beyond the ship: their light comes in from where they are,
        // cast at the ship's edge
        if (Kind == CloudKind.Camera && gateOf.Grid != null && !gateOf.Grid.MarkedForClose)
        {
            var ship = gateOf.Grid.PositionComp.WorldVolume;
            var toPatch = position - ship.Center;
            var distance = toPatch.Length();
            var edge = ship.Radius + GateLightClearance;
            if (distance > edge)
                position = ship.Center + toPatch * (edge / distance);
            radius = (float)Math.Max(radius, ship.Radius);
        }

        FlashLights.Sources.Add((position, radius, brightness));
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

            // The light it casts on the smoke and the ship swells and fades as a flash where it strikes, the bolt itself
            // a crack in it: lit only while the bolt shows, the light went on and off within a frame or two
            flashes.Add(new FlashPatch
            {
                Local = Vector3D.Transform(bolt.Middle, MatrixD.Invert(frame)),
                Radius = (float)bolt.Reach,
                Peak = bolt.Brightness,
                Phase = (float)(random.NextDouble() * Math.PI * 2),
                StartsAt = now,
                EndsAt = now + FlashPatch.Duration(random),
            });

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

            // A camera's gate is silent: its ship's cloud is heard
            if (Kind != CloudKind.Camera && AbortSound(now, ref loudness))
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
        var rate = (3.0 + Shape.Puffs.Count / 1000.0) * Math.Max(1f, intensity) * (Kind == CloudKind.Camera ? GateFlashes : 1.0);
        var spawned = 0;
        while (now >= nextFlashAt && spawned < 2)
        {
            nextFlashAt = Math.Max(nextFlashAt, now - 0.5) + -Math.Log(1.0 - random.NextDouble()) / rate;
            spawned++;
            // A weak intensity thins the events out rather than scheduling the next one seconds ahead
            if (random.NextDouble() > intensity)
                continue;

            // The gate's discharges are where the camera sees them: a few draws for a puff in its view
            var puff = Shape.Puffs[random.Next(Shape.Puffs.Count)];
            Evaluate(ref puff, now, out var alpha, out _, out var local);
            for (var draw = 0; Kind == CloudKind.Camera && !InGateView(local) && draw < 12; draw++)
            {
                puff = Shape.Puffs[random.Next(Shape.Puffs.Count)];
                Evaluate(ref puff, now, out alpha, out _, out local);
            }

            if (alpha < 0.05f || Kind == CloudKind.Camera && !InGateView(local))
                continue;

            var patch = new FlashPatch
            {
                Local = local,
                Radius = size * (0.2f + 0.2f * (float)random.NextDouble()),
                Peak = intensity * FlashPatch.PeakShare(random),
                Phase = (float)(random.NextDouble() * Math.PI * 2),
                StartsAt = now,
                EndsAt = now + FlashPatch.Duration(random),
            };
            flashes.Add(patch);

            // A discharge deep in the fog: heard as a dull flash, the deeper and farther the quieter
            var world = Vector3D.Transform(local, frame);
            var distance = Vector3D.Distance(world, cameraPosition);
            var clarity = patch.Peak * (float)Math.Exp(-FogAttenuation * FogDepth(world, cameraPosition));
            var flashLoudness = clarity * Near(distance);
            if (Kind != CloudKind.Camera && AbortSound(now, ref flashLoudness))
                JumpSounds.PlayDischarge(world, JumpSound.Flash, flashLoudness * medium, clarity);
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

        public void Draw(double now, float fade)
        {
            var speed = Velocity.Length();
            if (speed < 1e-3)
                return;

            var alpha = 0.55f * (1f - SmoothStep((float)((now - StartsAt) / (EndsAt - StartsAt)))) * fade;
            if (alpha < 0.01f)
                return;

            // The texture's bright head at the line's origin, its tail fading out behind it
            var direction = (Vector3)(-Velocity / speed);
            MyTransparentGeometry.AddLineBillboard(SparkMaterial, new Vector4(Color * alpha, alpha), Head, direction, Length, Thickness, MyBillboard.BlendTypeEnum.Standard);
        }
    }
}
