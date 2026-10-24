using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClientPlugin.Core;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using VRage;
using VRage.Audio;
using VRage.Data.Audio;
using VRage.FileSystem;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Effects;

public enum JumpSound
{
    /// <summary>The gate opening: once at the cloud when the countdown starts</summary>
    Opening,

    /// <summary>The open gate's roar, looping at every cloud while it lasts: near a dense cloud</summary>
    Cruising,

    /// <summary>The roar from a thin, small or distant cloud, crossfaded with <see cref="Cruising"/></summary>
    CruisingFar,

    /// <summary>The jump's rumble: once at the cloud left behind and once at the exit, there also for the pilots who arrive</summary>
    Jump,

    /// <summary>A wail of the gate, heard inside it from right before the jump on: one of several variants at random, a stream of them</summary>
    Wail,

    /// <summary>A roar of the gate echoing, from right before the jump on: one of several variants at random, now and then</summary>
    Echo,

    /// <summary>An aborted jump: once at the cloud torn apart</summary>
    Abort,

    /// <summary>A big lightning bolt seen in the fog, near: one of several variants at random</summary>
    Strike,

    /// <summary>A small bolt, or a big one too soon after the last strike: one of several variants at random</summary>
    Snap,

    /// <summary>A discharge deep in the fog, seen only as a flash: duller, one of several variants at random</summary>
    Flash,
}

/// <summary>
/// Sound slots of the gate. The jump is heard from the gate, the cloud, by the pilots and the observers alike: the jump
/// drives themselves are silent. Each stage has its one sound, the plugin's file in Assets; the wails and echoes
/// are cut from the plugin's roar (<see cref="EchoCuts"/>), the discharges from the game's own lightning
/// (<see cref="LightningCuts"/>). <see cref="GateSound"/> plays the gate's phases and echoes at each cloud, the cloud
/// its discharges.
/// </summary>
public static class JumpSounds
{
    /// <summary>The plugin's asset folder, its own sounds are in it; null if Pulsar did not provide it</summary>
    public static string AssetFolder;

    // A jump is heard at the clouds up to this far
    private const float CloudMaxDistance = 2000f;

    // Variants of a discharge, wail or echo: name1.wav .. name9.wav
    private const int MaxVariants = 9;

    private static readonly JumpSound[] Slots = (JumpSound[])Enum.GetValues(typeof(JumpSound));

    // Resolved for the session, per slot
    private static readonly Dictionary<JumpSound, MySoundPair> Pairs = new Dictionary<JumpSound, MySoundPair>();

    private static readonly Dictionary<MyJumpDriveDefinition, (MySoundPair Charging, MySoundPair JumpOut, MySoundPair JumpIn)> Originals =
        new Dictionary<MyJumpDriveDefinition, (MySoundPair, MySoundPair, MySoundPair)>();

    /// <summary>
    /// The sound of each stage, a file in Assets, and its emitter volume, balancing the stages against each other
    /// </summary>
    private static readonly Dictionary<JumpSound, (string File, float Volume)> StageSounds = new Dictionary<JumpSound, (string, float)>
    {
        [JumpSound.Opening] = ("spoolup", 0.392f),
        [JumpSound.Cruising] = ("roar", 0.75f),
        [JumpSound.CruisingFar] = ("roar.far", 0.75f),
        [JumpSound.Jump] = ("burst", 1.0f),
        [JumpSound.Abort] = ("tear-discharge", 1.0f),
    };

    // The lightning plays 4 dB under the stage sounds
    private const float LightningVolume = 0.631f;

    // The stream of wails about 5 dB under the lightning's strikes and 4 dB under the roar, the echoes a little louder
    // but now and then; each one a few semitones higher or lower at random (cue pitch variation, cents)
    private const float WailVolume = 0.3f, EchoVolume = 0.4f;
    private const float WailPitchVariation = 300f, EchoPitchVariation = 200f;

