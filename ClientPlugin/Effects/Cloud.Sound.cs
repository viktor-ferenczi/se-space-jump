using System;
using Sandbox.Game.World;
using VRageMath;

namespace ClientPlugin.Effects;

// The gate's sound at the cloud
public sealed partial class Cloud
{
    /// <summary>
    /// The gate's roar follows the drive and the cloud: it rises with the countdown and fades out as the cloud
    /// dissipates (left behind, at the exit or torn apart by an abort)
    /// </summary>
    /// <param name="nearness">1 for a dense, big cloud, falling to 0 as it thins and shrinks into the distance</param>
    public float GateLevel(double now, out float nearness)
    {
        nearness = 0f;
        if (!Started || Finished)
            return 0f;

        // The level curves of the stages, with t relative to the jump
        float level;
        switch (Kind)
        {
            case CloudKind.Origin when !double.IsNaN(abortedAt):
            {
                var age = (float)(now - abortedAt);
                level = Countdown(abortedAt, out _) * (1f - SmoothStep(age / 1.6f));
                nearness = 0.6f * (1f - SmoothStep(age / 1.0f));
                break;
            }

            case CloudKind.Origin when Detached:
            {
                var t = (float)(now - detachedAt);
                level = 1f - SmoothStep(t / 9.5f);
                nearness = 1f - SmoothStep(t / 7f);
                break;
            }

            case CloudKind.Origin:
                level = Countdown(now, out nearness);
                break;

            default:
            {
                var t = (float)(now - StartedAt);
                level = 1f - SmoothStep((t - 1f) / 9f);
                nearness = 1f - SmoothStep((t - 0.5f) / 8f);
                break;
            }
        }

        if (!double.IsNaN(fadeAt))
            level *= 1f - SmoothStep((float)(now - fadeAt) / (Kind == CloudKind.Provisional ? 0.6f : 3f));

        return level;
    }

    // The roar during the countdown (from its start, normally t-10): 25 % rising to full at t-1.5; the density's
    // nearness from 30 % to full at t-3
    private float Countdown(double now, out float nearness)
    {
        var length = Math.Max(1.0, JumpAt - StartedAt);
        var since = now - StartedAt;
        nearness = 0.3f + 0.7f * SmoothStep((float)(since / (0.7 * length)));
        return 0.25f + 0.75f * SmoothStep((float)(since / (0.85 * length)));
    }

    /// <summary>The gate's phase now, see <see cref="GatePhase"/></summary>
    public GatePhase SoundPhase(double now)
    {
        if (!Started || Finished)
            return GatePhase.None;

        switch (Kind)
        {
            case CloudKind.Origin when Aborted:
                return GatePhase.Abort;

            case CloudKind.Origin when Detached:
                if (now - detachedAt >= DepartureClosingDelay)
                    return GatePhase.Closing;
                return jumpHeard ? GatePhase.Jump : GatePhase.Cruising;

            case CloudKind.Origin:
                // Opening while the fog gathers, open once it covers the ship, inhaling right before the jump
                if (now >= JumpAt - PreJumpLead)
                    return GatePhase.PreJump;
                return now < JumpAt - 7.0 ? GatePhase.Opening : GatePhase.Cruising;

            default:
            {
                // The exit: the jump, then lingering and fading (as GateLevel). Those who arrive with the ship heard
                // the jump already.
                if (now - StartedAt >= ExitClosingDelay || !double.IsNaN(fadeAt))
                    return GatePhase.Closing;
                return jumpHeard ? GatePhase.Jump : GatePhase.Cruising;
            }
        }
    }

    /// <summary>Simulation frame: the gate's sound at the cloud</summary>
    public void UpdateSound(double now, Vector3D cameraPosition)
    {
        HeardLevel = 0f;
        if (Finished || Shape == null && Kind != CloudKind.Provisional)
        {
            StopSound();
            return;
        }

        var phase = SoundPhase(now);
        var level = GateLevel(now, out var nearness);
        if (phase == GatePhase.None && gate == null)
            return;

        var newGate = gate == null;
        gate ??= new GateSound();
        if (newGate && takenOverPhase != GatePhase.None)
            gate.Continue(takenOverPhase);

        var inside = Inside(cameraPosition);
        medium = MySession.Static?.Settings?.RealisticSound == true ? inside : 1f;
        var gain = DistanceGain(cameraPosition, ref nearness) * medium;
        HeardLevel = level * gain;
        // A stand-in cloud has no shape to be inside of: echoes, but no wails
        var radius = Shape?.BubbleSemiAxes.AbsMax() ?? 0;
        gate.Update(now, phase, Center, level, nearness, gain, radius, radius < 1 ? 0f : inside);
        if (now - lastHeardLogAt >= 0.5)
        {
            lastHeardLogAt = now;
            Log.Debug($"Gate heard: {Kind} at {Vector3D.Distance(cameraPosition, Center):0} m, radius {radius:0} m, gain {gain:0.000}, level {level:0.00}, phase {phase}, inside {inside:0.00}");
        }
    }

