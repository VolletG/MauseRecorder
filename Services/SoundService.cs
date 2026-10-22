using System.IO;
using System.Media;

namespace MauseRecorder.Services;

/// <summary>
/// A tiny synthesized click, so we do not ship audio files.
/// </summary>
public static class SoundService
{
    private static readonly Lazy<SoundPlayer> ClickPlayer = new(CreateClickPlayer);

    public static void PlayClick()
    {
        try { ClickPlayer.Value.Play(); }
        catch { /* Sound is optional. */ }
    }

    private static SoundPlayer CreateClickPlayer()
    {
        const int sampleRate = 44100;
        const double seconds = 0.028;
        var sampleCount = (int)(sampleRate * seconds);
        var dataSize = sampleCount * 2;

        using var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);

        var rng = new Random(7);
        for (var i = 0; i < sampleCount; i++)
        {
            var t = (double)i / sampleRate;
            var envelope = Math.Exp(-t * 150);
            var tone = Math.Sin(2 * Math.PI * 1650 * t) * 0.24;
            var tick = (rng.NextDouble() * 2 - 1) * 0.07;
            var sample = (short)(Math.Clamp((tone + tick) * envelope, -1, 1) * short.MaxValue);
            writer.Write(sample);
        }

        writer.Flush();
        stream.Position = 0;
        var player = new SoundPlayer(stream);
        player.Load();
        return player;
    }
}
