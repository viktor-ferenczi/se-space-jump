using System.Diagnostics.CodeAnalysis;
using ClientPlugin.Effects;
using HarmonyLib;
using Sandbox.Game.World;
using VRageRender;
using VRageMath;
using VRageRender.Messages;

namespace ClientPlugin.Patches;

/// <summary>
/// Dims the sun behind a cloud. The sun disc is drawn into the sky in the lighting pass, so bright that the smoke
/// billboards drawn over it would let it shine through; its intensity and the sun's glare follow
/// <see cref="JumpEffects.SunVisibility"/> instead. In the void with a jumping ship the sun and the sky stop lighting
/// the scene (<see cref="JumpEffects.VoidDarkness"/>).
/// </summary>
[HarmonyPatch]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class SunPatches
{
    // The sun glare's brightness last sent to the render
    private static float glareBrightness = 1f;

    // Sent every frame by MyGuiScreenGamePlay.Draw from MySector.SunProperties
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MyRenderProxy), nameof(MyRenderProxy.UpdateRenderEnvironment))]
    public static void UpdateRenderEnvironmentPrefix(ref MyEnvironmentData data)
    {
        var visibility = JumpEffects.SunVisibility;
        if (visibility < 1f)
        {
            // The disc and the halo around it
            var brightness = Brightness(visibility);
            data.EnvironmentLight.SunDiscIntensity *= brightness;
            data.EnvironmentLight.SunDiscColor *= brightness;
            data.EnvironmentLight.SunDiscColor2 *= brightness;
        }

        // In the void with the jumping ship neither the sun nor the sky lights it: the discharges do (FlashLights), and
        // its own lights
        var dark = JumpEffects.VoidDarkness;
        if (dark > 0f)
        {
            if (Log.DebugEnabled && JumpEffects.Now - lastVoidLogAt >= 0.5)
            {
                lastVoidLogAt = JumpEffects.Now;
                Log.Debug($"Void light: darkness {dark:0.00}, sun colour x{1f - dark:0.00} of {data.EnvironmentLight.SunColorRaw.X:0.0}");
            }

            var sun = 1f - dark;
            var sky = 1f - dark * (1f - VoidAmbient);
            // The shaders light with the sun's colour (directionalLightColor), its factors scale only parts of it; its
            // shadows only gate that light. The sky lights the ship from the environment map: through the ambient
            // factors, and in the reflections of glass and shields through the map's own skybox and atmosphere
            data.EnvironmentLight.SunColorRaw *= sun;
            data.EnvironmentLight.SunSpecularColorRaw *= sun;
            data.EnvironmentLight.AmbientDiffuseFactor *= sky;
            data.EnvironmentLight.AmbientSpecularFactor *= sky;
            data.EnvironmentLight.EnvSkyboxBrightness *= sky;
            data.EnvironmentLight.EnvAtmosphereBrightness *= sky;

            // Once each time the ship is all in the void, in every build: the game's log shows that the sun went out
            if (dark >= 0.995f && !voidLogged)
            {
                voidLogged = true;
                Log.Info($"In the void: sunlight on the ship x{sun:0.00}, sky light x{sky:0.00}, eye adaptation at x{sun:0.00} of its speed");
            }
        }

        if (dark < 0.005f)
            voidLogged = false;
    }

    private static double lastVoidLogAt;
    private static bool voidLogged;

    // The share of the sky's ambient light left in the void: none, the discharges light the ship there
    private const float VoidAmbient = 0f;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MySector), nameof(MySector.UpdateSunLight))]
    public static void UpdateSunLightPrefix()
    {
        // The glare is sent to the render only when it changed: a new brightness has to be sent
        var light = MySector.m_sunFlare;
        if (light != null && Brightness(JumpEffects.SunVisibility) != glareBrightness)
            light.MarkPositionDirty();
    }

    // The glare stays enabled and only its intensity drops, down to zero, which the render skips. A disabled glare
    // (GlareOn off, or an intensity below 0.01) is never removed from the render: the sun's last faint glare and
    // its streaks would stay on the screen.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MyRenderProxy), nameof(MyRenderProxy.UpdateRenderLight))]
    public static void UpdateRenderLightPrefix(ref UpdateRenderLightData data)
    {
        var light = MySector.m_sunFlare;
        if (light == null || data.ID != light.RenderObjectID)
            return;

        glareBrightness = Brightness(JumpEffects.SunVisibility);
        data.Glare.Intensity *= glareBrightness;
    }

    // The sun disc is so bright that a tenth of it still saturates the screen: it only looks dimmed when far less is left.
    // It goes out smoothly below a fifth: cut off at once, everything it lights went dark in one frame. The sun's light
    // on a ship in the void follows the same curve (JumpEffects.VoidDarkness): it is gone when the disc is
    internal static float Brightness(float visibility)
    {
        var x = MathHelper.Clamp((visibility - 0.05f) / 0.15f, 0f, 1f);
        return visibility * visibility * visibility * x * x * (3f - 2f * x);
    }
}
