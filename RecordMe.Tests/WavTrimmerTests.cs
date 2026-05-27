using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using RecordMe.Services;

namespace RecordMe.Tests;

public class WavTrimmerTests : IDisposable
{
    private const int SampleRate = 44100;
    private readonly List<string> _tempFiles = [];

    [Fact]
    public void TrimSilence_RemovesLeadingAndTrailingSilence()
    {
        // 1s silence | 0.5s tone | 1s silence  (mono)
        int silence = SampleRate;          // 1.0s
        int tone = SampleRate / 2;         // 0.5s
        string path = CreateWav(mono: true, silentFrames: silence, toneFrames: tone, trailingSilentFrames: silence);
        int originalFrames = FrameCount(path);

        WavTrimmer.TrimSilence(path);

        int trimmedFrames = FrameCount(path);
        Assert.True(trimmedFrames < originalFrames, "Expected the file to get shorter.");

        // What's left should be the tone plus a fade margin on each side (~480 frames),
        // and well under the original ~2.5s.
        Assert.True(trimmedFrames >= tone, $"Trimmed ({trimmedFrames}) should keep the whole tone ({tone}).");
        Assert.True(trimmedFrames < tone + 4 * 480, $"Trimmed ({trimmedFrames}) kept too much silence.");
    }

    [Fact]
    public void TrimSilence_PreservesTheSignalContent()
    {
        int silence = SampleRate;
        int tone = SampleRate / 4;
        string path = CreateWav(mono: true, silentFrames: silence, toneFrames: tone, trailingSilentFrames: silence);

        WavTrimmer.TrimSilence(path);

        // The loud tone (amplitude 0.5) must survive trimming.
        float peak = PeakAmplitude(path);
        Assert.True(peak > 0.4f, $"Expected the ~0.5 tone to remain, got peak {peak}.");
    }

    [Fact]
    public void TrimSilence_AllSilence_LeavesFileUnchanged()
    {
        string path = CreateWav(mono: true, silentFrames: SampleRate, toneFrames: 0, trailingSilentFrames: 0);
        int originalFrames = FrameCount(path);

        WavTrimmer.TrimSilence(path);

        // Nothing crosses the threshold, so the trimmer should skip the rewrite.
        Assert.Equal(originalFrames, FrameCount(path));
    }

    [Fact]
    public void TrimSilence_StereoFile_TrimsAndStaysStereo()
    {
        int silence = SampleRate;
        int tone = SampleRate / 2;
        string path = CreateWav(mono: false, silentFrames: silence, toneFrames: tone, trailingSilentFrames: silence);
        int originalFrames = FrameCount(path);

        WavTrimmer.TrimSilence(path);

        using var reader = new AudioFileReader(path);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.True(FrameCount(path) < originalFrames);
    }

    [Fact]
    public void TrimSilence_MissingFile_DoesNotThrow()
    {
        string path = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.wav");
        var ex = Record.Exception(() => WavTrimmer.TrimSilence(path));
        Assert.Null(ex);
    }

    // --- helpers ---

    private string CreateWav(bool mono, int silentFrames, int toneFrames, int trailingSilentFrames)
    {
        int channels = mono ? 1 : 2;
        string path = Path.Combine(Path.GetTempPath(), $"wavtrim_{Guid.NewGuid():N}.wav");
        _tempFiles.Add(path);

        var format = new WaveFormat(SampleRate, 16, channels);
        using var writer = new WaveFileWriter(path, format);

        WriteSilence(writer, silentFrames, channels);
        WriteTone(writer, toneFrames, channels, amplitude: 0.5f);
        WriteSilence(writer, trailingSilentFrames, channels);

        return path;
    }

    private static void WriteSilence(WaveFileWriter writer, int frames, int channels)
    {
        for (int f = 0; f < frames; f++)
            for (int ch = 0; ch < channels; ch++)
                writer.WriteSample(0f);
    }

    private static void WriteTone(WaveFileWriter writer, int frames, int channels, float amplitude)
    {
        for (int f = 0; f < frames; f++)
        {
            float sample = amplitude * MathF.Sin(2 * MathF.PI * 440 * f / SampleRate);
            for (int ch = 0; ch < channels; ch++)
                writer.WriteSample(sample);
        }
    }

    private static int FrameCount(string path)
    {
        using var reader = new AudioFileReader(path);
        long sampleCount = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
        return (int)(sampleCount / reader.WaveFormat.Channels);
    }

    private static float PeakAmplitude(string path)
    {
        using var reader = new AudioFileReader(path);
        var buffer = new float[reader.Length / (reader.WaveFormat.BitsPerSample / 8)];
        int read = reader.Read(buffer, 0, buffer.Length);
        float peak = 0f;
        for (int i = 0; i < read; i++)
            peak = MathF.Max(peak, MathF.Abs(buffer[i]));
        return peak;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
        }
    }
}
