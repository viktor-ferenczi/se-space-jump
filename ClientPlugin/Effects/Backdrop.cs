using System;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Effects;

/// <summary>
/// What a camera inside the smoke sees behind it: nothing. The smoke billboards are soft and wispy, and however many of
/// them overlap, a little of the planets, asteroids and sky behind them gets through, which the game's eye adaptation
/// then brightens in the dark view until it shows. So behind the smoke the view is closed by one opaque billboard of the
/// smoke's own color, facing the camera and filling the view. It is depth tested like the smoke: everything nearer than
/// it (the ship, the smoke in front, the lightning) stays as it is.
/// </summary>
public static class Backdrop
{
    private static MyStringId? material;

    // A white texture, opaque all over
    private static MyStringId Material => material ??= UnlitMaterials.Get("Square");

    // At a density this far below full the view is already closed: only the smoke's build-up and break-up are translucent
    private const float OpaqueFrom = 0.85f;

    /// <summary>The backdrop's opacity for a smoke density of 0..1</summary>
    public static float Opacity(float density) => MathHelper.Clamp(density / OpaqueFrom, 0f, 1f);

    /// <param name="distance">How far in front of the camera, along its view direction</param>
    /// <param name="opacity">0..1, 1 hides everything behind it</param>
    public static void Draw(double distance, float opacity)
    {
        var view = MySector.MainCamera;
        if (view == null || opacity < 0.003f)
            return;

        var matrix = view.WorldMatrix;
        var halfHeight = (float)Math.Tan(view.FieldOfView * 0.5);
        var halfWidth = halfHeight * Math.Max(1f, view.AspectRatio);
        var halfDiagonal = (float)Math.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight);
        var alpha = Math.Min(opacity, 1f);
        MyTransparentGeometry.AddBillboardOriented(Material, new Vector4(Cloud.SmokeColor * alpha, alpha), matrix.Translation + matrix.Forward * distance,
            matrix.Left, matrix.Up, (float)distance * halfDiagonal * 1.2f);
    }
}
