using System;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.World;
using VRageMath;

namespace ClientPlugin.Effects;

/// <summary>
/// The light on the smoke seen from outside. Black smoke in sunlight is not black: the sun lights the fringe of the
/// cloud a very dark grey, more on its sunny side and most where the camera looks at it towards the sun (smoke scatters
/// light mostly forwards: the bright edge of a cloud in front of the sun), while the inside stays black. Drawn all black, a cloud showed only
/// where something brighter was behind it, so it read as thick against the skybox's nebulae and hardly at all against
/// black space in a sunlit scene. Only the fringe is lit: the cloud's black middle, where the opaque core in front of
/// the ship is, matches the core. The billboards stay unlit materials (the lightning's glow in them must not be
/// shadowed), so the light is worked out here: the sun, unless a planet is in the way. Seen from inside (a camera in
/// the smoke) the smoke stays black: a view filled evenly with a lit grey is lifted to a bright grey by the eye
/// adaptation.
/// </summary>
public static class SmokeLight
{
    // How much of the sun's light the smoke sends back towards the camera: a very dark grey
    private const float SunlitLevel = 0.005f;

    // On the far side of a cloud from the sun this much of the sunlit side's light is left. Not much darker: every puff
    // would show its round outline against its neighbours
    private const float ShadeShare = 0.45f;

    // Forward scattering: towards the sun the fringe is up to ForwardGain times brighter, ForwardPower sharpens the lobe
    private const float ForwardGain = 2f;
    private const float ForwardPower = 4f;

    // The lit fringe: from this far out (in portal radii) the smoke is lit, fully from FringeFull on
    private const float FringeFrom = 0.7f;
    private const float FringeFull = 1.05f;

    // The planet's shadow edge, as a share of its radius
    private const double PenumbraShare = 0.03;

    /// <summary>The colour of smoke in the shade, lit by nothing (premultiplied by alpha when drawn)</summary>
    public static Vector3 Shade => Cloud.SmokeColor;

    private static Vector3D sunDirection = Vector3D.Up;
    private static float sunIntensity;

    /// <summary>Once per frame, before any smoke is drawn</summary>
    public static void Update()
    {
        var sun = MySector.SunProperties;
        sunDirection = sun.SunDirectionNormalized;
        sunIntensity = sun.SunIntensity;
    }

    public static Vector3D SunDirection => sunDirection;

    /// <summary>0..1, how much of the sun reaches this place: 0 in a planet's shadow</summary>
    public static float Sunlight(Vector3D position)
    {
        var light = 1f;
        foreach (var planet in MyPlanets.GetPlanets())
        {
            var toPlanet = planet.PositionComp.GetPosition() - position;
            var along = Vector3D.Dot(toPlanet, sunDirection);
            if (along <= 0)
                continue;

            var radius = (double)planet.AverageRadius;
            var off = Math.Sqrt(Math.Max(toPlanet.LengthSquared() - along * along, 0.0));
            var x = MathHelper.Clamp((float)((off - radius * (1 - PenumbraShare)) / (radius * 2 * PenumbraShare)), 0f, 1f);
            light = Math.Min(light, x * x * (3f - 2f * x));
        }

        return light * sunIntensity;
    }

    /// <summary>
    /// The smoke's colour at a place in a cloud (premultiplied by alpha when drawn)
    /// </summary>
    /// <param name="sunlight">0..1, <see cref="Sunlight"/> at the cloud</param>
    /// <param name="outward">The place from the cloud's middle, in portal radii</param>
    /// <param name="view">The direction from the camera to the place, normalized</param>
    public static Vector3 Color(float sunlight, Vector3D outward, Vector3D view)
    {
        if (sunlight <= 0f)
            return Shade;

        var rim = outward.Length();
        var fringe = SmoothStep((float)((rim - FringeFrom) / (FringeFull - FringeFrom)));
        if (fringe <= 0f)
            return Shade;

        var facing = rim > 1e-3 ? (float)(Vector3D.Dot(outward, sunDirection) / rim) : 0f;
        var side = ShadeShare + (1f - ShadeShare) * SmoothStep((facing + 0.6f) / 1.4f);
        var forward = (float)Math.Pow(Math.Max(Vector3D.Dot(view, sunDirection), 0.0), ForwardPower);
        return Shade + new Vector3(SunlitLevel * sunlight * fringe * side * (1f + ForwardGain * forward));
    }

    private static float SmoothStep(float x)
    {
        x = MathHelper.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }
}
