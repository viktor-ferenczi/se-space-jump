using System;
using VRage.Game;
using VRageMath;

namespace ClientPlugin.Effects;

// Drawing the puffs, the shroud in front of the camera and the light through the cloud
public sealed partial class Cloud
{
    /// <summary>Draws the cloud and multiplies the transmittance of the view from the camera towards the sun through it</summary>
    public void Draw(double now, Vector3D cameraPosition, Vector3D sunDirection, float darkness, bool lightning, double maxDistance, ref float sunTransmittance)
    {
        if (Shape == null || !Started || Finished)
            return;

        if (Detached)
            frame.Translation = detachedTranslation + velocity * (now - detachedAt);

        var center = Center;
        var distance = Vector3D.Distance(cameraPosition, center);
        var dt = double.IsNaN(lastDrawAt) ? 0 : MathHelper.Clamp(now - lastDrawAt, 0, 0.1);
        lastDrawAt = now;
        if (distance > maxDistance)
            return;

        PrepareClamping();

        // Fewer, bigger puffs far away
        var stride = distance < 2000 ? 1 : distance < 5000 ? 2 : 4;
        var strideScale = (float)Math.Sqrt(stride);

        if (lightning)
        {
            UpdateBolts(now, cameraPosition);
            UpdateSparks(now, dt);
            UpdateFlashes(now, cameraPosition);
        }
        else
        {
            flashes.Clear();
        }

        var flashTint = Palette.Flash;

        // The moment the jump fails the whole cloud lights up from inside with the tear, then powers down with a
        // flicker at each pop of its sound (AbortGlow)
        var abortFlash = 0f;
        if (Aborted)
        {
            var age = (float)(now - abortedAt);
            abortFlash = 0.5f * AbortBurst * AbortGlow(age);
        }

        var time = now - CreatedAt;
        var transmittance = 1.0;
        var shroud = Shroud(now);
        PrepareBehindShip(cameraPosition, shroud);
        var hullLight = 1f - FrontLight(cameraPosition, shroud);
        var puffs = Shape.Puffs;
        for (var i = 0; i < puffs.Count; i += stride)
        {
            var puff = puffs[i];
            Evaluate(ref puff, now, out var alpha, out var radius, out var local);
            alpha *= darkness;
            if (alpha < 0.003f)
                continue;

            var world = Vector3D.Transform(local, frame);
            radius *= strideScale;

            var allowed = WorldAllowed(world, out var byShip, out var byObstacles);
            if (allowed < radius)
            {
                // Puffs sampled right at their limit come back a rounding error below it
                if (byShip < radius * 0.98f)
                    ClampedByShip++;
                if (byObstacles < radius * 0.98f)
                    ClampedByObstacle++;
                alpha *= MathHelper.Clamp(allowed / (radius * 0.4f) - 1.5f, 0f, 1f);
                radius = allowed;
                if (alpha < 0.003f)
                    continue;
            }

            // Sun behind the cloud: the puff's soft disc dims the line of sight in proportion to its opacity there
            var toPuff = world - cameraPosition;
            var along = Vector3D.Dot(toPuff, sunDirection);
            if (along > -radius)
            {
                var off = toPuff.LengthSquared() - along * along;
                var r2 = (double)radius * radius;
                if (off < r2)
                    transmittance *= 1.0 - Math.Min(0.98, SunOcclusion * alpha) * (1.0 - off / r2);
            }

            // For a camera outside the portal the fog hugging the hull stays black in the discharges: lit, it would draw
            // the hull's outline where the hull cuts its billboards (the fog in front of the ship is lit instead)
            var glow = (puff.Hull ? hullLight : 1f) * Glow(world, now) * FlashGlow * (1f - BehindShip(world)) * NearCamera(world, cameraPosition);
            var color = SmokeColor + (glow + abortFlash) * flashTint;
            var angle = puff.Angle + puff.Spin * (float)time;
            MyTransparentGeometry.AddPointBillboard(Materials[puff.Material], new Vector4(color * alpha, Math.Min(alpha, 1f)), world, radius, angle);

            if (puff.Hull && shroud > 0.01f)
            {
                var layerAlpha = Math.Min(alpha * shroud, 1f);
                for (var layer = 1; layer <= ShroudLayers; layer++)
                    MyTransparentGeometry.AddPointBillboard(Materials[(puff.Material + layer) % Materials.Length], new Vector4(color * layerAlpha, layerAlpha), world, radius * (1f - 0.15f * layer), angle + 1.3f * layer);
            }
        }

        sunTransmittance *= (float)transmittance;

        if (shroud > 0.01f)
            DrawFrontShroud(now, cameraPosition, shroud, flashTint);

        var boltColor = Palette.Lightning * BoltBrightness;
        var haloColor = Palette.Lightning * BoltHaloBrightness;
        foreach (var bolt in bolts)
        {
            if (BehindShip(bolt.Middle) < 0.5f)
                bolt.Draw(random, ref boltColor, ref haloColor);
        }

        foreach (var spark in sparks)
            spark.Draw(now);
    }