    /// <summary>At the start of a session: resolves the slots, registers the sound files and silences the jump drives</summary>
    public static void Apply()
    {
        Pairs.Clear();
        var enabled = Config.Current.Enabled && MyAudio.Static != null;
        if (enabled)
        {
            foreach (var slot in Slots)
                Pairs[slot] = Resolve(slot);
        }

        foreach (var definition in MyDefinitionManager.Static.GetAllDefinitions().OfType<MyJumpDriveDefinition>())
        {
            if (!Originals.TryGetValue(definition, out var original))
            {
                original = (definition.ChargingSound, definition.JumpOutSound, definition.JumpInSound);
                Originals[definition] = original;
            }

            // No sound comes from the drive: the gate carries the whole jump
            definition.ChargingSound = enabled ? MySoundPair.Empty : original.Charging;
            definition.JumpOutSound = enabled ? MySoundPair.Empty : original.JumpOut;
            definition.JumpInSound = enabled ? MySoundPair.Empty : original.JumpIn;
        }
    }

    private static bool IsDischarge(JumpSound slot) => slot == JumpSound.Strike || slot == JumpSound.Snap || slot == JumpSound.Flash;

    private static MySoundPair Pair(JumpSound slot) => Pairs.TryGetValue(slot, out var pair) ? pair : null;

    private static bool IsLoop(JumpSound slot) => slot == JumpSound.Cruising || slot == JumpSound.CruisingFar;

    public static bool IsEcho(JumpSound slot) => slot == JumpSound.Wail || slot == JumpSound.Echo;

    private static MySoundPair Resolve(JumpSound slot)
    {
        if (IsDischarge(slot))
        {
            var files = GameLightningFiles(slot.ToString().ToLowerInvariant());
            return files.Count != 0 ? FilePair(slot, files.ToArray()) : MySoundPair.Empty;
        }

        if (IsEcho(slot))
        {
            var files = EchoFiles(slot.ToString().ToLowerInvariant());
            return files.Count != 0 ? FilePair(slot, files.ToArray()) : MySoundPair.Empty;
        }

        var file = StageSounds[slot].File;
        if (AssetFolder == null)
        {
            Log.Info($"Sound {slot}: no asset folder for {file}, silent");
            return MySoundPair.Empty;
        }

        return FilePair(slot, Path.Combine(AssetFolder, file + ".wav"));
    }

    /// <summary>The lightning cut from the game's own (<see cref="LightningCuts"/>), made once in the plugin's storage</summary>
    public static string GameLightningFolder => Path.Combine(MyFileSystem.UserDataPath, "Storage", Plugin.Name, "Lightning", "se");

    // Bump to make the files again after a change of the cuts
    private const string GameLightningVersion = "1133-v03";

    private static List<string> GameLightningFiles(string name)
    {
        var files = new List<string>();
        try
        {
            var folder = GameLightningFolder;
            var stamp = Path.Combine(folder, "version.txt");
            if (!File.Exists(stamp) || File.ReadAllText(stamp).Trim() != GameLightningVersion)
            {
                Directory.CreateDirectory(folder);
                var sources = Path.Combine(MyFileSystem.ContentPath, "Audio", "ARC", "EVENT");
                foreach (var (file, source, start, length, decay) in LightningCuts.Cuts)
                    LightningCuts.WriteWav(Path.Combine(folder, file + ".wav"), LightningCuts.Cut(LightningCuts.ReadWav(Path.Combine(sources, source + ".wav")), start, length, decay));
                File.WriteAllText(stamp, GameLightningVersion);
                Log.Info($"Lightning cut from the game's own into {folder}");
            }

            for (var i = 1; i <= MaxVariants; i++)
            {
                var variant = Path.Combine(folder, $"{name}{i}.wav");
                if (File.Exists(variant))
                    files.Add(variant);
            }
        }
        catch (Exception e)
        {
            Log.Info($"Lightning: cannot cut the game's lightning sounds: {e.Message}");
        }

        return files;
    }

    /// <summary>The wails and echoes cut from the plugin's roar (<see cref="EchoCuts"/>), made once in the plugin's storage</summary>
    public static string EchoFolder => Path.Combine(MyFileSystem.UserDataPath, "Storage", Plugin.Name, "Echoes");

    // Bump to make the files again after a change of the cuts or of the roar
    private const string EchoVersion = "1435-v02";

