using System;
using VRageMath;

namespace ClientPlugin.Effects;

/// <summary>The configured lightning and flash colors as linear tints for the billboards, brightest channel 1</summary>
public static class Palette
{
    private static Color lightningSource, flashSource;
    private static Vector3 lightning, flash;

    public static Vector3 Lightning
    {
        get
        {
            Update(Config.Current.LightningColor, ref lightningSource, ref lightning);
            return lightning;
        }
    }

    public static Vector3 Flash
    {
        get
        {
            Update(Config.Current.FlashColor, ref flashSource, ref flash);
            return flash;
        }
    }

    private static void Update(Color color, ref Color source, ref Vector3 tint)
    {
        if (color == source && tint != Vector3.Zero)
            return;

        source = color;
        var linear = new Vector3(Linear(color.R), Linear(color.G), Linear(color.B));
        var max = Math.Max(linear.X, Math.Max(linear.Y, linear.Z));
        tint = max > 1e-4f ? linear / max : Vector3.Zero;
    }

    private static float Linear(byte channel) => (float)Math.Pow(channel / 255.0, 2.2);
}
