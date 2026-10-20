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

    public float Brightness(double now)
    {
        var age = (float)(now - StartsAt);
        var left = (float)(EndsAt - now);
        if (age < 0 || left < 0)
            return 0f;

        var envelope = Math.Min(1f, age / 0.08f) * Math.Min(1f, left / (float)(EndsAt - StartsAt) * 1.5f);
        var flicker = 0.7f + 0.3f * (float)Math.Sin(age * 23f + Phase);
        return Peak * envelope * flicker;
    }
}
