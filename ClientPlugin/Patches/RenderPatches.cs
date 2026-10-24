using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;
using VRageRender.Messages;

namespace ClientPlugin.Patches;

/// <summary>
/// The sun's light, its disc and glare and the eye adaptation, changed as the render thread takes in the game's messages
/// (MyRender11.ProcessMessageInternal), each message's own copy. Not on the MyRenderProxy methods that send them: those
/// are small, and once another plugin patches their caller (Night Vision patches MyGuiScreenGamePlay.Draw), Harmony
/// compiles a new copy of the caller, and a patch on the methods it calls stopped running. On Viktor's client the sun
/// stayed on in the void and the log had no "In the void" line; with Night Vision on, so did ours (#1435)
/// </summary>
[HarmonyPatch]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
public static class RenderPatches
{
    public static MethodBase TargetMethod() => AccessTools.Method("VRageRender.MyRender11:ProcessMessageInternal");

    public static void Prefix(MyRenderMessageBase message)
    {
        switch (message)
        {
            case MyRenderMessageUpdateRenderEnvironment environment:
                SunPatches.DimEnvironment(ref environment.Data);
                break;
            case MyRenderMessageUpdateRenderLight light:
                SunPatches.DimGlare(ref light.Data);
                break;
            case MyRenderMessageUpdatePostprocessSettings postprocess:
                ExposurePatches.Hold(ref postprocess.Settings);
                break;
        }
    }
}