    /// <summary>
    /// A camera outside the portal looks at the ship through the fog in the portal's front. The sun's glints on the
    /// hull's edges are bright enough to shine through what the puffs leave, so a few dark billboards facing the camera
    /// are added there: in front of every part of the hull (the portal's radius is at least 1.3 times the ship's half
    /// diagonal) and no wider than the ship's silhouette, so nothing outside the portal darkens.
    /// </summary>
    private void DrawFrontShroud(double now, Vector3D cameraPosition, float shroud, Vector3 flashTint)
    {
        if (Shape.MapCount == 0)
            return;

        var center = Center;
        var portal = Shape.BubbleSemiAxes.X;
        var toCenter = center - cameraPosition;
        var distance = toCenter.Length();
        if (distance < portal * 1.05)
            return;

        var ship = Shape.LocalBox.HalfExtents.Length();
        var direction = toCenter / distance;
        var depth = distance - portal * 0.9;
        var angle = Math.Min(1.25 * ship / Math.Max(distance - ship, 1.0), 0.9 * Math.Asin(Math.Min(portal / distance, 1.0)));
        var radius = (float)(depth * Math.Tan(angle));
        var position = cameraPosition + direction * depth;
        var color = SmokeColor + Glow(position, now) * FlashGlow * flashTint;
        var alpha = Math.Min(shroud, 1f);
        for (var layer = 0; layer < FrontShroudLayers; layer++)
            MyTransparentGeometry.AddPointBillboard(Materials[layer % Materials.Length], new Vector4(color * alpha, alpha), position, radius * (1f - 0.1f * layer), (float)(now - CreatedAt) * 0.05f + 2.1f * layer);

        // The discharges among the black fog at the hull light the fog in front of the ship instead: drawn at the
        // front shroud's depth, which no part of the hull reaches, so their light never shows the hull's outline
        var frontLight = FrontLight(cameraPosition, shroud);
        var reach = Math.Cos(Math.Min(angle * 1.3, Math.PI / 2));
        var glowDepth = depth - 1.0;
        foreach (var patch in flashes)
        {
            var brightness = patch.Brightness(now);
            if (brightness < 0.02f)
                continue;

            var toPatch = Vector3D.Transform(patch.Local, frame) - cameraPosition;
            var patchDistance = toPatch.Length();
            if (patchDistance < depth || Vector3D.Dot(toPatch, direction) / patchDistance < reach)
                continue;

            var scale = (float)(glowDepth / patchDistance);
            var glowPosition = cameraPosition + toPatch * scale;
            var glow = flashTint * (FrontGlow * brightness * frontLight);
            for (var layer = 0; layer < 2; layer++)
                MyTransparentGeometry.AddPointBillboard(Materials[(layer + 1) % Materials.Length], new Vector4(glow, 0f), glowPosition, patch.Radius * scale * (0.9f - 0.25f * layer), patch.Phase + 2.4f * layer);
        }
    }

    /// <summary>
    /// 0..1, how much the discharges at the hull light the fog in front of the ship rather than the fog at the hull: for
    /// a camera outside the portal, while the ship is inside
    /// </summary>
    private float FrontLight(Vector3D cameraPosition, float shroud)
    {
        if (Shape.MapCount == 0)
            return 0f;

        var outside = (float)(Vector3D.Distance(Center, cameraPosition) / Shape.BubbleSemiAxes.X);
        return SmoothStep((outside - 1.05f) / 0.15f) * Math.Min(shroud, 1f);
    }

    /// <summary>
    /// The discharges light the fog deep in it: around a camera inside the cloud the nearest fog stays black, or one
    /// flash nearby would fill the whole view with its light
    /// </summary>
    private float NearCamera(Vector3D world, Vector3D cameraPosition)
    {
        var reach = 0.3 * Shape.BubbleSemiAxes.X;
        var distanceSquared = Vector3D.DistanceSquared(world, cameraPosition);
        if (distanceSquared >= reach * reach)
            return 1f;

        return SmoothStep((float)(Math.Sqrt(distanceSquared) / reach));
    }

    /// <summary>
    /// While the ship is inside, a discharge lighting the fog behind it would show its silhouette, however dense the fog
    /// in front: the fog behind the ship as this camera sees it stays dark, its bolts are not drawn
    /// </summary>
    private void PrepareBehindShip(Vector3D cameraPosition, float shroud)
    {
        shroudNow = Shape.MapCount == 0 ? 0f : shroud;
        if (shroudNow <= 0.01f)
            return;

        var toCenter = Center - cameraPosition;
        shroudCenterDistance = toCenter.Length();
        shroudShipRadius = Shape.LocalBox.HalfExtents.Length();
        if (shroudCenterDistance <= shroudShipRadius * 1.2)
        {
            // Among the ship: everything around is behind it in some direction, the cloud hides the view anyway
            shroudNow = 0f;
            return;
        }

        shroudCamera = cameraPosition;
        shroudDirection = toCenter / shroudCenterDistance;
        shroudCosAngle = Math.Cos(Math.Min(1.3 * Math.Asin(Math.Min(shroudShipRadius / shroudCenterDistance, 1.0)), Math.PI / 2));
    }

    /// <summary>0..1, whether a world position is in the fog behind the ship from this frame's camera</summary>
    private float BehindShip(Vector3D world)
    {
        if (shroudNow <= 0.01f)
            return 0f;

        var toWorld = world - shroudCamera;
        var distance = toWorld.Length();
        if (distance < 1e-3 || Vector3D.Dot(toWorld, shroudDirection) / distance < shroudCosAngle)
            return 0f;

        // From the ship's front half on: what is beside or behind the hull from this camera
        var behind = SmoothStep((float)((distance - (shroudCenterDistance - shroudShipRadius * 0.5)) / shroudShipRadius));
        return behind * shroudNow;
    }

    /// <summary>0..1, how much of the extra layers at the hull are drawn: while the ship is inside the cloud</summary>
    private float Shroud(double now)
    {
        switch (Kind)
        {
            case CloudKind.Origin when Detached:
                return 1f - SmoothStep((float)(now - detachedAt) / 1.0f);
            case CloudKind.Origin when !double.IsNaN(abortedAt):
                return 0f;
            case CloudKind.Origin:
                return SmoothStep((float)((now - (JumpAt - 3.0)) / 1.5));
            case CloudKind.Destination:
                return 1f - SmoothStep((float)(now - StartedAt - 0.5) / 2.5f);
            default:
                return 0f;
        }
    }
}
