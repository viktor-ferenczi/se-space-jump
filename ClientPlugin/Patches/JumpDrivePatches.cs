using System;
using System.Diagnostics.CodeAnalysis;
using ClientPlugin.Effects;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRageMath;

namespace ClientPlugin.Patches;

/// <summary>
/// Replaces the vanilla jump drive visuals (the JumpParticleEffect "Warp" particle and the 170 degree FOV warp)
/// with the black smoke clouds, and reports the jump's phases to <see cref="JumpEffects"/>.
/// The jump logic itself and its sounds stay vanilla.
/// </summary>
[HarmonyPatch(typeof(MyGridJumpDriveSystem))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class JumpDrivePatches
{
    private static bool Enabled => Config.Current.Enabled && !Sandbox.Engine.Platform.Game.IsDedicated;

    // Countdown started, on the server and on every client that has the grid
    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyGridJumpDriveSystem.Jump))]
    public static void JumpPostfix(MyGridJumpDriveSystem __instance)
    {
        if (!Enabled)
            return;

        Guard(() => JumpEffects.OnCountdown(__instance, __instance.m_jumpTimeLeft));
    }

    // Called on every tick of the countdown while the vanilla effect is not playing, which is always once it is replaced:
    // keeps the countdown in sync and starts the cloud of a jump this client did not see begin (joined during the countdown)
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyGridJumpDriveSystem.PlayParticleEffect))]
    public static bool PlayParticleEffectPrefix(MyGridJumpDriveSystem __instance)
    {
        if (!Enabled)
            return true;

        if (!__instance.m_jumped)
            Guard(() => JumpEffects.OnCountdown(__instance, __instance.m_jumpTimeLeft));

        return false;
    }

    // The vanilla effect widens the FOV to 170 degrees around the jump
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyGridJumpDriveSystem.UpdateJumpEffect))]
    public static bool UpdateJumpEffectPrefix()
    {
        return !Enabled;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyGridJumpDriveSystem.PerformJump))]
    public static void PerformJumpPrefix(MyGridJumpDriveSystem __instance, out JumpState __state)
    {
        var grid = __instance.Grid;
        __state = new JumpState
        {
            WorldMatrix = grid.WorldMatrix,
            Velocity = grid.Physics?.LinearVelocity ?? Vector3.Zero,
        };
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyGridJumpDriveSystem.PerformJump))]
    public static void PerformJumpPostfix(MyGridJumpDriveSystem __instance, JumpState __state)
    {
        if (!Enabled)
            return;

        Guard(() => JumpEffects.OnJumped(__instance, __state.WorldMatrix, __state.Velocity));
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyGridJumpDriveSystem.AbortJump))]
    public static void AbortJumpPrefix(MyGridJumpDriveSystem __instance)
    {
        if (!Enabled)
            return;

        Guard(() => JumpEffects.OnAborted(__instance));

        // The pilots see the gate fail instead of the reason (the vanilla notification is held back meanwhile)
        if (__instance.IsJumping && __instance.IsLocalCharacterAffectedByJump())
            Guard(NotificationPatches.ShowJumpAborted);
        NotificationPatches.Aborting = true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyGridJumpDriveSystem.AbortJump))]
    public static void AbortJumpPostfix()
    {
        NotificationPatches.Aborting = false;
    }

    // Static broadcast event: reaches every client, also those that do not have the jumping grid (yet)
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyGridJumpDriveSystem.OnPerformJump))]
    public static void OnPerformJumpPrefix(long entityId, Vector3D jumpTarget)
    {
        if (!Enabled)
            return;

        if (MyEntities.TryGetEntityById(entityId, out MyCubeGrid _))
            return;

        Guard(() => JumpEffects.OnRemoteJump(entityId, jumpTarget));
    }

    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
    }

    public struct JumpState
    {
        public MatrixD WorldMatrix;
        public Vector3 Velocity;
    }
}

[HarmonyPatch(typeof(MySession))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
public static class SessionPatches
{
    // Same place mods draw their billboards from (MySessionComponentBase.Draw of mod components)
    [HarmonyPostfix]
    [HarmonyPatch(nameof(MySession.DrawSync))]
    public static void DrawSyncPostfix()
    {
        try
        {
            JumpEffects.Draw();
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MySession.Unload))]
    public static void UnloadPrefix()
    {
        try
        {
            JumpEffects.Reset();
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
    }
}
