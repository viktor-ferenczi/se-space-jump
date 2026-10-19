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

    /// <summary>The gate inhales right before the jump (<see cref="JumpSound.PreJump"/> once, ending at the jump)</summary>
    PreJump,

    /// <summary>The jump (<see cref="JumpSound.Jump"/> once), at the cloud left behind and at the exit</summary>
    Jump,

    /// <summary>The cloud lingers and fades (<see cref="JumpSound.Closing"/> once, the roar fading out)</summary>
    Closing,

    /// <summary>The jump failed, the cloud is torn apart (<see cref="JumpSound.Abort"/> once, the roar dying fast)</summary>
    Abort,
}

/// <summary>
/// The sound of one cloud's gate, heard by everyone near it, pilots and observers alike. A state machine over the
/// gate's phases: entering a phase plays its one-shot slot at the cloud; under them the cruising roar loops at the
/// cloud's center, crossfaded with equal power between its near version (a dense, big, close cloud) and its far one
/// (no highs: a thin, small or distant cloud) and as loud as the cloud's state makes it. Loudness and character change
/// smoothly, as the cloud does: the sound never drops off at once.
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

    // One-shots overlap: the jump, the bloom, the closing and a surge may all sound at once. FadeFrom is when the
    // abort started fading the shot out, NaN while it plays on.
    private readonly (MyEntity3DSoundEmitter Emitter, JumpSound Slot, double FadeFrom)[] shots =
    {
        (new MyEntity3DSoundEmitter(null), default, double.NaN),
        (new MyEntity3DSoundEmitter(null), default, double.NaN),
        (new MyEntity3DSoundEmitter(null), default, double.NaN),
        (new MyEntity3DSoundEmitter(null), default, double.NaN),
    };

    private int nextShot;

    private readonly List<(JumpSound Slot, double StartAt)> pending = new List<(JumpSound, double)>();

    public GatePhase Phase { get; private set; }

    /// <summary>Goes on in a phase another gate reached (its one-shot played there)</summary>
    public void Continue(GatePhase phase) => Phase = phase;

    /// <param name="now">Game time, seconds</param>
    /// <param name="phase">The gate's phase now; entering a phase plays its one-shot</param>
    /// <param name="position">The cloud's center</param>
    /// <param name="level">0..1, how loud the cruising roar is</param>
    /// <param name="nearness">0..1, 1 for a dense, big, close cloud (all near loop), 0 for a thin, small or distant one (all far loop)</param>
    /// <param name="gain">0..1, the cloud's loudness at the camera's distance, on everything it plays</param>
    public void Update(double now, GatePhase phase, Vector3D position, float level, float nearness, float gain)
    {
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

            if (Start(slot, position, gain))
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

        // Relative to the camera: set again every frame as the camera moves
        for (var i = 0; i < shots.Length; i++)
        {
            var (emitter, slot, fadeFrom) = shots[i];
            if (!emitter.IsPlaying)
                continue;

            var fade = double.IsNaN(fadeFrom) ? 1f : (float)(1 - (now - fadeFrom) / AbortFadeSeconds);
            if (fade <= 0f)
            {
                emitter.StopSound(true);
                continue;
            }

            emitter.SetPosition(position);
            emitter.VolumeMultiplier = JumpSounds.Volume(slot) * gain * fade;
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
            case GatePhase.PreJump:
                return JumpSound.PreJump;
            case GatePhase.Jump:
                return JumpSound.Jump;
            case GatePhase.Closing:
                return JumpSound.Closing;
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

    private bool Start(JumpSound slot, Vector3D position, float gain)
    {
        var emitter = shots[nextShot].Emitter;
        emitter.SetPosition(position);
        emitter.VolumeMultiplier = JumpSounds.Volume(slot) * gain;
        JumpSounds.Play(emitter, JumpSounds.PairOf(slot));
        if (!emitter.IsPlaying)
            return false;

        shots[nextShot].Slot = slot;
        shots[nextShot].FadeFrom = double.NaN;
        nextShot = (nextShot + 1) % shots.Length;
        return true;
    }

    private static void Loop(MyEntity3DSoundEmitter emitter, JumpSound slot, Vector3D position, float volume)
    {
        var pair = JumpSounds.PairOf(slot);
        if (pair == null || volume < 0.002f)
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
        foreach (var (emitter, _, _) in shots)
            JumpSounds.PlayOut(emitter, position);
    }

    /// <summary>The session ends: everything stops</summary>
    public void Kill()
    {
        Stop(Vector3D.Zero);
        foreach (var (emitter, _, _) in shots)
            emitter.StopSound(true);
    }
}
