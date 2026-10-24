using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Game.Localization;
using VRage.Utils;

namespace ClientPlugin.Patches;

/// <summary>
/// The jump's HUD message names what the gate is doing instead of the vanilla "Ship will jump in N seconds":
/// Manifesting Gate (Ns), In Transit (Ns), Jump Complete (see JumpEffects.JumpMessage). An aborted jump shows "JUMP ABORTED!"
/// instead of the reason, and a player the jump would leave behind sees "JUMP IMMINENT!" in the same red.
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

    private static MyHudNotification imminent;

    private static double imminentAddedAt;

    /// <summary>Shows "JUMP IMMINENT!" in the red of the abort while the local player is about to be left behind, see JumpEffects.LeftBehind</summary>
    public static void ShowJumpImminent(bool show, double now, double? jumpTime)
    {
        if (show && imminent != null)
        {
            // Added again every second (adding one already shown only renews it): the HUD may have dropped it, as at
            // a change of the controlled entity
            if (now - imminentAddedAt >= 1.0)
            {
                MyHud.Notifications.Add(imminent);
                imminentAddedAt = now;
            }

            return;
        }

        if (show == (imminent != null))
            return;

        if (imminent != null)
        {
            MyHud.Notifications.Remove(imminent);
            imminent = null;
            Log.Debug($"Jump message: JUMP IMMINENT! removed (t{jumpTime:+0.00;-0.00})");
            return;
        }

        imminent = new MyHudNotification(MySpaceTexts.Format_OneParameter, 60000, "Red", level: MyNotificationLevel.Important);
        imminent.SetTextFormatArguments("JUMP IMMINENT!");
        MyHud.Notifications.Add(imminent);
        imminentAddedAt = now;
        Log.Debug($"Jump message: JUMP IMMINENT! (t{jumpTime:+0.00;-0.00})");
    }

    public static void ShowJumpAborted()
    {
        var failure = new MyHudNotification(MySpaceTexts.Format_OneParameter, 2500, "Red", level: MyNotificationLevel.Important);
        failure.SetTextFormatArguments("JUMP ABORTED!");
        MyHud.Notifications.Add(failure);
        Log.Debug("Jump message: JUMP ABORTED!");
    }
}