    private static List<string> EchoFiles(string name)
    {
        var files = new List<string>();
        if (AssetFolder == null)
        {
            Log.Info($"Echoes: no asset folder to cut {name} from, silent");
            return files;
        }

        try
        {
            var folder = EchoFolder;
            var stamp = Path.Combine(folder, "version.txt");
            if (!File.Exists(stamp) || File.ReadAllText(stamp).Trim() != EchoVersion)
            {
                Directory.CreateDirectory(folder);
                foreach (var (file, source, start, length, highPass, fadeIn, fadeOut, taps) in EchoCuts.Cuts)
                    LightningCuts.WriteWav(Path.Combine(folder, file + ".wav"), EchoCuts.Cut(LightningCuts.ReadWav(Path.Combine(AssetFolder, source + ".wav")), start, length, highPass, fadeIn, fadeOut, taps));
                File.WriteAllText(stamp, EchoVersion);
                Log.Info($"Echoes cut from the roar into {folder}");
            }

            for (var i = 1; i <= MaxVariants; i++)
            {
                var variant = Path.Combine(folder, $"{name}{i}.wav");
                if (File.Exists(variant))
                    files.Add(variant);
            }
        }
        catch (Exception e)
        {
            Log.Info($"Echoes: cannot cut them from the roar: {e.Message}");
        }

        return files;
    }

    private static MySoundPair FilePair(JumpSound slot, params string[] paths)
    {
        var missing = paths.FirstOrDefault(path => !File.Exists(path));
        if (missing != null)
        {
            Log.Info($"Sound {slot}: file not found: {missing}");
            return MySoundPair.Empty;
        }

        var cue = $"{Plugin.Name}_{slot}";
        var pitchVariation = slot == JumpSound.Wail ? WailPitchVariation : slot == JumpSound.Echo ? EchoPitchVariation : 0f;
        if (!RegisterCue(cue, paths, CloudMaxDistance, IsLoop(slot), pitchVariation))
            return MySoundPair.Empty;

        Log.Debug($"Sound {cue}: {string.Join(", ", paths)}");
        return new MySoundPair(cue);
    }

    /// <summary>
    /// Adds a 3D cue to the loaded cue bank, the same as an audio definition of a mod would: one of the files at random,
    /// or a loop of the first file; pitch variation in cents, at random for each sound played
    /// </summary>
    private static bool RegisterCue(string cue, string[] paths, float maxDistance, bool loop, float pitchVariation)
    {
        try
        {
            var audio = MyAudio.Static;
            if (AccessTools.Field(audio.GetType(), "m_cueBank")?.GetValue(audio) is not MyCueBank cueBank ||
                AccessTools.Field(typeof(MyCueBank), "m_waveBank")?.GetValue(cueBank) is not MyWaveBank waveBank)
            {
                Log.Info($"No cue bank to add {cue} to (audio {audio.GetType().Name})");
                return false;
            }

            var data = new MySoundData
            {
                SubtypeId = MyStringHash.GetOrCompute(cue),
                Category = MyStringId.GetOrCompute("SHIP"),
                MaxDistance = maxDistance,
                Volume = 1f,
                Loopable = loop,
                PitchVariation = pitchVariation,
                Waves = loop
                    ? new List<MyAudioWave> { new MyAudioWave { Type = MySoundDimensions.D3, Loop = paths[0] } }
                    : paths.Select(path => new MyAudioWave { Type = MySoundDimensions.D3, Start = path }).ToList(),
            };

            cueBank.m_cues[new MyCueId(data.SubtypeId)] = data;
            foreach (var wave in data.Waves)
                waveBank.Add(data, wave, cueBank.CacheLoaded);
            return true;
        }
        catch (Exception e)
        {
            Log.Error(e);
            return false;
        }
    }

    public static bool Has(JumpSound slot)
    {
        var pair = Pair(slot);
        return pair != null && pair != MySoundPair.Empty && (pair.Arcade.Hash != MyStringHash.NullOrEmpty || pair.Realistic.Hash != MyStringHash.NullOrEmpty);
    }

    public static MySoundPair PairOf(JumpSound slot) => Has(slot) ? Pair(slot) : null;

