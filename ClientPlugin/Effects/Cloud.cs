using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClientPlugin.Core;
using Sandbox.Game.Entities;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Effects;

public enum CloudKind
{
    /// <summary>Forms around the ship during the countdown, moves with it, stays behind and fades at the jump</summary>
    Origin,

    /// <summary>Dense around the ship the moment it arrives, stationary, dissipates</summary>
    Destination,

    /// <summary>Stand-in at the broadcast jump target until the arriving grid has been replicated to this client</summary>
    Provisional,
}

/// <summary>
/// One black smoke cloud: its puffs (from a <see cref="CloudShape"/>), its frame, its timeline, its lightning, sparks and
/// sound. The cloud is a spherical portal into a dimension of thick black fog with constant energy discharges: many
/// faint puffs overlap into the sphere, fog leaks out at its rim, discharges deep in the fog light regions of it up.
/// </summary>
public sealed partial class Cloud
{
    private static readonly string[] MaterialSources = { "Smoke", "Smoke_c", "Smoke_lit" };
    private static MyStringId[] materials;
    private static MyStringId[] Materials => materials ??= Array.ConvertAll(MaterialSources, UnlitMaterials.Get);

    private static readonly MyStringId SparkMaterial = MyStringId.GetOrCompute("WhiteSpark");

    // Opacity of one puff: the cloud's density comes from the overlap of many
    internal const float PuffOpacity = 0.75f;

    // Smoke color, premultiplied by alpha when drawn: black, lit only where a discharge flashes (the flash color)
    internal static readonly Vector3 SmokeColor = new Vector3(0.003f, 0.0025f, 0.004f);

    // The exit is bigger than the entrance: the portal's outer fog moves out this much more within the first second
    private const float ExitGrowth = 0.35f;

    // The fog thickens to this many times its density while the ship is inside it, so no sunlit edge of the hull shows
    // through before the jump or right after the arrival
    private const float CoverDensity = 2.2f;
    private const float HullCoverDensity = 3f;

    // The sun's glints on the hull are many times brighter than white: one puff's opacity, capped at 1, leaves enough of
    // them to show. While the ship is inside, every puff at the hull is drawn this many times more, turned and smaller.
    private const int ShroudLayers = 2;

    // For an observer outside the portal the same: dark billboards in the portal's front, between the camera and the
    // ship, sized to the ship's silhouette
    private const int FrontShroudLayers = 3;

    // Behind them an opaque core: opaque out to this many times the ship's outline, as far as the portal allows
    private const double CoreMargin = 1.1;

    // The optical depth of the layers in front of the ship when they are fully drawn (see ShroudOpacity)
    private const float ShroudDepth = 9f;

    // The sun's light at the cloud this frame (see SmokeLight)
    private float sunlight;

    // Leaking fog: one wisp flows out this far (portal radii) over its cycle
    private const float LeakDistance = 0.35f;

    // How much of the sun a puff hides, times its opacity: thin fog already hides the sun (see JumpEffects.Draw)
    private const float SunOcclusion = 1.6f;

    // How bright a discharge lights the fog around it (HDR, times the flash color): a lighter purple region
    internal const float FlashGlow = 2.5f;

    // Light of a discharge at the hull on the fog in front of the ship (additive, two layers)
    private const float FrontGlow = 1.5f;

    // How deep in the fog (portal radii, along the line of sight) a discharge is heard as a dull flash, not a bolt
    private const float DeepDischarge = 0.75f;

    // Loudness lost to the fog: exp(-FogAttenuation x depth in portal radii)
    private const float FogAttenuation = 0.8f;

    // A discharge is at full loudness up to this far (m), at 1/distance beyond
    private const float DischargeReference = 20f;

    // A bolt at least this loud is a strike, a quieter one a snap
    private const float StrikeLoudness = 0.3f;

    // A strike this soon (s) after the cloud's last one is heard as a snap: never dropped
    private const double StrikeSpacing = 0.35;

    // The abort's burst: not every discharge gets a sound, so the tear is not drowned. One gets one only if
    // AbortSoundSpacing s have passed since the last and then with AbortSoundKeep probability, at AbortSoundGain
    // (-6 dB) against a normal jump's lightning
    private const double AbortSoundSpacing = 0.03;
    private const double AbortSoundKeep = 0.6;
    private const float AbortSoundGain = 0.5f;

