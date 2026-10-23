using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Game.Localization;
using VRage.Utils;

namespace ClientPlugin.Patches;

/// <summary>
/// The jump's HUD message names what the gate is doing instead of the vanilla "Ship will jump in N seconds":
/// Manifesting Gate (N s), In Transit (N s), Jump Complete (see JumpEffects.JumpMessage). An aborted jump shows "JUMP ABORTED!"
/// instead of the reason.
/// </summary>
[HarmonyPatch(typeof(MyHudNotifications), nameof(MyHudNotifications.Add), typeof(MyHudNotificationBase))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
public static class NotificationPatches
{
    /// <summary>Set while the vanilla abort runs: its notification is not shown</summary>
    public static bool Aborting;

    private static readonly MyStringId[] AbortTexts =
    {
        MySpaceTexts.NotificationJumpAborted,
        MySpaceTexts.NotificationJumpAbortedStatic,
        MySpaceTexts.NotificationJumpAbortedLocked,
        MySpaceTexts.NotificationJumpAbortedNoLocation,
        MySpaceTexts.NotificationJumpAbortedShortDistance,
        MySpaceTexts.NotificationJumpAbortedAlreadyJumping,
    };

    public static bool Prefix(MyHudNotificationBase notification)
    {
        if (!Config.Current.Enabled || notification is not MyHudNotification message)
            return true;

        if (Aborting && AbortTexts.Contains(message.Text))
            return false;

        // The plugin shows the gate's phase instead (ShowJumpMessage)
        if (message.Text == MySpaceTexts.NotificationJumpWarmupTime)
            return false;

        return true;
    }

    private static MyHudNotification jumpMessage;
    private static string jumpMessageText;

    /// <summary>Shows the camera's jump phase as a HUD message until it changes; null removes it</summary>
    public static void ShowJumpMessage(string text, double? jumpTime)
    {
        if (text == jumpMessageText)
            return;

        // The next second of the countdown, or the next phase: the same message, so it does not blink
        if (jumpMessage != null && text != null)
        {
            jumpMessageText = text;
            jumpMessage.SetTextFormatArguments(text);
            Log.Debug($"Jump message: {text} (t{jumpTime:+0.00;-0.00})");
            return;
        }

        if (jumpMessage != null)
            MyHud.Notifications.Remove(jumpMessage);
        jumpMessage = null;
        jumpMessageText = text;
        if (text == null)
            return;

        jumpMessage = new MyHudNotification(MySpaceTexts.Format_OneParameter, 60000, "Blue", priority: 3);
        jumpMessage.SetTextFormatArguments(text);
        MyHud.Notifications.Add(jumpMessage);
        Log.Debug($"Jump message: {text} (t{jumpTime:+0.00;-0.00})");
    }

    public static void ShowJumpAborted()
    {
        var failure = new MyHudNotification(MySpaceTexts.Format_OneParameter, 2500, "Red", level: MyNotificationLevel.Important);
        failure.SetTextFormatArguments("JUMP ABORTED!");
        MyHud.Notifications.Add(failure);
        Log.Debug("Jump message: JUMP ABORTED!");
    }
}
