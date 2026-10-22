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
/// outside its cloud or where that cloud is thin (a big ship's cloud is wide), so it gets a cloud of its own: the same
/// black smoke around the camera, with lightning, and the other clouds are not drawn for it. The camera goes into the fog
/// with the ship, so what is near it, the ship itself above all, shows through the smoke and fades into it with distance:
/// uniform dark layers facing the camera, one every FogStep, each fading in softly in front of what it covers (no edge
/// where it cuts through the hull), take the light of anything at a distance d down to exp(-d / FogLength). Behind the
/// last of them the opaque Backdrop closes the view: nothing outside the cloud shows. Wisps of smoke drift in the fog,
/// lit by the discharges near the camera. They move with the camera and keep their orientation in the world, so they turn
/// past the view when the camera turns.
/// </summary>
public static class CameraCloud
{
    // The puffs fill a sphere around the camera out to Radius, the bigger ones no closer than Hollow
    private const float Radius = 26f;
    private const float Hollow = 3f;
    private const int PuffCount = 420;

    // A third of them close around the camera, the ones the flashes light
    private const int NearCount = PuffCount / 3;
    private const float NearFrom = 1.5f;
    private const float NearTo = 5f;

    // The wisps are thin: the fog takes the light with distance, the wisps only show the smoke moving. The flashes light
    // the near ones brighter for it
    private const float Density = 0.45f;
    private const float FlashBoost = 2f;

    // How much the bolts light the puffs farther out than the near shell
    private const float FarGlow = 0.2f;

    // The fog: a layer every FogStep from FogFrom to FogTo, light from FogLength away is down to 1/e when the fog has
    // formed; the backdrop at FogTo closes the view only once the fog in front of it is thick, so the hull of a big ship
    // farther away never shows an edge there
    private const float FogLength = 8f;
    private const float FogFrom = 1f;
    private const float FogTo = 40f;
    private const float FogStep = 2f;
    private const float BackdropFrom = 0.5f;
    private const float BackdropOver = 0.4f;

    // Bolts closer than this fill the view. They are drawn additive under the fog: it dims them with distance like
    // everything in it, and the hull in front of one hides it
    private const float BoltClearance = 4f;
    private const double BoltRate = 7.0;

    // Discharges in the near shell, seen as the smoke they light
    private const double FlashRate = 4.0;

    private static MyStringId? material;
    private static MyStringId Material => material ??= UnlitMaterials.Get("Smoke");

    // Opaque white, fading in over a fog step in front of the scene
    private static MyStringId? fogMaterial;
    private static MyStringId FogMaterial => fogMaterial ??= UnlitMaterials.Get("Square", FogStep / 0.3f);

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
    public static void Draw(Vector3D camera, float intensity, float lightning, double now)
    {
        UpdateBolts(camera, intensity * lightning, now);
        UpdateFlashes(intensity * lightning, now);
        DrawFog(intensity);

        var drift = Quaternion.CreateFromAxisAngle(Vector3.Up, (float)(now * 0.04));
        var flashTint = Palette.Flash;
        foreach (var puff in Puffs)
        {
            var world = camera + Vector3.Transform(puff.Local, drift);
            var alpha = Math.Min(1f, puff.Alpha * Cloud.PuffOpacity * Density * intensity);
            if (alpha < 0.003f)
                continue;

            var color = Cloud.SmokeColor + Glow(world, camera, puff.Near, now) * (puff.Near ? FlashBoost : 1f) * Cloud.FlashGlow * flashTint;
            MyTransparentGeometry.AddPointBillboard(Material, new Vector4(color * alpha, alpha), world, puff.Radius, puff.Angle + puff.Spin * (float)now);
        }

        foreach (var bolt in Bolts)
            bolt.Draw(Random, 1f, MyBillboard.BlendTypeEnum.AdditiveBottom);
    }

    /// <summary>0..1, how much is left of the light of something this far from the camera, in the fog of this intensity</summary>
    public static float Transmission(double distance, float intensity) =>
        (float)Math.Exp(-intensity * Math.Max(distance - FogFrom, 0.0) / FogLength) * (distance >= FogTo ? 1f - BackdropOpacity(intensity) : 1f);

    /// <summary>0..1, how much the backdrop at <see cref="FogTo"/> hides</summary>
    public static float BackdropOpacity(float intensity) => SmoothStep((intensity - BackdropFrom) / BackdropOver);

    private static void DrawFog(float intensity)
    {
        var view = MySector.MainCamera;
        var matrix = view.WorldMatrix;
        var halfHeight = (float)Math.Tan(view.FieldOfView * 0.5);
        var halfWidth = halfHeight * Math.Max(1f, view.AspectRatio);
        var halfDiagonal = (float)Math.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight);
        var alpha = 1f - (float)Math.Exp(-intensity * FogStep / FogLength);
        var color = new Vector4(Cloud.SmokeColor * alpha, alpha);
        for (var distance = FogFrom; distance < FogTo; distance += FogStep)
        {
            MyTransparentGeometry.AddBillboardOriented(FogMaterial, color, matrix.Translation + matrix.Forward * distance, matrix.Left, matrix.Up,
                distance * halfDiagonal * 1.2f);
        }

        Backdrop.Draw(FogTo, BackdropOpacity(intensity));
    }

    private static float SmoothStep(float x)
    {
        x = MathHelper.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
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
