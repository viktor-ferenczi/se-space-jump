using System;
using System.IO;
using VRage.Utils;
using VRageRender;

namespace ClientPlugin.Effects;

/// <summary>
/// A round billboard material, opaque in its middle (out to <see cref="OpaqueRadius"/> of its radius) and fading to
/// nothing at its rim, unlit. The game has none: its texture is written once into the temp folder as an uncompressed
/// DDS and a material added for it.
/// </summary>
public static class SoftDisc
{
    public const float OpaqueRadius = 0.85f;

    private const int Size = 128;

    private static MyStringId? material;

    public static MyStringId Material => material ??= Create();

    private static MyStringId Create()
    {
        var id = MyStringId.GetOrCompute($"{Plugin.Name}_SoftDisc");
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), Plugin.Name);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "SoftDisc.dds");
            File.WriteAllBytes(path, Texture());
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

    // White, alpha 1 out to OpaqueRadius, a smooth step down to 0 at the rim; 32 bit BGRA, one mip level
    private static byte[] Texture()
    {
        var data = new byte[128 + Size * Size * 4];
        void Int(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(data, offset);
        Int(0, 0x20534444); // "DDS "
        Int(4, 124);
        Int(8, 0x100F); // caps, height, width, pitch, pixel format
        Int(12, Size);
        Int(16, Size);
        Int(20, Size * 4);
        Int(76, 32);
        Int(80, 0x41); // RGB with alpha
        Int(88, 32);
        Int(92, 0x00FF0000);
        Int(96, 0x0000FF00);
        Int(100, 0x000000FF);
        Int(104, 0xFF000000);
        Int(108, 0x1000); // texture
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var dx = (x + 0.5f) / Size * 2f - 1f;
                var dy = (y + 0.5f) / Size * 2f - 1f;
                var t = Math.Clamp(((float)Math.Sqrt(dx * dx + dy * dy) - OpaqueRadius) / (1f - OpaqueRadius), 0f, 1f);
                var alpha = 1f - t * t * (3f - 2f * t);
                var o = 128 + (y * Size + x) * 4;
                data[o] = data[o + 1] = data[o + 2] = 255;
                data[o + 3] = (byte)Math.Round(alpha * 255f);
            }
        }

        return data;
    }
}
