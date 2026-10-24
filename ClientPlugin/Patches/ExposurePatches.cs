using System.Diagnostics.CodeAnalysis;
using ClientPlugin.Effects;
using HarmonyLib;
using VRageRender;

namespace ClientPlugin.Patches;

/// <summary>
/// Holds the eye adaptation in the void. As the sun goes out (<see cref="JumpEffects.VoidDarkness"/>) the exposure would
/// rise until the ship's lamps looked like daylight; instead it adapts the slower the darker the void is, and not at all
/// once the sun is gone, so the scene gets as dark as losing the sun makes it. The exposure it held is the sunlit one
/// the ship comes back to at the exit.
/// </summary>
[HarmonyPatch]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class ExposurePatches
{
    // Sent every frame by MyGuiScreenGamePlay.Draw, by reference to the game's own settings (MyPostprocessSettingsWrapper):
    // the message takes a copy, the settings are put back right after
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MyRenderProxy), nameof(MyRenderProxy.SwitchPostprocessSettings))]
    public static void SwitchPostprocessSettingsPrefix(ref MyPostprocessSettings settings, out (float Up, float Down)? __state)
    {
        __state = null;
        var hold = JumpEffects.VoidDarkness;
        if (hold <= 0f)
            return;

        // The shader moves the exposure towards the scene's by 1 - 2^(-dt * speed) a frame: no speed, no change (the
        // game's own way to freeze it, MyDX11Render)
        __state = (settings.Data.EyeAdaptationSpeedUp, settings.Data.EyeAdaptationSpeedDown);
        settings.Data.EyeAdaptationSpeedUp *= 1f - hold;
        settings.Data.EyeAdaptationSpeedDown *= 1f - hold;
        if (Log.DebugEnabled && JumpEffects.Now - lastLogAt >= 0.5)
        {
            lastLogAt = JumpEffects.Now;
            Log.Debug($"Exposure: adapting at x{1f - hold:0.00} of its speed ({settings.Data.EyeAdaptationSpeedUp:0.00} up, {settings.Data.EyeAdaptationSpeedDown:0.00} down)");
        }
    }

    private static double lastLogAt;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MyRenderProxy), nameof(MyRenderProxy.SwitchPostprocessSettings))]
    public static void SwitchPostprocessSettingsPostfix(ref MyPostprocessSettings settings, (float Up, float Down)? __state)
    {
        if (__state is not var (up, down))
            return;

        settings.Data.EyeAdaptationSpeedUp = up;
        settings.Data.EyeAdaptationSpeedDown = down;
    }
}