    // The gate inhales this long before the jump; its sound ends at the jump
    public const double PreJumpLead = 1.8;

    // After the jump, the closing starts this late at the cloud left behind and at the exit: after the jump's burst
    private const double DepartureClosingDelay = 1.0;
    private const double ExitClosingDelay = 1.5;

    private const float SparkBrightness = 9f;
    private static readonly Vector3 SparkWhite = new Vector3(7f, 7f, 9f);

    public readonly CloudKind Kind;
    public readonly MyCubeGrid Grid;
    public readonly long GridId;

    /// <summary>Entity ids of the grids that jump together (the physical group at the start)</summary>
    public readonly HashSet<long> GroupIds = new HashSet<long>();

    /// <summary>
    /// Entity ids of the blocks of those grids and of the characters seated in their cockpits, at the start and at the
    /// jump: the HUD marker of a remote broadcaster (an antenna, a pilot's suit) knows only its block or character
    /// </summary>
    public readonly HashSet<long> MemberIds = new HashSet<long>();

    private Task<CloudShape> shapeTask;
    private Task<List<Obstacle>> obstacleTask;
    public CloudShape Shape { get; private set; }
    private List<Obstacle> obstacles = new List<Obstacle>();

    private readonly Random random;

    // Local frame of the puffs. Origin: follows the grid until the jump, then keeps the jump velocity.
    private MatrixD frame;
    private Vector3D velocity;
    private Vector3D detachedTranslation;
    private Vector3D arrival;
    private bool attached;

    // Destination of a ship that was in its origin cloud: the ship goes from smoke into smoke, no fade in
    private bool seamless;

    public readonly double CreatedAt;

    /// <summary>
    /// Destination and provisional clouds: when the ship arrived (its jump). A destination cloud made late, once the
    /// grid streamed in on a client that learned of the jump from the broadcast, arrived when its provisional cloud did
    /// </summary>
    public double ArrivedAt;

    // A destination cloud made late takes over from the provisional cloud of the broadcast: it runs on the jump's
    // timeline, fades in over the provisional cloud fading out, and starts at its visible size
    private Cloud takenOver;
    private double takeOverAt = double.NaN;
    private float takeOverScale = 1f;
    private GatePhase takenOverPhase = GatePhase.None;
    private bool natural;
    private const float TakeOverFadeSeconds = 0.6f;
    private const float TakeOverResizeSeconds = 2.5f;

    // Origin: countdown end; Destination/Provisional: when the cloud started (shape ready)
    public double JumpAt;
    public double StartedAt = double.NaN;
    private double detachedAt = double.NaN;
    private double abortedAt = double.NaN;
    private double fadeAt = double.NaN;

    // A provisional cloud's smoke stays this long after its fade-out started: the destination cloud taking over fades in
    // first, so the two never thin out together
    private double fadeVisualDelay;

    public bool Finished { get; private set; }

    /// <summary>The third person camera has been in the fog of this cloud: its own fog stands in for the cloud (see JumpEffects.Draw)</summary>
    public bool WrappedCamera;

    public bool Started => !double.IsNaN(StartedAt);

    /// <summary>Since when the cloud forms where it is drawn: a destination cloud made late, since it took over</summary>
    public double FormingSince => double.IsNaN(takeOverAt) ? StartedAt : Math.Max(StartedAt, takeOverAt);
    public bool Detached => !double.IsNaN(detachedAt);
    public double DetachedAt => detachedAt;
    public Vector3D Departure => detachedTranslation;
    public Vector3D Arrival => arrival;
    public Vector3D Center => Vector3D.Transform(Shape?.BubbleCenter ?? Vector3D.Zero, frame);

    /// <summary>The portal's radius, 0 until the shape is ready</summary>
    public double PortalRadius => Shape?.BubbleSemiAxes.Max() ?? 0;

    /// <summary>Half the diagonal of the ship's box, 0 until the shape is ready (or without a ship)</summary>
    public double ShipRadius => Shape != null && Shape.LocalBox.Valid ? Shape.LocalBox.HalfExtents.Length() : 0;

    private readonly List<Bolt> bolts = new List<Bolt>();
    private readonly List<Spark> sparks = new List<Spark>();
    private readonly List<FlashPatch> flashes = new List<FlashPatch>();
    private double nextBoltAt;
    private double nextSparkAt;
    private double nextFlashAt;
    private double lastDrawAt = double.NaN;

