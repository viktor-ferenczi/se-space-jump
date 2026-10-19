using System.Diagnostics.CodeAnalysis;
using ClientPlugin.Effects;
using HarmonyLib;
using Sandbox.Game.World;
using VRageRender;
using VRageRender.Messages;

namespace ClientPlugin.Patches;

/// <summary>
/// Dims the sun behind a cloud. The sun disc is drawn into the sky in the lighting pass, so bright that the smoke
/// billboards drawn over it would let it shine through; its intensity and the sun's glare follow
/// <see cref="JumpEffects.SunVisibility"/> instead.
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
    }

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

    // The sun disc is so bright that a tenth of it still saturates the screen: it only looks dimmed when far less is left
    private static float Brightness(float visibility) => visibility < 0.05f ? 0f : visibility * visibility * visibility;
}