    /// <summary>
    /// The volume of a slot (emitter volume multiplier), which balances the gate's sounds to each other. The loops and
    /// the discharges are scaled further by the cloud's state.
    /// </summary>
    public static float Volume(JumpSound slot) => IsDischarge(slot) ? LightningVolume : slot == JumpSound.Wail ? WailVolume : slot == JumpSound.Echo ? EchoVolume : StageSounds[slot].Volume;

    // A sound quieter than this is not started: only a discharge far away is that quiet
    private const float Inaudible = 0.015f;

    // Own emitters for the discharges, which play at their own volumes: the game's pooled emitters would keep the
    // volume. Strikes and snaps share a pool. A full pool takes the voice started longest ago: that cuts the tail of
    // the oldest sound, the new one is always heard.
    private static readonly MyEntity3DSoundEmitter[] StrikeEmitters = new MyEntity3DSoundEmitter[12];
    private static readonly MyEntity3DSoundEmitter[] FlashEmitters = new MyEntity3DSoundEmitter[24];
    private static int nextStrikeEmitter;
    private static int nextFlashEmitter;

    /// <summary>Discharges, wails and echoes heard in this session, for the debug log</summary>
    public static int StrikesPlayed, SnapsPlayed, FlashesPlayed, WailsPlayed, EchoesPlayed;

    // Discharges seen in the render frame, played in the next simulation frame
    private static readonly List<(Vector3D Position, JumpSound Slot, float Cutoff, float Volume)> Discharges =
        new List<(Vector3D, JumpSound, float, float)>();

    // The muffling of a discharge follows its clarity (its brightness dimmed by the fog between it and the camera):
    // a low-pass from the first cutoff (Hz) at clarity 0 up to the second at the full clarity, on a log scale. Strikes
    // and snaps are sharp (none at all at the top: XAudio2's filter passes everything at 7350 Hz), flashes dull.
    private const float BoltCutoffLow = 1500f, BoltCutoffHigh = 7350f, BoltClarityFull = 0.6f;
    private const float FlashCutoffLow = 250f, FlashCutoffHigh = 1200f, FlashClarityFull = 0.5f;

    /// <summary>
    /// A discharge in a cloud (<see cref="JumpSound.Strike"/>, <see cref="JumpSound.Snap"/> or
    /// <see cref="JumpSound.Flash"/>) at its position. Loudness 0..1 comes from its size, distance and how deep in the
    /// fog it is, clarity 0..1 from the same without the distance: it sets how muffled it sounds. Called from the render
    /// frame, which runs while the game updates its sounds in parallel: the sound starts in the next simulation frame.
    /// </summary>
    public static void PlayDischarge(Vector3D position, JumpSound slot, float loudness, float clarity)
    {
        var volume = Volume(slot) * Math.Min(loudness, 1f);
        if (volume < Inaudible || !Has(slot))
            return;

        // Not every bolt is heard: at most one sharp sound (strike or snap) a second and two flashes, from
        // every cloud together, and only the brighter ones (by clarity: the same bolts near and far, quieter far away).
        // The bolts are still drawn.
        var flash = slot == JumpSound.Flash;
        var now = JumpEffects.Now;
        ref var last = ref flash ? ref lastFlashSoundAt : ref lastSharpSoundAt;
        if (clarity < (flash ? FlashMinClarity : SharpMinClarity) || now - last < (flash ? FlashSoundSpacing : SharpSoundSpacing))
            return;
        last = now;

        var (low, high, full) = flash ? (FlashCutoffLow, FlashCutoffHigh, FlashClarityFull) : (BoltCutoffLow, BoltCutoffHigh, BoltClarityFull);
        var q = MathHelper.Clamp(clarity / full, 0f, 1f);
        Discharges.Add((position, slot, low * (float)Math.Pow(high / low, q), volume));
    }

    // The rate cap of the discharge sounds: seconds between two, and the clarity a discharge needs to be heard at all
    private const double SharpSoundSpacing = 1.0, FlashSoundSpacing = 0.5;
    private const float SharpMinClarity = 0.25f, FlashMinClarity = 0.12f;
    private static double lastSharpSoundAt = double.NegativeInfinity, lastFlashSoundAt = double.NegativeInfinity;