    private GateSound gate;
    private double lastStrikeAt = double.NegativeInfinity;

    // The jump is heard at this cloud: not at the cloud left behind by a camera that jumped along, not at a destination
    // whose provisional cloud already sounded it
    private bool jumpHeard = true;

    // This frame's view of the ship inside the cloud, for keeping the fog behind it dark (see BehindShip)
    private float shroudNow;
    private Vector3D shroudCamera;
    private Vector3D shroudDirection;
    private double shroudCenterDistance;
    private double shroudShipRadius;
    private double shroudCosAngle;

    // Per frame clamping: the ship's frame (sealed rooms of the jumping grids) and the frames of the obstacle grids
    private bool clampToShip;
    private MatrixD worldToShip;
    private float shipClampRelax;
    private readonly List<(AirMap Map, MatrixD WorldToGrid)> obstacleFrames = new List<(AirMap, MatrixD)>();

    /// <summary>Puff draws found reaching into sealed air and cut down, by whose sealed air, for the debug log</summary>
    public int ClampedByShip { get; private set; }

    public int ClampedByObstacle { get; private set; }

    private Cloud(CloudKind kind, MyCubeGrid grid, double now, int seed)
    {
        Kind = kind;
        Grid = grid;
        GridId = grid?.EntityId ?? 0;
        CreatedAt = now;
        ArrivedAt = now;
        random = new Random(seed);
        if (grid != null)
        {
            frame = grid.WorldMatrix;
            GroupIds.Add(grid.EntityId);
            var group = MyCubeGridGroups.Static.Physical.GetGroup(grid);
            if (group != null)
            {
                foreach (var node in group.Nodes)
                {
                    if (node.NodeData != null)
                        GroupIds.Add(node.NodeData.EntityId);
                }
            }

            AddMembers();
        }
    }

    private void AddMembers()
    {
        foreach (var id in GroupIds)
        {
            if (!MyEntities.TryGetEntityById(id, out MyCubeGrid grid) || grid.MarkedForClose)
                continue;

            foreach (var block in grid.GetFatBlocks())
            {
                MemberIds.Add(block.EntityId);
                // Seated, not remote controlling from elsewhere
                if (block is MyCockpit { Pilot: not null } cockpit)
                    MemberIds.Add(cockpit.Pilot.EntityId);
            }
        }
    }

    /// <summary>A grid of the jumping group, one of its blocks, or a character seated in one</summary>
    public bool Carries(long entityId) => GroupIds.Contains(entityId) || MemberIds.Contains(entityId);

    public static Cloud CreateOrigin(MyCubeGrid grid, double now, double jumpAt)
    {
        var cloud = new Cloud(CloudKind.Origin, grid, now, (int)(grid.EntityId ^ (long)(now * 1000)))
        {
            JumpAt = jumpAt,
            StartedAt = now,
            attached = true,
        };
        cloud.shapeTask = ShapeBuilder.Start(grid, cloud.random.Next());
        cloud.obstacleTask = ShapeBuilder.StartObstacles(grid, cloud.GroupIds);
        return cloud;
    }

    /// <summary>Destination cloud of a grid that just jumped, reusing the origin cloud's shape when there was one (same grid, same orientation)</summary>
    public static Cloud CreateDestination(MyCubeGrid grid, double now, Cloud origin)
    {
        var cloud = new Cloud(CloudKind.Destination, grid, now, (int)(grid.EntityId * 31 ^ (long)(now * 1000)));
        if (origin != null)
        {
            cloud.GroupIds.UnionWith(origin.GroupIds);
            cloud.shapeTask = origin.shapeTask;
            cloud.seamless = true;

            // Drawn from this very frame on, the ship must not show between its two clouds
            if (origin.Shape != null)
            {
                cloud.Shape = origin.Shape;
                cloud.StartedAt = now;
            }
        }
        else
        {
            cloud.shapeTask = ShapeBuilder.Start(grid, cloud.random.Next());
        }

        cloud.obstacleTask = ShapeBuilder.StartObstacles(grid, cloud.GroupIds);
        return cloud;
    }

