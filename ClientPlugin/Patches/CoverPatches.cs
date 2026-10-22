using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using ClientPlugin.Effects;
using HarmonyLib;
using Sandbox.RenderDirect.ActorComponents;
using VRageMath;

namespace ClientPlugin.Patches;

/// <summary>
/// The glares of lights and thrusters, drawn on top of everything once the transparent scene is resolved, fade where
/// the smoke hides them (<see cref="Cover"/>). Render thread.
/// </summary>
[HarmonyPatch]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class GlarePatch
{
    // MyFlareRenderer.Draw(FlareId, Vector3D position, Vector3D forward, Vector4 colorFactor, float intensityMultiplier)
    public static MethodBase TargetMethod() =>
        AccessTools.GetDeclaredMethods(AccessTools.TypeByName("VRageRender.MyFlareRenderer")).First(m => m.Name == "Draw" && m.GetParameters().Length == 5);

    public static void Prefix(Vector3D position, ref float intensityMultiplier)
    {
        if (Cover.Active)
            intensityMultiplier *= Cover.Keep(position);
    }
}

/// <summary>
/// A thruster's flame billboards (and its light) fade where the smoke hides them (<see cref="Cover"/>): the renderer
/// would average them into the smoke in front, however dense. Render thread.
/// </summary>
[HarmonyPatch(typeof(MyThrustFlameAnimator), nameof(MyThrustFlameAnimator.OnUpdateBeforeDraw))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class FlamePatch
{
    public static void Prefix(MyThrustFlameAnimator __instance, ref Vector4 ___m_currentColor, out Vector4 __state)
    {
        __state = ___m_currentColor;
        if (Cover.Active && __instance.Owner != null)
            ___m_currentColor *= Cover.Keep(__instance.Owner.WorldMatrix.Translation);
    }

    public static void Postfix(ref Vector4 ___m_currentColor, Vector4 __state)
    {
        ___m_currentColor = __state;
    }
}
