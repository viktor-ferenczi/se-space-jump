using System;
using VRageMath;

namespace ClientPlugin.Effects;

/// <summary>A region of smoke lit from inside for a moment, flickering: a discharge deep in the fog</summary>
internal sealed class FlashPatch
{
    // In the frame of its cloud: an origin cloud's patch moves with the ship, a camera cloud's with the camera
    public Vector3D Local;
    public float Radius;
    public float Peak;
    public float Phase;
    public double StartsAt;
    public double EndsAt;

    // A flash swells and fades over about a second (FadeFrom..FadeTo s, its peak in the middle), as bright as PeakFrom..1
    // of its cloud's lightning: the deep discharges are a slow glow in the smoke, the strikes are the bolts
    private const double FadeFrom = 0.8;
    private const double FadeTo = 1.6;
    private const float PeakFrom = 0.3f;

    /// <summary>A bolt's flash peaks at this times PeakShare of the bolt's brightness</summary>
    public const float BoltShare = 0.4f;

    /// <summary>How long a new flash lasts (s), at random</summary>
    public static double Duration(Random random) => FadeFrom + (FadeTo - FadeFrom) * random.NextDouble();

    /// <summary>The share of its cloud's lightning a new flash peaks at, at random</summary>
    public static float PeakShare(Random random) => PeakFrom + (1f - PeakFrom) * (float)(random.NextDouble() * random.NextDouble() + random.NextDouble()) / 2f;

    public float Brightness(double now)
    {
        var age = (float)(now - StartsAt);
        var left = (float)(EndsAt - now);
        if (age < 0 || left < 0)
            return 0f;

        // Up and down as sin^2 of its life, no edge at either end; a slow, faint flicker on it
        var envelope = (float)Math.Sin(Math.PI * age / (EndsAt - StartsAt));
        var flicker = 0.9f + 0.1f * (float)Math.Sin(age * 9f + Phase);
        return Peak * envelope * envelope * flicker;
    }
}
