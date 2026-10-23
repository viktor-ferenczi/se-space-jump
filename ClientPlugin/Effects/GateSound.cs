using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using VRageMath;

namespace ClientPlugin.Effects;

/// <summary>The phases of a gate, each with its own sound slot</summary>
public enum GatePhase
{
    None,

    /// <summary>The countdown: the gate opens (<see cref="JumpSound.Opening"/> once, the roar rising)</summary>
    Opening,

    /// <summary>The gate is open (the <see cref="JumpSound.Cruising"/> roar)</summary>
    Cruising,

    /// <summary>Right before the jump: the gate's echoes rise (<see cref="JumpSound.Wail"/>, <see cref="JumpSound.Echo"/>)</summary>
    PreJump,

    /// <summary>The jump (<see cref="JumpSound.Jump"/> once, its rumble), at the cloud left behind and at the exit; the echoes go on</summary>
    Jump,

    /// <summary>The cloud lingers and fades, the roar and the echoes with it</summary>
    Closing,

    /// <summary>The jump failed, the cloud is torn apart (<see cref="JumpSound.Abort"/> once, the roar dying fast)</summary>
    Abort,
}

/// <summary>
/// The sound of one cloud's gate, heard by everyone near it, pilots and observers alike. A state machine over the
/// gate's phases: entering a phase plays its one-shot slot at the cloud; under them the cruising roar loops at the
/// cloud's center, crossfaded with equal power between its near version (a dense, big, close cloud) and its far one
/// (no highs: a thin, small or distant cloud) and as loud as the cloud's state makes it. From right before the jump to
/// the end of the cloud the gate echoes: a roar now and then somewhere in the cloud, and inside the cloud a constant
/// stream of wails. Loudness and character change smoothly, as the cloud does: the sound never drops off at once.
/// </summary>
public sealed class GateSound
{
    // A one-shot the camera is out of range of when it starts is tried again this long: pilots reach the exit in the
    // frames after the jump
    private const double RetrySeconds = 1;

    private readonly MyEntity3DSoundEmitter near = new MyEntity3DSoundEmitter(null);
    private readonly MyEntity3DSoundEmitter far = new MyEntity3DSoundEmitter(null);

    // An abort fades out every one-shot still sounding in this long
    private const double AbortFadeSeconds = 0.12;

    // One-shots overlap: the jump's rumble, a few wails and an echo all sound at once. Offset is where in the cloud it
    // sounds, from its center; FadeFrom is when the abort started fading the shot out, NaN while it plays on.
    private readonly (MyEntity3DSoundEmitter Emitter, JumpSound Slot, Vector3D Offset, double FadeFrom)[] shots = new (MyEntity3DSoundEmitter, JumpSound, Vector3D, double)[12];

    private int nextShot;

    // The echoes: a wail every WailSpacing s (each one about 3.3 s long, so 4 or 5 overlap: a constant stream), a roar
    // echo every EchoSpacing s; both rise over EchoRise s from the start of the pre-jump, then follow the roar's level
    private const double WailSpacingMin = 0.5, WailSpacingMax = 1.0;
    private const double EchoSpacingMin = 1.5, EchoSpacingMax = 4.0;
    private const double EchoRise = 1.8;

    // Where in the cloud they sound, as a share of its radius from its center: the wails close around the listener,
    // the echoes anywhere in it
    private const double WailSpread = 0.6, EchoSpread = 0.8;

    private static readonly Random Random = new Random();

    private double echoesFrom = double.NaN;
    private double nextWailAt, nextEchoAt;

    public GateSound()
    {
        for (var i = 0; i < shots.Length; i++)
            shots[i] = (new MyEntity3DSoundEmitter(null), default, Vector3D.Zero, double.NaN);
    }

    // The phases that start the echoes: once started, they go on to the end of the cloud, unless the jump is aborted
    private static bool Echoing(GatePhase phase) => phase == GatePhase.PreJump || phase == GatePhase.Jump || phase == GatePhase.Closing;

    private readonly List<(JumpSound Slot, double StartAt)> pending = new List<(JumpSound, double)>();

