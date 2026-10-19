using System;
using System.IO;

namespace ClientPlugin.Core;

/// <summary>
/// The lightning bolts cut from Space Engineers' own weather lightning (Content/Audio/ARC/EVENT/Lightning_0..3.wav,
/// 44.1 kHz mono 16-bit), made on the player's machine from the game's files, not shipped with the plugin: 5 ms fade-in,
/// a snap's fast decay, the last 30 % faded out, the peak at -3 dBFS.
/// </summary>
public static class LightningCuts
{
    /// <summary>File name, source file, start s, length s, a snap's decay (s, 0 for none)</summary>
    public static readonly (string Name, string Source, double Start, double Length, double Decay)[] Cuts =
    {
        ("strike1", "Lightning_0", 0.08, 1.2, 0),
        ("strike2", "Lightning_2", 0.08, 1.2, 0),
        ("snap1", "Lightning_0", 0.10, 0.4, 0.08),
        ("snap2", "Lightning_2", 0.10, 0.4, 0.08),
        ("snap3", "Lightning_2", 0.33, 0.4, 0.08),
        ("snap4", "Lightning_0", 0.20, 0.4, 0.08),
        ("flash1", "Lightning_0", 3.04, 1.3, 0),
    };

    public const int SampleRate = 44100;

    /// <summary>One cut from the samples of its source file, as 16-bit samples</summary>
    public static short[] Cut(short[] source, double start, double length, double decay)
    {
        var from = (int)(start * SampleRate);
        var n = Math.Min((int)(length * SampleRate), source.Length - from);
        if (n <= 0)
            throw new ArgumentException($"cut at {start} s is beyond the end of the source");

        var y = new double[n];
        for (var i = 0; i < n; i++)
            y[i] = source[from + i] / 32768.0;

        var fadeIn = (int)(0.005 * SampleRate);
        for (var i = 0; i < Math.Min(fadeIn, n); i++)
            y[i] *= fadeIn == 1 ? 0 : i / (double)(fadeIn - 1);

        if (decay > 0)
            for (var i = 0; i < n; i++)
                y[i] *= Math.Exp(-i / (double)SampleRate / decay);

        var tail = (int)(0.3 * n);
        for (var i = 0; i < tail; i++)
        {
            var g = 1.0 - i / (double)(tail - 1);
            y[n - tail + i] *= g * g;
        }

        var peak = 0.0;
        foreach (var v in y)
            peak = Math.Max(peak, Math.Abs(v));
        var scale = peak > 0 ? Math.Pow(10, -3 / 20.0) / peak : 0;

        var result = new short[n];
        for (var i = 0; i < n; i++)
            result[i] = (short)Math.Round(Math.Max(-1.0, Math.Min(32767 / 32768.0, y[i] * scale)) * 32768.0);
        return result;
    }

    /// <summary>The samples of a 44.1 kHz mono 16-bit PCM WAV file</summary>
    public static short[] ReadWav(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF")
            throw new InvalidDataException($"{path}: not a RIFF file");
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
            throw new InvalidDataException($"{path}: not a WAVE file");

        var format = false;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            var next = reader.BaseStream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                var tag = reader.ReadInt16();
                var channels = reader.ReadInt16();
                var rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                var bits = reader.ReadInt16();
                if (tag != 1 || channels != 1 || rate != SampleRate || bits != 16)
                    throw new InvalidDataException($"{path}: format {tag}, {channels} channels, {rate} Hz, {bits} bits; expected 44.1 kHz mono 16-bit PCM");
                format = true;
            }
            else if (id == "data")
            {
                if (!format)
                    throw new InvalidDataException($"{path}: data before format");
                var samples = new short[size / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = reader.ReadInt16();
                return samples;
            }

            reader.BaseStream.Position = next;
        }

        throw new InvalidDataException($"{path}: no data");
    }

    /// <summary>Writes 44.1 kHz mono 16-bit PCM</summary>
    public static void WriteWav(string path, short[] samples)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF".ToCharArray());
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVEfmt ".ToCharArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data".ToCharArray());
        writer.Write(samples.Length * 2);
        foreach (var s in samples)
            writer.Write(s);
    }
}