    private double lastHeardLogAt = double.NegativeInfinity;

    /// <summary>0..1, how loud the cloud's roar is at the camera this frame (for the music ducking)</summary>
    public float HeardLevel { get; private set; }

    // 0..1, how much of the cloud's sound reaches the camera at all. Realistic sound: no sound travels through vacuum,
    // the fog is what carries the gate's sound, it is heard only inside the cloud (see Inside); the game still muffles
    // it in a helmet or a sealed cockpit. Arcade sound: everywhere, quieter with the distance.
    private float medium = 1f;

    /// <summary>
    /// 0..1, how far the camera is inside the gate: inside the cloud (fading out over the outer 15 % of its radius beyond
    /// the rim) or on a grid the fog reaches, the jumping ship, a ship next to it
    /// </summary>
    private float Inside(Vector3D cameraPosition)
    {
        var radius = Shape?.BubbleSemiAxes.AbsMax() ?? 0;
        if (radius < 1)
            return 1f;

        var inside = 1f - SmoothStep((float)((Vector3D.Distance(cameraPosition, Center) - radius) / (0.15 * radius)));
        if (inside >= 1f)
            return 1f;

        return JumpEffects.CameraGridTouches(new BoundingSphereD(Center, radius)) ? 1f : inside;
    }

    /// <summary>
    /// The cloud's loudness at the camera, 1 inside: the game itself hardly attenuates within its range. A big cloud
    /// carries farther. From afar, only the far (dark) roar is heard.
    /// </summary>
    private float DistanceGain(Vector3D cameraPosition, ref float nearness)
    {
        var radius = (float)(Shape?.BubbleSemiAxes.AbsMax() ?? 0);
        if (radius < 1f)
            return 1f;

        var outside = (float)Math.Max(0, Vector3D.Distance(cameraPosition, Center) - radius);
        nearness *= MathHelper.Clamp(1f - outside / (3f * radius), 0f, 1f);
        return 1f / (1f + outside / radius);
    }

    /// <summary>
    /// How deep in the fog a point is from the camera, in portal radii: the length of the line of sight inside the
    /// portal's sphere up to the point
    /// </summary>
    private float FogDepth(Vector3D world, Vector3D cameraPosition)
    {
        var radius = Shape.BubbleSemiAxes.AbsMax();
        if (radius < 1)
            return 0f;

        var toPoint = world - cameraPosition;
        var distance = toPoint.Length();
        if (distance < 1e-3)
            return 0f;

        var direction = toPoint / distance;
        var toCenter = Center - cameraPosition;
        var entry = 0.0;
        if (toCenter.LengthSquared() > radius * radius)
        {
            // Outside: where the line of sight enters the sphere
            var along = Vector3D.Dot(toCenter, direction);
            var off = toCenter.LengthSquared() - along * along;
            entry = along - Math.Sqrt(Math.Max(radius * radius - off, 0));
        }

        return (float)(Math.Max(distance - entry, 0) / radius);
    }

    // Loudness left of a discharge this deep in the fog and this far from the camera
    private readonly Random abortSoundRandom = new Random();
    private double lastAbortSoundAt = double.NegativeInfinity;

    /// <summary>Whether a discharge now gets a sound, and its loudness in the abort's burst (see AbortSoundSpacing)</summary>
    private bool AbortSound(double now, ref float loudness)
    {
        if (!Aborted || now < abortedAt)
            return true;

        if (now - lastAbortSoundAt < AbortSoundSpacing || abortSoundRandom.NextDouble() >= AbortSoundKeep)
            return false;

        lastAbortSoundAt = now;
        loudness *= AbortSoundGain;
        return true;
    }

    // A discharge at full volume up to DischargeReference away, 1/distance beyond
    private static float Near(double distance) => (float)Math.Min(1.0, DischargeReference / Math.Max(distance, 1e-3));

    public void StopSound()
    {
        if (Shape != null)
            gate?.Stop(Center);
        else
            gate?.Kill();
        gate = null;
    }
}
