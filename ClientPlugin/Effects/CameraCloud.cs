using System;
using System.Collections.Generic;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Effects;

/// <summary>
/// The cloud around a camera block on the hull of a jumping ship, looking at it from outside: the camera goes into the
/// void with its ship (the third person camera gets a smoke gate instead, see <see cref="CloudKind.Camera"/>). The void
/// is a sphere around the ship's portal that grows to take the camera in. Inside it the whole world is fog of one density: the ship, in the void with the camera,
/// shows through it as through fog, its parts near the camera clearly, the farther ones dimmer, so zooming in on it
/// brings it out of the fog bit by bit. Beyond the void's surface the fog is thick: everything outside the gate
/// (planets, asteroids, the sun) is left behind. Other ships that do not jump are hidden as the fog forms (see
/// <see cref="OutsideGrids"/>).
///
/// The fog is a volume drawn as layers, every part of it a share of one optical depth, so it fades smoothly with the
/// distance and over time and has no edge anywhere: in front of the camera, layers facing it at depths that grow
/// geometrically, each standing for the fog between it and the next one (soft particles: a hull between two layers gets
/// the share of the fog in front of it); at the void's surface, nested spheres. Seen through the layers, something d
/// metres away keeps exp(-d / FogLength) of its light, and something outside the void nothing, once the fog has formed.
///
/// Wisps of smoke drift in the fog, lit by the discharges near the camera. They move with the camera and keep their
/// orientation in the world, so they turn past the view when the camera turns. Farther out, discharges light regions of
/// the fog all over the view, dimmed by the fog in front of them like everything else in it, and the brightest of them
/// light the ship as well: a few point lights of the flash colour, so the hull flickers with the fog around it.
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
    private const float Density = 0.3f;
    private const float FlashBoost = 2f;

    // How much the bolts light the puffs farther out than the near shell
    private const float FarGlow = 0.2f;

    // The fog inside the void: light from FogLength away is down to 1/e once it has formed. The same at any zoom (the
    // whole world is fog): half the ship's half diagonal, never shorter than MinFogLength
    private const float MinFogLength = 10f;
    private const float FogLengthPerShipRadius = 0.5f;

    // Layers facing the camera, from FirstLayer metres on, each LayerGrowth times farther than the one before
    private const float FirstLayer = 0.6f;
    private const float LayerGrowth = 1.3f;
    private const int MaxLayers = 40;

    // The void's surface: at least VoidMargin beyond the camera and the portal. The thick fog beyond it is Shells nested
    // spheres, ShellSpacing of the void's radius apart, OutsideDepth optical depth in all once formed (1e-7 of the light
    // is left), of which the formation's share is its square: what is outside is gone well before the fog in the void
    // has formed
    private const double VoidMargin = 12;
    private const int Shells = 6;
    private const double ShellSpacing = 0.05;
    private const double MinShellSpacing = 2;
    private const float OutsideDepth = 16f;

    // The shells' spheres are cube spheres of this many quads along a cube face's edge
    private const int ShellDivisions = 8;

    // Bolts closer than this fill the view. They are drawn additive under the fog: it dims them with distance like
    // everything in it, and the hull in front of one hides it
    private const float BoltClearance = 4f;
    private const double BoltRate = 7.0;

    // Discharges in the near shell, seen as the smoke they light
    private const double FlashRate = 4.0;

    // Discharges deeper in the fog, all over the view: between FarFlashFrom metres and FarFlashTo fog lengths away, in
    // and a little beyond the view's cone, each lighting a region of the fog about FarFlashSize times its distance across
    private const double FarFlashRate = 7.0;
    private const float FarFlashFrom = 6f;
    private const float FarFlashTo = 2.5f;
    private const float FarFlashSize = 0.45f;
    private const float FarFlashGlow = 0.6f;

    private static MyStringId? material;
    private static MyStringId Material => material ??= UnlitMaterials.Get("Smoke");

    private struct CameraPuff
    {
        public Vector3 Local;
        public float Radius, Alpha, Angle, Spin;
        public bool Near;
    }

    /// <summary>The void around the camera this frame</summary>
    public readonly struct Space
    {
        public readonly Vector3D Camera;
        public readonly Vector3D Center;
        public readonly double Radius;
        public readonly double Spacing;
        public readonly float Length;
        public readonly float Intensity;

        public Space(Vector3D camera, Vector3D center, double radius, double spacing, float length, float intensity)
        {
            Camera = camera;
            Center = center;
            Radius = radius;
            Spacing = spacing;
            Length = length;
            Intensity = intensity;
        }

        /// <summary>0..1, how much is left of the light of something at this place, seen from the camera</summary>
        public float Transmission(Vector3D position)
        {
            if (Intensity <= 0f)
                return 1f;

            var depth = Intensity * Math.Max(Vector3D.Distance(position, Camera) - FirstLayer, 0.0) / Length;
            var outside = (Vector3D.Distance(position, Center) - Radius) / (Shells * Spacing);
            if (outside > 0)
                depth += OutsideOpticalDepth(Intensity) * Math.Min(outside, 1.0);
            return (float)Math.Exp(-depth);
        }
    }

    private static readonly CameraPuff[] Puffs = new CameraPuff[PuffCount];
    private static readonly List<Bolt> Bolts = new List<Bolt>();
    private static readonly List<FlashPatch> Flashes = new List<FlashPatch>();

    // Their Local is relative to the void's centre: they stay in the fog when the camera moves
    private static readonly List<FlashPatch> FarFlashes = new List<FlashPatch>();
    private static double nextFarFlashAt;
    private static readonly Random Random = new Random();
    private static double nextBoltAt;
    private static double nextFlashAt;

    // The layers' depths and their soft particle materials, each fading in over the depth up to the next layer
    private static readonly float[] LayerDepths = new float[MaxLayers + 1];
    private static readonly MyStringId?[] LayerMaterials = new MyStringId?[MaxLayers];

    // Unit directions of the shell sphere's vertices, per cube face
    private static readonly Vector3D[,,] ShellVertices = new Vector3D[6, ShellDivisions + 1, ShellDivisions + 1];

    // The shells' soft particle materials: for spacings in steps of a quarter of an octave
    private static readonly Dictionary<int, MyStringId> ShellMaterials = new Dictionary<int, MyStringId>();

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

        LayerDepths[0] = FirstLayer;
        for (var k = 1; k <= MaxLayers; k++)
            LayerDepths[k] = LayerDepths[k - 1] * LayerGrowth;

        // Cube faces: a normal and two axes across it
        var faces = new[]
        {
            (Vector3D.Right, Vector3D.Up, Vector3D.Forward), (Vector3D.Left, Vector3D.Up, Vector3D.Backward),
            (Vector3D.Up, Vector3D.Forward, Vector3D.Right), (Vector3D.Down, Vector3D.Backward, Vector3D.Right),
            (Vector3D.Forward, Vector3D.Up, Vector3D.Left), (Vector3D.Backward, Vector3D.Up, Vector3D.Right),
        };
        for (var face = 0; face < 6; face++)
        {
            var (normal, a, b) = faces[face];
            for (var i = 0; i <= ShellDivisions; i++)
            for (var j = 0; j <= ShellDivisions; j++)
            {
                var u = Math.Tan((2.0 * i / ShellDivisions - 1) * Math.PI / 4);
                var v = Math.Tan((2.0 * j / ShellDivisions - 1) * Math.PI / 4);
                ShellVertices[face, i, j] = Vector3D.Normalize(normal + a * u + b * v);
            }
        }
    }

    /// <summary>The void of the last frame drawn, for the flames and glares in it (see <see cref="Cover"/>)</summary>
    public static Space Current { get; private set; }

    public static void Clear()
    {
        Bolts.Clear();
        Flashes.Clear();
        FarFlashes.Clear();
        Current = default;
    }

    /// <summary>Optical depth of the fog outside the void at a formation of 0..1</summary>
    public static float OutsideOpticalDepth(float intensity) => OutsideDepth * intensity * intensity;

    /// <summary>0..1, how much of what is outside the void shows through its fog</summary>
    public static float OutsideTransmission(float intensity) => (float)Math.Exp(-OutsideOpticalDepth(intensity));

    /// <param name="camera">The camera's position</param>
    /// <param name="cloud">The cloud of the camera's ship: its portal is in the void</param>
    /// <param name="intensity">0..1, how far the camera's fog has formed</param>
    /// <param name="lightning">0..1, the lightning in the ship's cloud</param>
    public static void Draw(Vector3D camera, Cloud cloud, float intensity, float lightning, double now)
    {
        var center = cloud.Center;
        var toCenter = Vector3D.Distance(camera, center);
        var radius = Math.Max(cloud.PortalRadius, toCenter + VoidMargin);
        var length = Math.Max(MinFogLength, FogLengthPerShipRadius * (float)cloud.ShipRadius);
        var spacing = Math.Max(MinShellSpacing, ShellSpacing * radius);
        Current = new Space(camera, center, radius, spacing, length, intensity);

        var fog = Cloud.SmokeColor;
        UpdateBolts(camera, intensity * lightning, now);
        UpdateFlashes(intensity * lightning, now);
        UpdateFarFlashes(Current, intensity * lightning, now);
        DrawLayers(Current, fog);
        DrawShells(Current, fog);
        DrawFarFlashes(Current, now);

        var drift = Quaternion.CreateFromAxisAngle(Vector3.Up, (float)(now * 0.04));
        var flashTint = Palette.Flash;
        foreach (var puff in Puffs)
        {
            var world = camera + Vector3.Transform(puff.Local, drift);
            var alpha = Math.Min(1f, puff.Alpha * Cloud.PuffOpacity * Density * intensity);
            if (alpha < 0.003f)
                continue;

            var color = fog + Glow(world, camera, puff.Near, now) * (puff.Near ? FlashBoost : 1f) * Cloud.FlashGlow * flashTint;
            MyTransparentGeometry.AddPointBillboard(Material, new Vector4(color * alpha, alpha), world, puff.Radius, puff.Angle + puff.Spin * (float)now);
        }

        foreach (var bolt in Bolts)
            bolt.Draw(Random, 1f, MyBillboard.BlendTypeEnum.AdditiveBottom);
    }

    /// <summary>The fog in the void: layers facing the camera, out to the void's far side</summary>
    private static void DrawLayers(Space fog, Vector3 color)
    {
        var view = MySector.MainCamera;
        var matrix = view.WorldMatrix;
        var halfHeight = (float)Math.Tan(view.FieldOfView * 0.5);
        var halfWidth = halfHeight * Math.Max(1f, view.AspectRatio);
        var halfDiagonal = (float)Math.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight);
        var farthest = Vector3D.Distance(fog.Camera, fog.Center) + fog.Radius;
        for (var k = 0; k < MaxLayers && LayerDepths[k] < farthest; k++)
        {
            var depth = LayerDepths[k];
            var alpha = 1f - (float)Math.Exp(-fog.Intensity * (LayerDepths[k + 1] - depth) / fog.Length);
            if (alpha < 0.001f)
                continue;

            var layerMaterial = LayerMaterials[k] ??= UnlitMaterials.Get("Square", (LayerDepths[k + 1] - depth) / 0.3f);
            MyTransparentGeometry.AddBillboardOriented(layerMaterial, new Vector4(color * alpha, alpha), matrix.Translation + matrix.Forward * depth, matrix.Left, matrix.Up,
                depth * halfDiagonal * 1.2f);
        }
    }

    /// <summary>The thick fog beyond the void's surface: nested spheres around it, the parts in front of the camera</summary>
    private static void DrawShells(Space fog, Vector3 color)
    {
        var alpha = 1f - (float)Math.Exp(-OutsideOpticalDepth(fog.Intensity) / Shells);
        if (alpha < 0.001f)
            return;

        var shellMaterial = ShellMaterial(fog.Spacing);
        var forward = MySector.MainCamera.WorldMatrix.Forward;
        var vector = new Vector4(color * alpha, alpha);
        for (var shell = 0; shell < Shells; shell++)
        {
            var radius = fog.Radius + shell * fog.Spacing;
            for (var face = 0; face < 6; face++)
            for (var i = 0; i < ShellDivisions; i++)
            for (var j = 0; j < ShellDivisions; j++)
            {
                var quad = new MyQuadD
                {
                    Point0 = fog.Center + ShellVertices[face, i, j] * radius,
                    Point1 = fog.Center + ShellVertices[face, i + 1, j] * radius,
                    Point2 = fog.Center + ShellVertices[face, i + 1, j + 1] * radius,
                    Point3 = fog.Center + ShellVertices[face, i, j + 1] * radius,
                };

                // Behind the camera
                if (Vector3D.Dot(quad.Point0 - fog.Camera, forward) < 0 && Vector3D.Dot(quad.Point1 - fog.Camera, forward) < 0 &&
                    Vector3D.Dot(quad.Point2 - fog.Camera, forward) < 0 && Vector3D.Dot(quad.Point3 - fog.Camera, forward) < 0)
                    continue;

                var middle = (quad.Point0 + quad.Point2) * 0.5;
                MyTransparentGeometry.AddQuad(shellMaterial, ref quad, vector, ref middle);
            }
        }
    }

    private static MyStringId ShellMaterial(double spacing)
    {
        var step = (int)Math.Round(Math.Log(spacing, 2) * 4);
        if (!ShellMaterials.TryGetValue(step, out var id))
            ShellMaterials[step] = id = UnlitMaterials.Get("Square", (float)Math.Pow(2, step / 4.0) / 0.3f);
        return id;
    }

    private static void UpdateFarFlashes(Space fog, float intensity, double now)
    {
        FarFlashes.RemoveAll(f => now > f.EndsAt);
        if (intensity <= 0.01f)
            return;

        var view = MySector.MainCamera;
        var forward = view.WorldMatrix.Forward;
        var cone = Math.Cos(Math.Min(view.FieldOfView * Math.Max(1f, view.AspectRatio) * 0.6, Math.PI * 0.45));
        if (nextFarFlashAt < now - 0.5)
            nextFarFlashAt = now;
        while (now >= nextFarFlashAt)
        {
            nextFarFlashAt += -Math.Log(1.0 - Random.NextDouble()) / FarFlashRate;
            if (Random.NextDouble() > intensity)
                continue;

            Vector3D direction;
            do
            {
                direction = RandomDirection(Random);
            } while (Vector3D.Dot(direction, forward) < cone);

            var distance = FarFlashFrom + (FarFlashTo * fog.Length - FarFlashFrom) * Random.NextDouble();
            FarFlashes.Add(new FlashPatch
            {
                Local = fog.Camera + direction * distance - fog.Center,
                Radius = (float)(FarFlashSize * distance) + 3f,
                Peak = intensity * FlashPatch.PeakShare(Random),
                Phase = (float)(Random.NextDouble() * Math.PI * 2),
                StartsAt = now,
                EndsAt = now + FlashPatch.Duration(Random),
            });
        }
    }

    /// <summary>
    /// The far discharges: additive glows of the flash colour in the fog, as bright as the fog in front of them lets
    /// through (the renderer does not dim a billboard by those in front of it), hidden by the hull where it is nearer
    /// </summary>
    private static void DrawFarFlashes(Space fog, double now)
    {
        var tint = Palette.Flash * (Cloud.FlashGlow * FarFlashGlow);
        foreach (var patch in FarFlashes)
        {
            var position = fog.Center + patch.Local;
            var brightness = patch.Brightness(now) * fog.Transmission(position);
            if (brightness < 0.01f)
                continue;

            for (var layer = 0; layer < 2; layer++)
                MyTransparentGeometry.AddPointBillboard(Material, new Vector4(tint * brightness, 0f), position, patch.Radius * (1f - 0.3f * layer), patch.Phase + 2.1f * layer);
        }
    }

    /// <summary>The far discharges, for the light they cast on the ship (see <see cref="FlashLights"/>)</summary>
    public static void AddLightSources(double now)
    {
        var fog = Current;
        foreach (var patch in FarFlashes)
            FlashLights.Sources.Add((fog.Center + patch.Local, patch.Radius, patch.Brightness(now)));
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
                Peak = intensity * FlashPatch.PeakShare(Random),
                Phase = (float)(Random.NextDouble() * Math.PI * 2),
                StartsAt = now,
                EndsAt = now + FlashPatch.Duration(Random),
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
