using System;
using System.Collections.Generic;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Effects;

/// <summary>
/// The cloud around a third person camera that jumps with the ship. The camera can be anywhere around the ship, often
/// outside its cloud or where that cloud is thin (a big ship's cloud is wide), so it gets a cloud of its own: a shell of
/// the same black smoke around the camera, with lightning, hiding everything outside it, and the other clouds are not
/// drawn for it. Smoke billboards are depth tested, so what is close to the camera (the hull of the ship in a close third
/// person view) can only be hidden by the puffs in front of it: a dense shell of them close around the camera, the only
/// ones the lightning lights up much. The puffs farther out stay as dark as the hull, so the lit smoke behind it never
/// shows its outline. It moves with the camera and keeps its orientation in the world, so it turns past the view when
/// the camera turns.
/// </summary>
public static class CameraCloud
{
    // The puffs fill a sphere around the camera out to Radius, the bigger ones no closer than Hollow
    private const float Radius = 26f;
    private const float Hollow = 3f;
    private const int PuffCount = 420;

    // A third of them close around the camera, in front of anything near it
    private const int NearCount = PuffCount / 3;
    private const float NearFrom = 1.5f;
    private const float NearTo = 5f;

    // As dense as the ship's cloud when it is formed (Cloud.CoverDensity)
    private const float Density = 2.2f;

    // How much the bolts light the puffs farther out than the near shell (lit, they would show the outline of the hull
    // in front of them)
    private const float FarGlow = 0.2f;

    // Behind the puffs, at the shell's outer edge: dark layers facing the camera, and behind them the opaque Backdrop. The
    // smoke texture is wispy, about 0.6 opaque where it is dense, so each layer's alpha makes up for that.
    private const int BackdropLayers = 6;
    private const float BackdropDistance = Radius + 4f;
    private const float TextureOpacity = 0.6f;

    // The backdrop comes in front of the ship by this much, but no nearer to the camera than NearestBackdrop
    private const double ShipClearance = 2.0;
    private const double NearestBackdrop = 1.2;

    // Bolts closer than this fill the view (as in Cloud). They are drawn additive on top: the near shell would hide them
    private const float BoltClearance = 8f;
    private const double BoltRate = 7.0;

    // Discharges in the near shell, seen as the smoke they light
    private const double FlashRate = 4.0;

    private static MyStringId? material;
    private static MyStringId Material => material ??= UnlitMaterials.Get("Smoke");

    private struct CameraPuff
    {
        public Vector3 Local;
        public float Radius, Alpha, Angle, Spin;
        public bool Near;
    }

    private static readonly CameraPuff[] Puffs = new CameraPuff[PuffCount];
    private static readonly List<Bolt> Bolts = new List<Bolt>();
    private static readonly List<FlashPatch> Flashes = new List<FlashPatch>();
    private static readonly Random Random = new Random();
    private static double nextBoltAt;
    private static double nextFlashAt;

    static CameraCloud()
    {
        var random = new Random(1097);
        for (var i = 0; i < PuffCount; i++)
        {
            var near = i < NearCount;
            var radius = near ? 2.5f + 1.5f * (float)random.NextDouble() : 4f + 4f * (float)random.NextDouble();
            var distance = near
                ? NearFrom + (NearTo - NearFrom) * (float)random.NextDouble()
                : Hollow + radius * 0.5f + (Radius - Hollow - radius * 0.5f) * (float)Math.Pow(random.NextDouble(), 0.7);
            Puffs[i] = new CameraPuff
            {
                Local = RandomDirection(random) * distance,
                Radius = radius,
                Alpha = 0.55f + 0.45f * (float)random.NextDouble(),
                Angle = (float)(random.NextDouble() * Math.PI * 2),
                Spin = (float)((random.NextDouble() - 0.5) * 0.4),
                Near = near,
            };
        }
    }

    public static void Clear()
    {
        Bolts.Clear();
        Flashes.Clear();
    }

    /// <param name="camera">The camera's position</param>
    /// <param name="intensity">0..1, how far the ship's cloud has formed around the jump</param>
    /// <param name="lightning">0..1, the lightning in the ship's cloud</param>
    /// <param name="shipDistance">How far the nearest grid of the jumping ship is from the camera</param>
    public static void Draw(Vector3D camera, float intensity, float lightning, double now, double shipDistance)
    {
        UpdateBolts(camera, intensity * lightning, now);
        UpdateFlashes(intensity * lightning, now);
        DrawBackdrop(intensity, now, shipDistance);

        var drift = Quaternion.CreateFromAxisAngle(Vector3.Up, (float)(now * 0.04));
        var flashTint = Palette.Flash;
        foreach (var puff in Puffs)
        {
            var world = camera + Vector3.Transform(puff.Local, drift);
            var alpha = Math.Min(1f, puff.Alpha * Cloud.PuffOpacity * Density * intensity);
            if (alpha < 0.003f)
                continue;

            var color = Cloud.SmokeColor + Glow(world, camera, puff.Near, now) * Cloud.FlashGlow * flashTint;
            MyTransparentGeometry.AddPointBillboard(Material, new Vector4(color * alpha, alpha), world, puff.Radius, puff.Angle + puff.Spin * (float)now);
        }

        foreach (var bolt in Bolts)
            bolt.Draw(Random, 1f, MyBillboard.BlendTypeEnum.AdditiveTop);
    }