    public GatePhase Phase { get; private set; }

    /// <summary>Goes on in a phase another gate reached (its one-shot played there), echoing if it was</summary>
    public void Continue(GatePhase phase)
    {
        Phase = phase;
        if (Echoing(phase))
            echoesFrom = double.NegativeInfinity;
    }

    // The echoes' level: rising from the start of the pre-jump; a gate that was echoing already elsewhere (the exit, a
    // cloud taking over) echoes at once
    private float EchoLevel(double now) => double.IsNaN(echoesFrom) ? 0f : Cloud.SmoothStep((float)((now - echoesFrom) / EchoRise));

    /// <param name="now">Game time, seconds</param>
    /// <param name="phase">The gate's phase now; entering a phase plays its one-shot</param>
    /// <param name="position">The cloud's center</param>
    /// <param name="level">0..1, how loud the cruising roar is</param>
    /// <param name="nearness">0..1, 1 for a dense, big, close cloud (all near loop), 0 for a thin, small or distant one (all far loop)</param>
    /// <param name="gain">0..1, the cloud's loudness at the camera's distance, on everything it plays</param>
    /// <param name="radius">The cloud's radius, where its echoes sound</param>
    /// <param name="inside">0..1, how far the camera is inside the cloud: the wails are heard only there</param>
    public void Update(double now, GatePhase phase, Vector3D position, float level, float nearness, float gain, double radius, float inside)
    {
        if (Echoing(phase) && double.IsNaN(echoesFrom))
            echoesFrom = phase == GatePhase.PreJump && Phase != GatePhase.None ? now : double.NegativeInfinity;

        if (phase != Phase)
        {
            Phase = phase;

            // The abort tears the gate apart: what it was playing (the opening, a surge, the inhale) fades out at
            // once, and nothing scheduled starts any more
            if (phase == GatePhase.Abort)
            {
                pending.Clear();
                for (var i = 0; i < shots.Length; i++)
                {
                    if (shots[i].Emitter.IsPlaying)
                        shots[i].FadeFrom = now;
                }
            }

            var slot = OneShot(phase);
            if (slot.HasValue)
                PlayOnce(slot.Value, now);
        }

        for (var i = pending.Count - 1; i >= 0; i--)
        {
            var (slot, startAt) = pending[i];
            if (now < startAt)
                continue;

            if (Start(slot, position, Vector3D.Zero, JumpSounds.Volume(slot) * gain))
            {
                Log.Debug($"Sound {slot} at {position}: True, {now - startAt:0.000} s later");
                pending.RemoveAt(i);
            }
            else if (now > startAt + RetrySeconds)
            {
                Log.Debug($"Sound {slot} at {position}: out of hearing range");
                pending.RemoveAt(i);
            }
        }

        var echoLevel = level * EchoLevel(now);
        if (!double.IsNaN(echoesFrom) && phase != GatePhase.Abort)
        {
            if (now >= nextWailAt)
            {
                nextWailAt = now + Uniform(WailSpacingMin, WailSpacingMax);
                if (inside > 0f && Start(JumpSound.Wail, position, Scatter(radius * WailSpread), EchoVolume(JumpSound.Wail, gain, echoLevel, inside)))
                    JumpSounds.WailsPlayed++;
            }

            if (now >= nextEchoAt)
            {
                nextEchoAt = now + Uniform(EchoSpacingMin, EchoSpacingMax);
                if (Start(JumpSound.Echo, position, Scatter(radius * EchoSpread), EchoVolume(JumpSound.Echo, gain, echoLevel, inside)))
                    JumpSounds.EchoesPlayed++;
            }
        }

        // Relative to the camera: set again every frame as the camera moves
        for (var i = 0; i < shots.Length; i++)
        {
            var (emitter, slot, offset, fadeFrom) = shots[i];
            if (!emitter.IsPlaying)
                continue;

            var fade = double.IsNaN(fadeFrom) ? 1f : (float)(1 - (now - fadeFrom) / AbortFadeSeconds);
            if (fade <= 0f)
            {
                emitter.StopSound(true);
                continue;
            }

            emitter.SetPosition(position + offset);
            emitter.VolumeMultiplier = (JumpSounds.IsEcho(slot) ? EchoVolume(slot, gain, echoLevel, inside) : JumpSounds.Volume(slot) * gain) * fade;
        }

        // Two uncorrelated loops: an equal-power crossfade keeps the loudness through the middle
        var cruising = JumpSounds.Volume(JumpSound.Cruising) * level * gain;
        Loop(near, JumpSound.Cruising, position, cruising * (float)Math.Sqrt(nearness));
        Loop(far, JumpSound.CruisingFar, position, cruising * (float)Math.Sqrt(1f - nearness));
    }

