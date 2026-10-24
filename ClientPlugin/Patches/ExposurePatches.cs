using ClientPlugin.Effects;
using VRageRender;

namespace ClientPlugin.Patches;

/// <summary>
/// Holds the eye adaptation in the void. As the sun goes out (<see cref="JumpEffects.VoidDarkness"/>) the exposure would
/// rise until the ship's lamps looked like daylight; instead it adapts the slower the darker the void is, and not at all
/// once the sun is gone, so the scene gets as dark as losing the sun makes it. The exposure it held is the sunlit one
/// the ship comes back to at the exit. Applied to what the render thread receives (see <see cref="RenderPatches"/>).
/// </summary>
public static class ExposurePatches
{
    /// <summary>The post-processing settings MyGuiScreenGamePlay.Draw sends every frame, the message's own copy</summary>
    internal static void Hold(ref MyPostprocessSettings settings)
    {
        var hold = JumpEffects.VoidDarkness;
        if (hold <= 0f)
            return;

        // The shader moves the exposure towards the scene's by 1 - 2^(-dt * speed) a frame: no speed, no change (the
        // game's own way to freeze it, MyDX11Render)
        settings.Data.EyeAdaptationSpeedUp *= 1f - hold;
        settings.Data.EyeAdaptationSpeedDown *= 1f - hold;
        if (Log.DebugEnabled && JumpEffects.Now - lastLogAt >= 0.5)
        {
            lastLogAt = JumpEffects.Now;
            Log.Debug($"Exposure: adapting at x{1f - hold:0.00} of its speed ({settings.Data.EyeAdaptationSpeedUp:0.00} up, {settings.Data.EyeAdaptationSpeedDown:0.00} down)");
        }
    }

    private static double lastLogAt;
}
