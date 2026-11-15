using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Nava.Services;

/// <summary>Short, quiet recording cues with no dependency on Windows alert sounds.</summary>
public static class AudioCues
{
    public const int StartedDurationMilliseconds = 160;
    public const int StoppedDurationMilliseconds = 180;

    private const int SampleRate = 44100;
    private const uint SoundAsync = 0x0001;
    private const uint SoundNoDefault = 0x0002;
    private const uint SoundMemory = 0x0004;

    private static readonly byte[] StartedWave = BuildWave(
        StartedDurationMilliseconds, 523.25, 659.25, 0.045, 0.080);
    private static readonly byte[] StoppedWave = BuildWave(
        StoppedDurationMilliseconds, 587.33, 440.00, 0.040, 0.095);

    // PlaySound reads the memory after returning. These two small, immutable
    // buffers stay pinned for the process lifetime, including during overlap.
    private static readonly GCHandle StartedPin = GCHandle.Alloc(StartedWave, GCHandleType.Pinned);
    private static readonly GCHandle StoppedPin = GCHandle.Alloc(StoppedWave, GCHandleType.Pinned);

    public static void PlayStarted() => Play(StartedPin.AddrOfPinnedObject());
    public static void PlayStopped() => Play(StoppedPin.AddrOfPinnedObject());

    /// <summary>Returns an independent WAV copy for previews or headless validation.</summary>
    public static byte[] GetStartedWave() => (byte[])StartedWave.Clone();
    public static byte[] GetStoppedWave() => (byte[])StoppedWave.Clone();

    private static void Play(IntPtr wave)
    {
        try
        {
            // A new cue replaces an unfinished cue. Never fall back to an OS beep.
            _ = PlaySoundW(wave, IntPtr.Zero, SoundAsync | SoundMemory | SoundNoDefault);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static byte[] BuildWave(int milliseconds, double fromHz, double toHz, double peak, double glideSeconds)
    {
        int count = SampleRate * milliseconds / 1000;
        double duration = (count - 1.0) / SampleRate;
        var samples = new double[count];
        double phase = 0, maximum = 0;
        for (int index = 0; index < count; index++)
        {
            double time = index / (double)SampleRate;
            double frequency = fromHz + (toHz - fromHz) * Smooth(time / glideSeconds);
            phase += 2 * Math.PI * frequency / SampleRate;
            // Eighteen milliseconds of rounded attack and sixty of rounded decay
            // remove clicks. A light harmonic adds warmth without a sharp chime.
            double envelope = Smooth(time / 0.018)
                * Smooth((duration - time) / 0.060)
                * Math.Exp(-2.4 * time / duration);
            double sample = (Math.Sin(phase) + 0.035 * Math.Sin(2 * phase)) * envelope;
            samples[index] = sample;
            maximum = Math.Max(maximum, Math.Abs(sample));
        }

        using var stream = new MemoryStream(44 + count * 2);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + count * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);             // PCM format chunk length.
        writer.Write((ushort)1);      // Linear PCM.
        writer.Write((ushort)1);      // Mono.
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(count * 2);
        double gain = maximum > 0 ? peak * short.MaxValue / maximum : 0;
        foreach (double sample in samples)
            writer.Write((short)Math.Round(sample * gain));
        writer.Flush();
        return stream.ToArray();
    }

    private static double Smooth(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundW(IntPtr sound, IntPtr module, uint flags);
}
