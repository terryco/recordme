using NAudio.Wave;
using System;
using System.IO;

namespace RecordMe.Services;

public static class WavTrimmer
{
    private const float SilenceThreshold = 0.005f; // ~-46dB
    private const int FadeMarginSamples = 480;     // ~10ms at 44.1kHz, smooth fade at trim points

    public static void TrimSilence(string wavFilePath)
    {
        if (!File.Exists(wavFilePath)) return;

        float[] samples;
        WaveFormat format;

        using (var reader = new AudioFileReader(wavFilePath))
        {
            format = reader.WaveFormat;
            samples = new float[reader.Length / (format.BitsPerSample / 8)];
            int read = reader.Read(samples, 0, samples.Length);
            if (read < samples.Length)
                Array.Resize(ref samples, read);
        }

        int channels = format.Channels;
        int totalFrames = samples.Length / channels;

        // Find first frame above threshold
        int startFrame = 0;
        for (int f = 0; f < totalFrames; f++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (Math.Abs(samples[f * channels + ch]) > SilenceThreshold)
                {
                    startFrame = f;
                    goto foundStart;
                }
            }
        }
        foundStart:

        // Find last frame above threshold
        int endFrame = totalFrames - 1;
        for (int f = totalFrames - 1; f >= startFrame; f--)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (Math.Abs(samples[f * channels + ch]) > SilenceThreshold)
                {
                    endFrame = f;
                    goto foundEnd;
                }
            }
        }
        foundEnd:

        // Add a small margin so we don't clip transients
        startFrame = Math.Max(0, startFrame - FadeMarginSamples);
        endFrame = Math.Min(totalFrames - 1, endFrame + FadeMarginSamples);

        // If no meaningful trimming, skip rewrite
        if (startFrame <= FadeMarginSamples && endFrame >= totalFrames - FadeMarginSamples - 1)
            return;

        int trimmedFrames = endFrame - startFrame + 1;
        int startSample = startFrame * channels;
        int sampleCount = trimmedFrames * channels;

        // Write trimmed file
        var targetFormat = new WaveFormat(format.SampleRate, 16, channels);
        var tempPath = wavFilePath + ".tmp";

        using (var writer = new WaveFileWriter(tempPath, targetFormat))
        {
            byte[] buffer = new byte[sampleCount * 2];
            for (int i = 0; i < sampleCount; i++)
            {
                var sample = Math.Clamp(samples[startSample + i], -1f, 1f);
                short val = (short)(sample * short.MaxValue);
                buffer[i * 2] = (byte)(val & 0xFF);
                buffer[i * 2 + 1] = (byte)((val >> 8) & 0xFF);
            }
            writer.Write(buffer, 0, buffer.Length);
        }

        File.Move(tempPath, wavFilePath, overwrite: true);
    }
}