    // Voices of the game's pool given a low-pass, set back to no filter once they stop: the pool does not reset the
    // filter, the next sound on the voice would be muffled too
    private static readonly HashSet<IMySourceVoice> Filtered = new HashSet<IMySourceVoice>();

    private static void StartDischarge(Vector3D position, JumpSound slot, float cutoff, float volume)
    {
        var flash = slot == JumpSound.Flash;
        var emitters = flash ? FlashEmitters : StrikeEmitters;
        ref var next = ref flash ? ref nextFlashEmitter : ref nextStrikeEmitter;

        // A free emitter, else the one started longest ago
        var index = next;
        for (var i = 0; i < emitters.Length; i++)
        {
            var candidate = (next + i) % emitters.Length;
            if (emitters[candidate] == null || !emitters[candidate].IsPlaying)
            {
                index = candidate;
                break;
            }
        }

        next = (index + 1) % emitters.Length;
        switch (slot)
        {
            case JumpSound.Flash:
                FlashesPlayed++;
                break;
            case JumpSound.Snap:
                SnapsPlayed++;
                break;
            default:
                StrikesPlayed++;
                break;
        }

        var emitter = emitters[index] ??= new MyEntity3DSoundEmitter(null);
        emitter.SetPosition(position);
        emitter.VolumeMultiplier = volume;
        Play(emitter, Pair(slot));
        if (emitter.Sound is IMySourceVoice voice && VoiceFilter.LowPass(voice, cutoff))
        {
            Filtered.Add(voice);
            Log.Debug($"Discharge {slot}: low-pass {cutoff:0} Hz, volume {volume:0.000}");
        }
        else
        {
            Log.Debug($"Discharge {slot}: no voice for the low-pass");
        }
    }

    private static void ResetFilters(bool all)
    {
        Filtered.RemoveWhere(voice =>
        {
            if (!all && StrikeEmitters.Concat(FlashEmitters).Any(e => e != null && e.IsPlaying && e.Sound == voice))
                return false;
            VoiceFilter.Clear(voice);
            return true;
        });
    }

    /// <summary>
    /// Starts a sound on an emitter of no entity. With realistic sound on, the game plays such a sound in space only
    /// when told that it is always heard; its helmet and cockpit filters still apply.
    /// </summary>
    public static bool Play(MyEntity3DSoundEmitter emitter, MySoundPair pair, bool skipIntro = false)
    {
        return emitter.PlaySound(pair, stopPrevious: true, skipIntro: skipIntro, alwaysHearOnRealistic: true);
    }

    // One-shots of finished clouds playing out at the cloud's last place
    private static readonly List<(MyEntity3DSoundEmitter Emitter, Vector3D Position)> PlayingOut = new List<(MyEntity3DSoundEmitter, Vector3D)>();

    /// <summary>Lets a one-shot of a cloud that is gone play out where the cloud was</summary>
    public static void PlayOut(MyEntity3DSoundEmitter emitter, Vector3D position)
    {
        if (emitter.IsPlaying)
            PlayingOut.Add((emitter, position));
    }

    /// <summary>Simulation frame, before the game updates its sounds</summary>
    public static void Update()
    {
        ResetFilters(false);
        foreach (var (position, slot, cutoff, volume) in Discharges)
            StartDischarge(position, slot, cutoff, volume);
        Discharges.Clear();

        // Relative to the camera: set again every frame as the camera moves
        for (var i = PlayingOut.Count - 1; i >= 0; i--)
        {
            var (emitter, position) = PlayingOut[i];
            if (emitter.IsPlaying)
                emitter.SetPosition(position);
            else
                PlayingOut.RemoveAt(i);
        }
    }

    /// <summary>At the end of a session: the emitters belong to it</summary>
    public static void Reset()
    {
        foreach (var (emitter, _) in PlayingOut)
            emitter.StopSound(true);
        PlayingOut.Clear();
        Discharges.Clear();
        ResetFilters(true);
        lastSharpSoundAt = lastFlashSoundAt = double.NegativeInfinity;

        foreach (var emitters in new[] { StrikeEmitters, FlashEmitters })
        {
            for (var i = 0; i < emitters.Length; i++)
            {
                emitters[i]?.StopSound(true);
                emitters[i] = null;
            }
        }
    }
}