    private static JumpSound? OneShot(GatePhase phase)
    {
        switch (phase)
        {
            case GatePhase.Opening:
                return JumpSound.Opening;
            case GatePhase.Jump:
                return JumpSound.Jump;
            case GatePhase.Abort:
                return JumpSound.Abort;
            default:
                return null;
        }
    }

    /// <summary>Plays a one-shot of the cloud once, from startAt (game time) on: tried again until the camera is in range</summary>
    public void PlayOnce(JumpSound slot, double startAt)
    {
        if (JumpSounds.Has(slot))
            pending.Add((slot, startAt));
    }

    // At its volume, on a free emitter, else on the one started longest ago
    private bool Start(JumpSound slot, Vector3D position, Vector3D offset, float volume)
    {
        var pair = JumpSounds.PairOf(slot);
        if (pair == null || volume < Inaudible)
            return false;

        var index = nextShot;
        for (var i = 0; i < shots.Length; i++)
        {
            var candidate = (nextShot + i) % shots.Length;
            if (!shots[candidate].Emitter.IsPlaying)
            {
                index = candidate;
                break;
            }
        }

        var emitter = shots[index].Emitter;
        emitter.SetPosition(position + offset);
        emitter.VolumeMultiplier = volume;
        JumpSounds.Play(emitter, pair);
        if (!emitter.IsPlaying)
            return false;

        shots[index] = (emitter, slot, offset, double.NaN);
        nextShot = (index + 1) % shots.Length;
        return true;
    }

    // Quieter than this, a loop stops and an echo is not started
    private const float Inaudible = 0.002f;

    private static float EchoVolume(JumpSound slot, float gain, float echoLevel, float inside) =>
        JumpSounds.Volume(slot) * gain * echoLevel * (slot == JumpSound.Wail ? inside : 1f);

    private static double Uniform(double min, double max) => min + (max - min) * Random.NextDouble();

    // A point in a ball of this radius, at random
    private static Vector3D Scatter(double radius)
    {
        Vector3D point;
        do
        {
            point = new Vector3D(Random.NextDouble() * 2 - 1, Random.NextDouble() * 2 - 1, Random.NextDouble() * 2 - 1);
        } while (point.LengthSquared() > 1);

        return point * radius;
    }

    private static void Loop(MyEntity3DSoundEmitter emitter, JumpSound slot, Vector3D position, float volume)
    {
        var pair = JumpSounds.PairOf(slot);
        if (pair == null || volume < Inaudible)
        {
            if (emitter.IsPlaying)
                emitter.StopSound(true);
            return;
        }

        emitter.SetPosition(position);
        emitter.VolumeMultiplier = volume;

        // Not started (or stopped by the game) while out of hearing range: tried again as the listener comes closer
        if (!emitter.IsPlaying)
            JumpSounds.Play(emitter, pair, skipIntro: true);
    }

    /// <summary>The cloud is gone: its loops stop; a one-shot still sounding plays out where it was</summary>
    public void Stop(Vector3D position)
    {
        near.StopSound(true);
        far.StopSound(true);
        pending.Clear();
        foreach (var (emitter, _, offset, _) in shots)
            JumpSounds.PlayOut(emitter, position + offset);
    }

    /// <summary>The session ends: everything stops</summary>
    public void Kill()
    {
        Stop(Vector3D.Zero);
        foreach (var (emitter, _, _, _) in shots)
            emitter.StopSound(true);
    }
}
