using System;
using System.IO;
using VRage.Utils;
using VRageRender;

namespace ClientPlugin.Effects;

/// <summary>
/// A round billboard material, opaque in its middle (out to <see cref="OpaqueRadius"/> of its radius) and fading to
/// nothing at its rim, unlit. The game has none: its texture is the plugin's own asset (Assets/SoftDisc.dds, an
/// uncompressed DDS), a material is added for it.
/// </summary>
public static class SoftDisc
{
    public const float OpaqueRadius = 0.85f;

    private static MyStringId? material;

    public static MyStringId Material => material ??= Create();

    private static MyStringId Create()
    {
        var id = MyStringId.GetOrCompute($"{Plugin.Name}_SoftDisc");
        if (JumpSounds.AssetFolder == null)
        {
            Log.Info("Soft disc: no asset folder, the smoke's cover is not drawn");
            return id;
        }

        try
        {
            var path = Path.Combine(JumpSounds.AssetFolder, "SoftDisc.dds");
            MyTransparentMaterials.TryGetMaterial(MyStringId.GetOrCompute("Square"), out var m);
            MyTransparentMaterials.AddMaterial(new MyTransparentMaterial(id, m.TextureType, path, null, m.SoftParticleDistanceScale,
                false, false, m.Color, m.ColorAdd, m.ShadowMultiplier, m.LightMultiplier, false, m.TriangleFaceCulling,
                false, m.AlphaMistingStart, m.AlphaMistingEnd, m.AlphaSaturation, m.Reflectivity, false, m.TargetSize, m.Fresnel,
                m.ReflectionShadow, m.Gloss, m.GlossTextureAdd, m.SpecularColorFactor));
            Log.Debug($"Soft disc material {id}: {path}");
        }
        catch (Exception e)
        {
            Log.Error(e);
        }

        return id;
    }
}