    public static Cloud CreateProvisional(Vector3D target, long gridId, double now)
    {
        var cloud = new Cloud(CloudKind.Provisional, null, now, (int)(target.X * 7 + target.Y * 13 + target.Z * 17))
        {
            frame = MatrixD.CreateTranslation(target),
            StartedAt = now,
        };
        cloud.GroupIds.Add(gridId);

        // No grid yet: a plain sphere of smoke around the target
        var scale = MathHelper.Clamp(Config.Current.CloudScale, 0.25f, 8f);
        var shape = new CloudShape
        {
            BubbleCenter = Vector3D.Zero,
            BubbleSemiAxes = new Vector3D(30 * scale),
        };
        for (var i = 0; i < 700; i++)
        {
            var direction = new Vector3D(cloud.random.NextDouble() - 0.5, cloud.random.NextDouble() - 0.5, cloud.random.NextDouble() - 0.5);
            if (direction.LengthSquared() < 1e-6)
                continue;
            direction.Normalize();
            var rim = Math.Pow(cloud.random.NextDouble(), 0.5);
            shape.Puffs.Add(new Puff
            {
                Position = direction * (30 * scale * rim),
                Radius = (5f + 5f * (float)cloud.random.NextDouble()) * scale,
                MaxRadius = 14f * scale,
                Angle = (float)(cloud.random.NextDouble() * Math.PI * 2),
                Spin = (float)((cloud.random.NextDouble() - 0.5) * 0.4),
                Alpha = 0.55f + 0.45f * (float)cloud.random.NextDouble(),
                Seed = (float)cloud.random.NextDouble(),
                Material = (byte)cloud.random.Next(MaterialSources.Length),
                Drift = direction,
                Rim = (float)rim,
            });
        }

        cloud.Shape = shape;
        cloud.obstacles = new List<Obstacle>();
        return cloud;
    }

    /// <summary>The jump happened: the cloud stays where the ship was, moving on with the ship's velocity</summary>
    public void Detach(MatrixD preJumpMatrix, Vector3D arrivalPosition, Vector3 jumpVelocity, double now, bool heard)
    {
        if (!attached)
            return;

        AddMembers();
        jumpHeard = heard;
        attached = false;
        frame = preJumpMatrix;
        detachedTranslation = preJumpMatrix.Translation;
        arrival = arrivalPosition;
        velocity = jumpVelocity;
        detachedAt = now;
    }

    public void Abort(double now)
    {
        if (double.IsNaN(abortedAt))
            abortedAt = now;
    }

    public bool Aborted => !double.IsNaN(abortedAt);

    // Lightning, sparks and flashes at the abort, relative to a formed cloud's: a violent end, as strong as the cloud had grown
    private float AbortBurst => 2.5f * (Shape == null ? 0f : SmoothStep((float)((abortedAt - StartedAt) / 3.0)));

    // The abort sound (tear-discharge): the tear's rip and four strikes at 0, 0.05, 0.12 and
    // 0.22 s, then the discharge winding down with a pop at 0.16, 0.42 and 0.8 s, gone by about 1.3 s
    private static readonly float[] AbortStrikes = { 0f, 0.05f, 0.12f, 0.22f };
    private static readonly (float At, float Strength)[] AbortPops = { (0.16f, 0.45f), (0.42f, 0.3f), (0.8f, 0.18f) };

    /// <summary>
    /// 0..1, the glow of an aborted cloud this long after the abort, following its sound: it lights up at once and
    /// flickers with the tear's strikes for a quarter of a second, then loses its glow and powers down with a weak
    /// flicker at each pop, dark by about 1.2 s
    /// </summary>
    private static float AbortGlow(float age)
    {
        if (age < 0f)
            return 0f;

        var tear = age < 0.15f ? 1f : (float)Math.Exp(-(age - 0.15f) / 0.08f);
        var glow = 0.6f * tear;
        foreach (var at in AbortStrikes)
            glow += 0.4f * Pulse(age - at, 0.03f);
        foreach (var (at, strength) in AbortPops)
            glow += strength * Pulse(age - at, 0.06f);
        return Math.Min(1f, glow) * (1f - SmoothStep((age - 0.9f) / 0.4f));
    }

    // A short flash starting at 0 and lasting about width
    private static float Pulse(float t, float width) => t < 0f ? 0f : (float)Math.Exp(-t / width);

