using System;

namespace ClientPlugin.Core;

/// <summary>
/// The hyperspace echoes, cut from the plugin's own roar (Assets/Sounds/roar.wav and roar.far.wav) on the player's
/// machine, not shipped: the wails are short stretches of the roar with its low drone filtered out, so its howling tones
/// stand out; the echoes are longer swells of the whole roar. Each fades in and out softly and repeats itself a few
/// times, quieter each time; the peak at -3 dBFS.
/// </summary>
public static class EchoCuts
{
    // Echo taps: delay s, gain
    private static readonly (double Delay, double Gain)[] WailTaps = { (0.38, 0.5), (0.76, 0.25), (1.14, 0.12) };
    private static readonly (double Delay, double Gain)[] RoarTaps = { (0.5, 0.4), (1.0, 0.16) };

    /// <summary>File name, source file, start s, length s, high-pass Hz (0 for none), fade in s, fade out s, echo taps</summary>
    public static readonly (string Name, string Source, double Start, double Length, double HighPass, double FadeIn, double FadeOut, (double Delay, double Gain)[] Taps)[] Cuts =
    {
        ("wail1", "roar", 0.3, 2.2, 120, 0.5, 1.0, WailTaps),
        ("wail2", "roar", 1.5, 2.2, 120, 0.5, 1.0, WailTaps),
        ("wail3", "roar", 4.4, 2.2, 120, 0.5, 1.0, WailTaps),
        ("wail4", "roar", 5.3, 2.2, 120, 0.5, 1.0, WailTaps),
        ("wail5", "roar", 6.8, 2.2, 120, 0.5, 1.0, WailTaps),
        ("wail6", "roar", 7.7, 2.2, 120, 0.5, 1.0, WailTaps),
        ("echo1", "roar", 0.0, 3.5, 0, 1.2, 1.8, RoarTaps),
        ("echo2", "roar", 3.2, 3.5, 0, 1.2, 1.8, RoarTaps),
        ("echo3", "roar", 6.4, 3.5, 0, 1.2, 1.8, RoarTaps),
        ("echo4", "roar.far", 1.6, 3.5, 0, 1.2, 1.8, RoarTaps),
        ("echo5", "roar.far", 4.8, 3.5, 0, 1.2, 1.8, RoarTaps),
        ("echo6", "roar.far", 6.2, 3.5, 0, 1.2, 1.8, RoarTaps),
    };

    private const int SampleRate = LightningCuts.SampleRate;

    /// <summary>One cut from the samples of its source file, as 16-bit samples</summary>
    public static short[] Cut(short[] source, double start, double length, double highPass, double fadeIn, double fadeOut, (double Delay, double Gain)[] taps)
    {
        var from = (int)(start * SampleRate);
        var n = Math.Min((int)(length * SampleRate), source.Length - from);
        if (n <= 0)
            throw new ArgumentException($"cut at {start} s is beyond the end of the source");

        var y = new double[n];
        for (var i = 0; i < n; i++)
            y[i] = source[from + i] / 32768.0;

        // Two passes of a second order high-pass: 24 dB per octave below the cutoff
        if (highPass > 0)
        {
            HighPass(y, highPass);
            HighPass(y, highPass);
        }

        // Raised cosine fades
        var fin = fadeIn * SampleRate;
        var fout = fadeOut * SampleRate;
        for (var i = 0; i < n; i++)
        {
            if (i < fin)
                y[i] *= 0.5 - 0.5 * Math.Cos(Math.PI * i / fin);
            if (n - i < fout)
                y[i] *= 0.5 - 0.5 * Math.Cos(Math.PI * (n - i) / fout);
        }

        var tail = taps.Length == 0 ? 0 : (int)(taps[taps.Length - 1].Delay * SampleRate);
        var echoed = new double[n + tail];
        Array.Copy(y, echoed, n);
        foreach (var (delay, gain) in taps)
        {
            var offset = (int)(delay * SampleRate);
            for (var i = 0; i < n; i++)
                echoed[offset + i] += gain * y[i];
        }

        var peak = 0.0;
        foreach (var v in echoed)
            peak = Math.Max(peak, Math.Abs(v));
        var scale = peak > 0 ? Math.Pow(10, -3 / 20.0) / peak : 0;

        var result = new short[echoed.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = (short)Math.Round(Math.Max(-1.0, Math.Min(32767 / 32768.0, echoed[i] * scale)) * 32768.0);
        return result;
    }

    // RBJ biquad high-pass, Q 0.7071, in place
    private static void HighPass(double[] y, double cutoff)
    {
        var w = 2 * Math.PI * cutoff / SampleRate;
        var alpha = Math.Sin(w) / (2 * 0.7071);
        var cos = Math.Cos(w);
        var a0 = 1 + alpha;
        var b0 = (1 + cos) / 2 / a0;
        var b1 = -(1 + cos) / a0;
        var b2 = b0;
        var a1 = -2 * cos / a0;
        var a2 = (1 - alpha) / a0;

        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < y.Length; i++)
        {
            var x = y[i];
            var v = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1;
            x1 = x;
            y2 = y1;
            y1 = v;
            y[i] = v;
        }
    }
}