    private static void DrawBackdrop(float intensity, double now, double shipDistance)
    {
        var view = MySector.MainCamera;
        var matrix = view.WorldMatrix;
        var halfHeight = (float)Math.Tan(view.FieldOfView * 0.5);
        var halfWidth = halfHeight * Math.Max(1f, view.AspectRatio);
        var halfDiagonal = (float)Math.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight);
        var coverage = Math.Min(1f - (float)Math.Pow(1f - intensity, 2.2), 0.995f);
        var alpha = Math.Min(1f, (1f - (float)Math.Pow(1f - coverage, 1.0 / BackdropLayers)) / TextureOpacity);
        var color = new Vector4(Cloud.SmokeColor * alpha, alpha);
        for (var i = 0; i < BackdropLayers; i++)
        {
            var distance = BackdropDistance + i;
            var angle = (float)now * (i % 2 == 0 ? 0.05f : -0.04f) + i * 1.3f;
            var rotation = Quaternion.CreateFromAxisAngle(matrix.Forward, angle);
            MyTransparentGeometry.AddBillboardOriented(Material, color, matrix.Translation + matrix.Forward * distance,
                Vector3.Transform(matrix.Left, rotation), Vector3.Transform(matrix.Up, rotation), distance * halfDiagonal * 2.6f);
        }

        // The layers still let a little of a bright planet or asteroid through: behind them the view is closed. In front
        // of the ship too: a hull or a lit window nearer than the layers would show through the puffs' gaps
        Backdrop.Draw(MathHelper.Clamp(shipDistance - ShipClearance, NearestBackdrop, BackdropDistance + BackdropLayers), Backdrop.Opacity(intensity));
    }

    private static void UpdateBolts(Vector3D camera, float intensity, double now)
    {
        Bolts.RemoveAll(b => now > b.EndsAt);
        if (intensity <= 0.01f)
            return;

        if (nextBoltAt < now - 0.2)
            nextBoltAt = now;
        while (now >= nextBoltAt)
        {
            nextBoltAt += -Math.Log(1.0 - Random.NextDouble()) / BoltRate;
            if (Random.NextDouble() > intensity)
                continue;

            var start = camera + RandomDirection(Random) * (float)(BoltClearance + (Radius - BoltClearance) * Random.NextDouble());
            var length = 3 + 6 * Random.NextDouble();
            var end = start + RandomDirection(Random) * (float)length;
            if (Vector3D.Distance(end, camera) < BoltClearance)
                continue;

            var thickness = (float)MathHelper.Clamp(length * 0.03, 0.08, 0.5);
            Bolts.Add(new Bolt(Bolt.Jagged(start, end, Random), thickness, intensity * (0.5f + 0.5f * (float)Random.NextDouble()), now + 0.05 + Random.NextDouble() * 0.12, length * 2.5 + 10));
        }
    }

    private static void UpdateFlashes(float intensity, double now)
    {
        Flashes.RemoveAll(f => now > f.EndsAt);
        if (intensity <= 0.01f)
            return;

        if (nextFlashAt < now - 0.5)
            nextFlashAt = now;
        while (now >= nextFlashAt)
        {
            nextFlashAt += -Math.Log(1.0 - Random.NextDouble()) / FlashRate;
            if (Random.NextDouble() > intensity)
                continue;

            Flashes.Add(new FlashPatch
            {
                Local = RandomDirection(Random) * (float)(NearFrom + (NearTo - NearFrom) * Random.NextDouble()),
                Radius = 4f + 3f * (float)Random.NextDouble(),
                Peak = intensity * (0.5f + 0.5f * (float)Random.NextDouble()),
                Phase = (float)(Random.NextDouble() * Math.PI * 2),
                StartsAt = now,
                EndsAt = now + 0.4 + 0.8 * Random.NextDouble(),
            });
        }
    }

    /// <summary>The light of the discharges at a puff: the flashes light only the near shell, the bolts the far puffs a little</summary>
    private static float Glow(Vector3D world, Vector3D camera, bool near, double now)
    {
        var glow = 0f;
        if (near)
        {
            foreach (var patch in Flashes)
            {
                var d = Vector3D.Distance(world, camera + patch.Local);
                if (d < patch.Radius)
                {
                    var x = (float)(1.0 - d / patch.Radius);
                    glow += patch.Brightness(now) * x * x;
                }
            }
        }

        foreach (var bolt in Bolts)
        {
            var d = Vector3D.Distance(world, bolt.Middle);
            if (d < bolt.Reach)
            {
                var x = (float)(1.0 - d / bolt.Reach);
                glow += bolt.Brightness * x * x * (near ? 1f : FarGlow);
            }
        }

        return Math.Min(glow, 1f);
    }

    private static Vector3 RandomDirection(Random random)
    {
        while (true)
        {
            var v = new Vector3(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);
            var lengthSquared = v.LengthSquared();
            if (lengthSquared > 1e-4f && lengthSquared <= 1f)
                return v / (float)Math.Sqrt(lengthSquared);
        }
    }
}