    /// <summary>The jump was already heard at this place (from the provisional cloud this one replaces)</summary>
    /// <summary>
    /// A destination cloud made late (the grid streamed in after the broadcast) takes over from the provisional cloud:
    /// the jump was heard there, and the gate goes on in the phase it reached
    /// </summary>
    public void TakeOver(Cloud provisional, double now)
    {
        jumpHeard = false;
        takenOver = provisional;
        takeOverAt = now;
        provisional.fadeVisualDelay = TakeOverFadeSeconds * 0.67;
        ArrivedAt = provisional.ArrivedAt;
        takenOverPhase = provisional.gate?.Phase ?? GatePhase.None;
    }

    /// <summary>Starts fading out a provisional cloud once the real one took over</summary>
    public void FadeOut(double now)
    {
        if (double.IsNaN(fadeAt))
            fadeAt = now;
    }

    /// <summary>True once the shape is ready, the cloud then starts (destination) and can be drawn</summary>
    public bool Poll(double now)
    {
        // The obstacles first: a new cloud starts only with them, a destination taking over its origin's shape gets them a moment later
        if (obstacleTask != null && obstacleTask.IsCompleted)
        {
            if (obstacleTask.Status == TaskStatus.RanToCompletion)
                obstacles = obstacleTask.Result;
            else
                Log.Error(obstacleTask.Exception ?? new Exception("Obstacle maps failed"));
            obstacleTask = null;
        }

        if (Shape == null && shapeTask != null && shapeTask.IsCompleted && obstacleTask == null)
        {
            if (shapeTask.Status == TaskStatus.RanToCompletion)
            {
                Shape = shapeTask.Result;
                Log.Debug($"{Kind} cloud of {Grid?.DisplayName}: {Shape.Puffs.Count} puffs, {Shape.MapCount} grids, {obstacles.Count} obstacle grids, ready after {(now - CreatedAt) * 1000:0} ms");
            }
            else
            {
                Log.Error(shapeTask.Exception ?? new Exception("Shape build failed"));
                Finished = true;
            }
        }

        if (Shape != null && !Started)
        {
            if (takenOver == null)
            {
                StartedAt = now;
            }
            else
            {
                // On the jump's timeline, at the provisional cloud's visible size
                StartedAt = Math.Min(now, ArrivedAt);
                natural = true;
                var own = VisibleRadius(now);
                natural = false;
                var shown = takenOver.VisibleRadius(now);
                takeOverScale = own > 1 && shown > 1 ? (float)MathHelper.Clamp(shown / own, 0.25, 4) : 1f;
                Log.Debug($"Destination cloud of {Grid?.DisplayName} takes over at t{now - ArrivedAt:+0.00}: {shown:0.0} m shown, {own:0.0} m its own");
            }
        }

        if (attached && Grid != null)
        {
            if (Grid.MarkedForClose)
                Abort(now);
            else
                frame = Grid.WorldMatrix;
        }

        if (Detached)
            frame.Translation = detachedTranslation + velocity * (now - detachedAt);

        if (Started && Age(now) > Lifetime)
            Finished = true;

        if (!Started && now - CreatedAt > 20)
            Finished = true;

        if (!Detached && Kind == CloudKind.Origin && double.IsNaN(abortedAt) && now > JumpAt + 5)
            Abort(now);

        return Shape != null;
    }

    private double Age(double now)
    {
        if (!double.IsNaN(fadeAt))
            return now - fadeAt;
        if (!double.IsNaN(abortedAt))
            return now - abortedAt;
        if (Detached)
            return now - detachedAt;
        return now - StartedAt;
    }

    private double Lifetime
    {
        get
        {
            if (!double.IsNaN(fadeAt))
                return Kind == CloudKind.Provisional ? 0.6 + fadeVisualDelay : 3.5;
            if (!double.IsNaN(abortedAt))
                return 2.0;
            switch (Kind)
            {
                case CloudKind.Origin:
                    return Detached ? 12.0 : double.MaxValue;
                case CloudKind.Destination:
                    return 12.5;
                default:
                    return 12.0;
            }
        }
    }

