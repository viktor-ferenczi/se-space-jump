using System;
using Sandbox;
using VRage.Audio;

namespace ClientPlugin.Effects;

/// <summary>
/// Lowers the game's music while a jump is heard, so the gate's sounds come through without being louder: down to the
/// "Music during a jump" share of the music volume in about half a second, back up in about two seconds once the clouds
/// fade. Only the playing volume changes, never the music volume setting.
/// </summary>
public static class MusicDuck
{
    // A cloud heard this loud (its level at the camera) or louder lowers the music fully
    private const float FullAt = 0.3f;

    private static float duck = 1f;
    private static bool ducking;
    private static float lowest = 1f;

    /// <param name="heard">0..1, the loudest cloud at the camera</param>
    /// <param name="dt">Seconds since the last update</param>
    public static void Update(float heard, float dt)
    {
        var audio = MyAudio.Static;
        var game = MySandboxGame.Static;
        if (audio == null || game == null)
            return;

        // The game mutes the music itself while its window is not in focus
        if (MySandboxGame.Config.EnableMuteWhenNotInFocus && !game.hasFocus)
            return;

        var target = 1f - (1f - Config.Current.MusicDuringJump) * Math.Min(1f, heard / FullAt);
        duck += (target - duck) * Math.Min(1f, dt / (target < duck ? 0.5f : 2f));
        if (duck < 0.999f)
        {
            audio.VolumeMusic = MySandboxGame.Config.MusicVolume * duck;
            if (!ducking)
                Log.Debug($"Music ducking from {MySandboxGame.Config.MusicVolume:0.00}");
            ducking = true;
            lowest = Math.Min(lowest, duck);
        }
        else if (ducking)
        {
            Restore();
        }
    }

    /// <summary>The music back at its set volume (at once: a session ends)</summary>
    public static void Restore()
    {
        duck = 1f;
        if (!ducking)
            return;

        ducking = false;
        Log.Debug($"Music restored, lowest {lowest:0.00} of its volume");
        lowest = 1f;
        if (MyAudio.Static != null && MySandboxGame.Config != null)
            MyAudio.Static.VolumeMusic = MySandboxGame.Config.MusicVolume;
    }
}
