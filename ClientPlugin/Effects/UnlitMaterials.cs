using VRage.Utils;
using VRageRender;

namespace ClientPlugin.Effects;

/// <summary>
/// Unlit copies of the vanilla smoke materials. The vanilla ones are lit particles: the billboard shader multiplies their
/// color by the shadowed sunlight and ambient light at the billboard, which would turn the lit regions and the lightning
/// glow of the cloud black wherever the ship shadows it. The renderer looks materials up by id every frame, so copies
/// added once the definitions are loaded work like any other.
/// </summary>
public static class UnlitMaterials
{
    public static MyStringId Get(string source)
    {
        var id = MyStringId.GetOrCompute($"{Plugin.Name}_{source}");
        if (MyTransparentMaterials.ContainsMaterial(id))
            return id;

        if (!MyTransparentMaterials.TryGetMaterial(MyStringId.GetOrCompute(source), out var m))
            return MyStringId.GetOrCompute(source);

        MyTransparentMaterials.AddMaterial(new MyTransparentMaterial(id, m.TextureType, m.Texture, m.GlossTexture, m.SoftParticleDistanceScale,
            false, m.AlphaMistingEnable, m.Color, m.ColorAdd, m.ShadowMultiplier, m.LightMultiplier, m.IsFlareOccluder, m.TriangleFaceCulling,
            m.UseAtlas, m.AlphaMistingStart, m.AlphaMistingEnd, m.AlphaSaturation, m.Reflectivity, m.AlphaCutout, m.TargetSize, m.Fresnel,
            m.ReflectionShadow, m.Gloss, m.GlossTextureAdd, m.SpecularColorFactor)
        {
            UVOffset = m.UVOffset,
            UVSize = m.UVSize,
        });
        Log.Debug($"Unlit material {id} from {source}");
        return id;
    }
}