    /// <summary>How much of the cloud has formed around a camera that jumps with the ship, for its <see cref="CameraCloud"/></summary>
    public float CameraCloudIntensity(double now)
    {
        if (!Started || Finished)
            return 0f;

        // Torn apart with the cloud
        if (Aborted)
            return Kind == CloudKind.Origin ? FogAround(abortedAt) * (1f - SmoothStep((float)(now - abortedAt) / 0.8f)) : 0f;

        switch (Kind)
        {
            case CloudKind.Origin:
                return Detached ? 0f : FogAround(now);
            case CloudKind.Destination:
                return 1f - SmoothStep((float)((now - StartedAt - 0.6) / 4.0));
            default:
                return 0f;
        }
    }

    /// <summary>
    /// How dense the smoke is around a camera inside the portal, 0..1, for the <see cref="Backdrop"/> behind it, and how
    /// far along the view direction the backdrop goes: past the portal's far side, so the ship and the whole cloud stay
    /// in front of it. 0 for a camera outside the portal, which sees the cloud from outside.
    /// </summary>
    public float InsideDensity(Vector3D camera, Vector3D forward, double now, out double distance)
    {
        distance = 0;
        if (Shape == null || !Started || Finished)
            return 0f;

        // The exit's outer fog moves out in its first second (Evaluate)
        var radius = Shape.BubbleSemiAxes.X * (Kind == CloudKind.Origin ? 1f : 1f + ExitGrowth * SmoothStep((float)(now - StartedAt) / 0.8f));
        var toCenter = Center - camera;
        var inside = 1f - SmoothStep((float)((toCenter.Length() / radius - 0.8) / 0.25));
        if (inside <= 0f)
            return 0f;

        distance = Vector3D.Dot(toCenter, forward) + radius;
        return inside * Formed(now);
    }

    /// <summary>0..1, how far the smoke has formed, following the puffs' opacity over the cloud's life (Evaluate)</summary>
    private float Formed(double now)
    {
        if (Aborted)
            return Kind == CloudKind.Origin ? FormingAround(abortedAt) * (1f - SmoothStep((float)(now - abortedAt) / 0.8f)) : 0f;

        var age = (float)(now - StartedAt);
        float formed;
        switch (Kind)
        {
            case CloudKind.Origin:
                formed = Detached ? FormingAround(detachedAt) * (1f - SmoothStep((float)(now - detachedAt - 0.6) / 3.5f)) : FormingAround(now);
                break;
            case CloudKind.Destination:
                formed = (seamless ? 1f : SmoothStep(age / 0.25f)) * (1f - SmoothStep((age - 0.6f) / 4.0f));
                if (!double.IsNaN(takeOverAt) && !natural)
                    formed *= SmoothStep((float)(now - takeOverAt) / TakeOverFadeSeconds);
                break;
            default:
                formed = SmoothStep(age / 0.3f) * (double.IsNaN(fadeAt) ? 1f - SmoothStep((age - 8f) / 3f) : 1f - SmoothStep((float)(now - fadeAt - fadeVisualDelay) / 0.45f));
                break;
        }

        if (!double.IsNaN(fadeAt) && Kind != CloudKind.Provisional)
            formed *= 1f - SmoothStep((float)(now - fadeAt) / 2f);
        return formed;
    }

    private float FormingAround(double now) => SmoothStep((float)((now - (JumpAt - 5.5)) / 4.5));

    // The camera's fog forms from the countdown's start, with the cloud, fully formed a second before the jump (it takes
    // over from the cloud while both are thin)
    private float FogAround(double now) => SmoothStep((float)((now - StartedAt) / Math.Max(1.0, JumpAt - 1.0 - StartedAt)));

