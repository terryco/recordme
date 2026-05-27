using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RecordMe.Services;

public class AudioDeviceInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public override string ToString() => Name;
}

public readonly record struct StereoLevel(float Left, float Right);

public class AudioCaptureService : IDisposable
{
    private WasapiCapture? _capture;
    private CircularAudioBuffer? _circularBuffer;
    private WaveFileWriter? _waveWriter;
    private readonly object _writerLock = new();
    private bool _isRecordingToFile;
    private WaveFormat? _waveFormat;

    // 5-second pre-roll buffer
    private const int PreRollSeconds = 5;

    public bool IsCapturing { get; private set; }
    public WaveFormat? WaveFormat => _waveFormat;
    public bool RecordStereoOnly { get; set; }
    // First channel of the stereo pair to extract when recording stereo-only from a
    // multichannel device (0 = channels 1-2, 2 = channels 3-4, ...).
    public int SelectedChannelPair { get; set; }

    public event EventHandler<StereoLevel>? LevelChanged;

    public static List<AudioDeviceInfo> GetCaptureDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        var enumerator = new MMDeviceEnumerator();

        try
        {
            var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            devices.Add(new AudioDeviceInfo { Id = defaultDevice.ID, Name = $"{defaultDevice.FriendlyName} (Default)" });
        }
        catch { }

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            if (devices.Exists(d => d.Id == device.ID)) continue;
            devices.Add(new AudioDeviceInfo { Id = device.ID, Name = device.FriendlyName });
        }

        // Also include loopback (output) devices for recording system audio
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            devices.Add(new AudioDeviceInfo { Id = "loopback:" + device.ID, Name = $"{device.FriendlyName} (Loopback)" });
        }

        return devices;
    }

    public void StartCapturing(string? deviceId = null)
    {
        if (IsCapturing) return;

        var enumerator = new MMDeviceEnumerator();

        // GetDevice throws COMException 0x80070490 ("Element not found") when the saved
        // device ID refers to a device that's no longer plugged in — fall back to the default.
        try
        {
            if (deviceId != null && deviceId.StartsWith("loopback:"))
            {
                var actualId = deviceId["loopback:".Length..];
                var device = enumerator.GetDevice(actualId);
                _capture = new WasapiLoopbackCapture(device);
            }
            else if (deviceId != null)
            {
                var device = enumerator.GetDevice(deviceId);
                _capture = new WasapiCapture(device);
            }
            else
            {
                _capture = new WasapiCapture();
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _capture = new WasapiCapture();
        }

        _waveFormat = _capture.WaveFormat;

        int bufferSamples = _waveFormat.SampleRate * _waveFormat.Channels * PreRollSeconds;
        _circularBuffer = new CircularAudioBuffer(bufferSamples);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
        IsCapturing = true;
    }

    public void StopCapturing()
    {
        if (!IsCapturing) return;
        _capture?.StopRecording();
        IsCapturing = false;
    }

    public string StartWriting(string outputDir)
    {
        if (_waveFormat == null) return string.Empty;

        lock (_writerLock)
        {
            if (_isRecordingToFile) return string.Empty;

            var fileName = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.TickCount64 & 0xFFFF:X4}.wav";
            var filePath = Path.Combine(outputDir, fileName);
            // Write the pre-roll buffer first. When stereo-only is on, cap to 2 channels —
            // ConvertToTarget sums all source channels so the master mix is captured no matter
            // which input channel(s) it lives on (Zoom L-6 Max routes it past channel 1-2).
            int targetChannels = RecordStereoOnly ? Math.Min(_waveFormat.Channels, 2) : _waveFormat.Channels;
            var targetFormat = new WaveFormat(44100, 16, targetChannels);
            _waveWriter = new WaveFileWriter(filePath, targetFormat);

            if (_circularBuffer != null)
            {
                var preRoll = _circularBuffer.ReadAll();
                if (preRoll.Length > 0)
                {
                    var resampled = ProcessForWriter(preRoll, targetFormat);
                    WriteFloatsAsBytes(resampled, targetFormat);
                }
            }
            _isRecordingToFile = true;
            return filePath;
        }
    }

    public void StopWriting()
    {
        lock (_writerLock)
        {
            _isRecordingToFile = false;
            _waveWriter?.Dispose();
            _waveWriter = null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_waveFormat == null || e.BytesRecorded == 0) return;

        // Convert incoming bytes to float samples
        var floats = ConvertBytesToFloats(e.Buffer, e.BytesRecorded, _waveFormat);

        // Per-channel peak levels for the selected stereo pair (so the meter reflects
        // exactly what would be recorded in stereo-only mode).
        int channels = _waveFormat.Channels;
        int leftCh = Math.Clamp(SelectedChannelPair, 0, channels - 1);
        int rightCh = channels > 1 ? Math.Clamp(SelectedChannelPair + 1, 0, channels - 1) : leftCh;
        float leftPeak = 0, rightPeak = 0;
        int frames = floats.Length / channels;
        for (int f = 0; f < frames; f++)
        {
            int baseIdx = f * channels;
            float l = Math.Abs(floats[baseIdx + leftCh]);
            float r = Math.Abs(floats[baseIdx + rightCh]);
            if (l > leftPeak) leftPeak = l;
            if (r > rightPeak) rightPeak = r;
        }
        LevelChanged?.Invoke(this, new StereoLevel(leftPeak, rightPeak));

        // Always write to circular buffer
        _circularBuffer?.Write(floats, 0, floats.Length);

        // Write to file if recording
        lock (_writerLock)
        {
            if (_isRecordingToFile && _waveWriter != null)
            {
                var targetFormat = _waveWriter.WaveFormat;
                var resampled = ProcessForWriter(floats, targetFormat);
                WriteFloatsAsBytes(resampled, targetFormat);
            }
        }
    }

    // Reduces channels (extracts the selected stereo pair) if needed, then resamples to the
    // writer's target format.
    private float[] ProcessForWriter(float[] floats, WaveFormat targetFormat)
    {
        if (_waveFormat == null) return floats;

        var sourceFormat = _waveFormat;
        var processed = floats;

        if (targetFormat.Channels < sourceFormat.Channels)
        {
            processed = ExtractChannelPair(processed, sourceFormat.Channels, SelectedChannelPair);
            sourceFormat = new WaveFormat(sourceFormat.SampleRate, sourceFormat.BitsPerSample, 2);
        }

        return ConvertToTarget(processed, sourceFormat, targetFormat);
    }

    private void WriteFloatsAsBytes(float[] samples, WaveFormat targetFormat)
    {
        if (_waveWriter == null) return;

        byte[] buffer = new byte[samples.Length * (targetFormat.BitsPerSample / 8)];
        if (targetFormat.BitsPerSample == 16)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                var sample = Math.Clamp(samples[i], -1f, 1f);
                short val = (short)(sample * short.MaxValue);
                buffer[i * 2] = (byte)(val & 0xFF);
                buffer[i * 2 + 1] = (byte)((val >> 8) & 0xFF);
            }
        }
        else if (targetFormat.BitsPerSample == 32)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                BitConverter.TryWriteBytes(buffer.AsSpan(i * 4), samples[i]);
            }
        }

        _waveWriter.Write(buffer, 0, buffer.Length);
    }

    private static float[] ConvertBytesToFloats(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        // WASAPI typically returns WaveFormatExtensible which reports Encoding as Extensible
        // even when the actual data is 32-bit IEEE float. Check BitsPerSample as well.
        if (format.Encoding == WaveFormatEncoding.IeeeFloat ||
            (format.BitsPerSample == 32 && bytesRecorded >= 4))
        {
            int sampleCount = bytesRecorded / 4;
            var floats = new float[sampleCount];
            Buffer.BlockCopy(buffer, 0, floats, 0, sampleCount * 4);
            return floats;
        }
        else if (format.BitsPerSample == 16)
        {
            int sampleCount = bytesRecorded / 2;
            var floats = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(buffer, i * 2);
                floats[i] = sample / 32768f;
            }
            return floats;
        }
        else if (format.BitsPerSample == 24)
        {
            int sampleCount = bytesRecorded / 3;
            var floats = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                int sample = (buffer[i * 3] | (buffer[i * 3 + 1] << 8) | (buffer[i * 3 + 2] << 16));
                if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
                floats[i] = sample / 8388608f;
            }
            return floats;
        }

        return [];
    }

    private static float[] ConvertToTarget(float[] input, WaveFormat source, WaveFormat target)
    {
        if (source.SampleRate == target.SampleRate && source.Channels == target.Channels)
            return input;

        // Simple resampling: if rates differ, do linear interpolation
        double ratio = (double)source.SampleRate / target.SampleRate;
        int sourceChannels = source.Channels;
        int targetChannels = target.Channels;
        int sourceFrames = input.Length / sourceChannels;
        int targetFrames = (int)(sourceFrames / ratio);
        var output = new float[targetFrames * targetChannels];

        for (int i = 0; i < targetFrames; i++)
        {
            double srcPos = i * ratio;
            int srcIdx = (int)srcPos;
            double frac = srcPos - srcIdx;

            for (int ch = 0; ch < targetChannels; ch++)
            {
                int srcCh = ch < sourceChannels ? ch : 0;
                float s1 = srcIdx * sourceChannels + srcCh < input.Length
                    ? input[srcIdx * sourceChannels + srcCh] : 0;
                float s2 = (srcIdx + 1) * sourceChannels + srcCh < input.Length
                    ? input[(srcIdx + 1) * sourceChannels + srcCh] : s1;
                output[i * targetChannels + ch] = (float)(s1 + (s2 - s1) * frac);
            }
        }

        return output;
    }

    // Extracts a stereo pair starting at firstChannel from a multichannel float buffer.
    private static float[] ExtractChannelPair(float[] input, int sourceChannels, int firstChannel)
    {
        int frames = input.Length / sourceChannels;
        var output = new float[frames * 2];
        int ch0 = Math.Clamp(firstChannel, 0, sourceChannels - 1);
        int ch1 = Math.Clamp(firstChannel + 1, 0, sourceChannels - 1);
        for (int f = 0; f < frames; f++)
        {
            int baseIdx = f * sourceChannels;
            output[f * 2] = input[baseIdx + ch0];
            output[f * 2 + 1] = input[baseIdx + ch1];
        }
        return output;
    }

    public static int GetDeviceChannelCount(string? deviceId)
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            MMDevice device;
            if (string.IsNullOrEmpty(deviceId))
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            else if (deviceId.StartsWith("loopback:"))
                device = enumerator.GetDevice(deviceId["loopback:".Length..]);
            else
                device = enumerator.GetDevice(deviceId);
            return device.AudioClient.MixFormat.Channels;
        }
        catch
        {
            return 2;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        StopWriting();
    }

    public void Dispose()
    {
        StopWriting();
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;
    }
}
