using System;
using System.Reflection;
using VRage.Audio;

namespace ClientPlugin.Effects;

/// <summary>
/// The low-pass of a game sound voice: XAudio2's state-variable filter, which every voice of the game's pool has. Through
/// reflection on the voice's own types: the SharpDX the plugin compiles against is not the one the game loads.
/// </summary>
public static class VoiceFilter
{
    // The plugin's sounds: 44.1 kHz
    private const double SampleRate = 44100;

    private static PropertyInfo voiceProperty;
    private static MethodInfo setFilter;
    private static Type parametersType;
    private static object lowPassType;
    private static bool failed;

    /// <summary>
    /// Sets a low-pass at the cutoff (Hz): Frequency 2 sin(pi f / rate), 1 for none. OneOverQ 1.4 has no bump at the
    /// cutoff; it goes to 1 as the frequency nears 1, where a higher value would lift the top end. False when the voice
    /// has no filter to set.
    /// </summary>
    public static bool LowPass(IMySourceVoice voice, float cutoff)
    {
        var f = Math.Min(1.0, 2.0 * Math.Sin(Math.PI * cutoff / SampleRate));
        return Set(voice, (float)f, (float)(1.4 - 0.4 * f * f * f * f));
    }

    /// <summary>No filter: as the game leaves its voices</summary>
    public static void Clear(IMySourceVoice voice) => Set(voice, 1f, 1f);

    private static bool Set(IMySourceVoice voice, float frequency, float oneOverQ)
    {
        if (failed || voice == null)
            return false;

        try
        {
            if (voiceProperty == null || !voiceProperty.DeclaringType!.IsInstanceOfType(voice))
            {
                voiceProperty = voice.GetType().GetProperty("Voice", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var sourceVoice = voiceProperty?.PropertyType;
                var assembly = sourceVoice?.Assembly;
                parametersType = assembly?.GetType("SharpDX.XAudio2.FilterParameters");
                var filterType = assembly?.GetType("SharpDX.XAudio2.FilterType");
                setFilter = sourceVoice?.GetMethod("SetFilterParameters", new[] { parametersType, typeof(int) });
                if (setFilter == null || filterType == null)
                {
                    failed = true;
                    Log.Info($"Voice filter: no SetFilterParameters on {voice.GetType().FullName}, the bolts play unfiltered");
                    return false;
                }

                lowPassType = Enum.Parse(filterType, "LowPassFilter");
            }

            var target = voiceProperty.GetValue(voice);
            if (target == null)
                return false;

            var parameters = Activator.CreateInstance(parametersType);
            parametersType.GetField("Type").SetValue(parameters, lowPassType);
            parametersType.GetField("Frequency").SetValue(parameters, frequency);
            parametersType.GetField("OneOverQ").SetValue(parameters, oneOverQ);
            setFilter.Invoke(target, new[] { parameters, (object)0 });
            return true;
        }
        catch (Exception e)
        {
            failed = true;
            Log.Info($"Voice filter: {e.GetBaseException().Message}; the bolts play unfiltered");
            return false;
        }
    }
}