    private static float SmoothStep(float x)
    {
        x = MathHelper.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    /// <summary>
    /// For the diagnostics log: the cloud's visible size now, the 90th percentile of its puffs' outer edges (distance from
    /// the bubble's centre plus the puff's radius) among the puffs at a quarter of their full opacity or more; 0 when none
    /// shows
    /// </summary>
    public double VisibleRadius(double now)
    {
        if (Shape == null || Finished)
            return 0;

        var edges = new List<double>();
        for (var i = 0; i < Shape.Puffs.Count; i++)
        {
            var puff = Shape.Puffs[i];
            Evaluate(ref puff, now, out var alpha, out var radius, out var local);
            if (alpha >= 0.25f * puff.Alpha * PuffOpacity)
                edges.Add((local - Shape.BubbleCenter).Length() + radius);
        }

        if (edges.Count == 0)
            return 0;

        edges.Sort();
        return edges[(int)(0.9 * (edges.Count - 1))];
    }

    /// <summary>Opacity, size and position of a puff now, in the cloud's frame</summary>
    private void Evaluate(ref Puff puff, double now, out float alpha, out float radius, out Vector3D local)
    {
        local = puff.Position;
        radius = puff.Radius;

        // Thin in the portal's outer edge so its outline is soft but defined, and the small puffs in the niches of the hull
        // a bit fainter. Denser inside, around the hull, so the ship disappears in it
        alpha = puff.Alpha * PuffOpacity * (1f + 1.2f * (1f - SmoothStep(puff.Rim / 0.6f))) * (1f - 0.6f * SmoothStep((puff.Rim - 0.7f) / 0.3f)) * MathHelper.Clamp(0.55f + radius * 0.15f, 0.55f, 1f);

        if (puff.Leak)
        {
            // Fog leaking out of the portal: a wisp flows out from the rim, swells and thins, then the next one comes
            var cycle = 2.5 + 2.5 * puff.Seed;
            var phase = (float)(((now - CreatedAt) / cycle + puff.Seed * 7.3) % 1.0);
            local += puff.Drift * (phase * LeakDistance * (float)Shape.BubbleSemiAxes.X);
            radius *= 1f + 0.9f * phase;
            alpha = puff.Alpha * PuffOpacity * 0.7f * (float)Math.Sin(Math.PI * phase);
        }

        switch (Kind)
        {
            case CloudKind.Origin:
            {
                // Forms from the bubble's rim inwards over the countdown, flowing into vented rooms through their openings
                // Fully formed a second before the jump (smoke flowing into vented rooms may take longer): the ship is
                // hidden before it leaves
                var formStart = StartedAt;
                var formEnd = Math.Max(JumpAt - 2.0, formStart + 1.0);
                var appearAt = formStart + (formEnd - formStart) * puff.Order * 0.8 + puff.FlowDelay;
                var formedUntil = Detached ? detachedAt : !double.IsNaN(abortedAt) ? abortedAt : now;
                var formed = SmoothStep((float)((formedUntil - appearAt) / 1.0));
                alpha *= formed * (1f + ((puff.Hull ? HullCoverDensity : CoverDensity) - 1f) * SmoothStep((float)((formedUntil - (JumpAt - 3.0)) / 1.5)));
                radius *= 0.55f + 0.45f * formed;

                if (Detached)
                {
                    // Left behind: lingers, spreads out and fades, no ship around any more to keep out of
                    var age = (float)(now - detachedAt);
                    var hold = 0.6f + 1.2f * puff.Seed;
                    var life = 4.5f + 5f * puff.Seed;
                    alpha *= 1f - SmoothStep((age - hold) / life);
                    radius *= 1f + 0.8f * age / (hold + life);
                    local += puff.Drift * ((0.5f + 1.3f * puff.Seed) * age);
                }
                else if (!double.IsNaN(abortedAt))
                {
                    // The jump failed: the cloud is ripped apart at once, blown outwards and gone within a second or so
                    var age = (float)(now - abortedAt);
                    alpha *= 1f - SmoothStep(age / (0.5f + 0.8f * puff.Seed));
                    var outward = local - Shape.BubbleCenter;
                    var length = outward.Length();
                    if (length > 1e-3)
                        local += outward / length * ((6f + 14f * puff.Seed) * age + (6f + 6f * puff.Seed) * (1f - (float)Math.Exp(-age / 0.1f)));
                    local += puff.Drift * (4f * age);
                    radius *= 1f + 0.6f * age;
                }

                break;
            }

            case CloudKind.Destination:
            {
                // Dense at once, holds a moment, then dissipates while spreading slowly; stationary. The exit is bigger:
                // the outer fog moves out within the first second
                var age = (float)(now - StartedAt);
                var grow = 1f + ExitGrowth * SmoothStep((puff.Rim - 0.3f) / 0.7f) * SmoothStep(age / 0.8f);
                local = Shape.BubbleCenter + (local - Shape.BubbleCenter) * grow;
                radius *= grow;
                var hold = 1.2f + 1.2f * puff.Seed;
                var life = 4f + 5f * puff.Seed;
                alpha *= (seamless ? 1f : SmoothStep(age / 0.25f)) * (1f - SmoothStep((age - hold) / life));
                alpha *= 1f + ((puff.Hull ? HullCoverDensity : CoverDensity) - 1f) * (1f - SmoothStep((age - 0.5f) / 2.5f));
                radius *= 1f + 0.5f * Math.Max(0f, age) / (hold + life);
                local += puff.Drift * ((0.4f + 1.0f * puff.Seed) * age);

                if (!double.IsNaN(takeOverAt) && !natural)
                {
                    var since = (float)(now - takeOverAt);
                    var scale = takeOverScale + (1f - takeOverScale) * SmoothStep(since / TakeOverResizeSeconds);
                    local = Shape.BubbleCenter + (local - Shape.BubbleCenter) * scale;
                    radius *= scale;
                    alpha *= SmoothStep(since / TakeOverFadeSeconds);
                }

                break;
            }

            default:
            {
                // An exit cloud of its own size: its outer fog moves out within the first second
                var age = (float)(now - StartedAt);
                var grow = 1f + ExitGrowth * SmoothStep((puff.Rim - 0.3f) / 0.7f) * SmoothStep(age / 0.8f);
                local *= grow;
                radius *= grow;
                alpha *= SmoothStep(age / 0.3f);
                if (!double.IsNaN(fadeAt))
                    alpha *= 1f - SmoothStep((float)(now - fadeAt - fadeVisualDelay) / 0.45f);
                else
                    alpha *= 1f - SmoothStep((age - 8f) / 3f);
                local += puff.Drift * (0.5f * age);
                break;
            }
        }

        if (!double.IsNaN(fadeAt) && Kind != CloudKind.Provisional)
            alpha *= 1f - SmoothStep((float)(now - fadeAt) / 2f);
    }

    /// <summary>
    /// Whether a world position is in the air smoke reaches around the ship (open space or a vented room), as opposed
    /// to the inside of a pressurized room or a block. Unknown until the shape is ready.
    /// </summary>
    public bool IsOpenAt(Vector3D world)
    {
        if (Shape == null || Grid == null || Grid.MarkedForClose || Shape.MapCount == 0)
            return false;

        return Shape.IsOpen(Vector3D.Transform(world, MatrixD.Invert(Grid.WorldMatrix)));
    }

    #region Clamping

    /// <summary>
    /// The sealed rooms to keep the smoke out of this frame: the jumping grids while the cloud is around them (the ship
    /// moves through a stationary destination cloud) and the grids nearby that do not jump.
    /// </summary>
    private void PrepareClamping()
    {
        clampToShip = Grid != null && !Grid.MarkedForClose && Shape.MapCount != 0 && (attached || Kind == CloudKind.Destination);
        shipClampRelax = 0f;
        if (clampToShip)
        {
            worldToShip = MatrixD.Invert(Grid.WorldMatrix);
        }
        else if (Detached && Shape.MapCount != 0)
        {
            // Where the ship was: the smoke flows into the space it left over a second and a half, rather than
            // every puff there growing to full size at once in the frame of the jump
            var age = (float)(lastDrawAt - detachedAt);
            if (age < 1.5f)
            {
                clampToShip = true;
                worldToShip = MatrixD.Invert(frame);
                shipClampRelax = 14f * Math.Max(age, 0f) / 1.5f;
            }
        }

        obstacleFrames.Clear();
        foreach (var obstacle in obstacles)
        {
            if (!obstacle.Grid.MarkedForClose)
                obstacleFrames.Add((obstacle.Map, MatrixD.Invert(obstacle.Grid.WorldMatrix)));
        }
    }

    /// <summary>Radius of the largest sphere around a world position that stays out of all sealed air near the cloud</summary>
    private float WorldAllowed(Vector3D world) => WorldAllowed(world, out _, out _);

    private float WorldAllowed(Vector3D world, out float byShip, out float byObstacles)
    {
        byShip = clampToShip ? Shape.AllowedRadius(Vector3D.Transform(world, worldToShip)) + shipClampRelax : float.MaxValue;
        byObstacles = float.MaxValue;
        foreach (var (map, worldToGrid) in obstacleFrames)
        {
            byObstacles = Math.Min(byObstacles, map.AllowedRadius(Vector3D.Transform(world, worldToGrid)));
            if (byObstacles <= 0f)
                break;
        }

        return Math.Min(byShip, byObstacles);
    }

    #endregion
}
