using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RecordMe.Services;

public class PlaybackService : IDisposable
{
    private IWavePlayer? _waveOut;
    private WaveStream? _reader;
    private IDisposable? _resampler;
    private readonly object _lock = new();

    static PlaybackService()
    {
        try { MediaFoundationApi.Startup(); } catch { }
    }

    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;
    public TimeSpan CurrentPosition => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan TotalDuration => _reader?.TotalTime ?? TimeSpan.Zero;

    public event EventHandler? PlaybackStopped;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<string>? PlaybackError;

    private Timer? _positionTimer;

    public void Play(string wavFilePath)
    {
        Stop();

        try
        {
            // Try AudioFileReader first (handles most formats), fall back to WaveFileReader
            try
            {
                _reader = new AudioFileReader(wavFilePath);
            }
            catch
            {
                _reader = new WaveFileReader(wavFilePath);
            }

            // Get device's mix format — WASAPI shared mode requires the input to match it.
            var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            var deviceFormat = device.AudioClient.MixFormat;

            // AudioFileReader is already an ISampleProvider (32-bit float). Fall back to
            // ToSampleProvider() for plain WaveFileReader. For multichannel files
            // (e.g. 14-ch from a Zoom L-6 Max), downmix to stereo first —
            // MediaFoundationResampler can't reliably handle very high channel counts.
            ISampleProvider sampleSource = _reader as ISampleProvider ?? _reader.ToSampleProvider();
            if (sampleSource.WaveFormat.Channels > 2)
            {
                sampleSource = new StereoDownmixSampleProvider(sampleSource);
            }

            IWaveProvider source = sampleSource.ToWaveProvider();
            if (!FormatsMatch(source.WaveFormat, deviceFormat))
            {
                var resampler = new MediaFoundationResampler(source, deviceFormat) { ResamplerQuality = 60 };
                _resampler = resampler;
                source = resampler;
            }

            _waveOut = new WasapiOut(AudioClientShareMode.Shared, 100);
            _waveOut.Init(source);
            _waveOut.PlaybackStopped += OnWaveOutStopped;

            _positionTimer = new Timer(_ =>
            {
                try
                {
                    if (_reader != null)
                        PositionChanged?.Invoke(this, _reader.CurrentTime);
                }
                catch { }
            }, null, 0, 100);

            _waveOut.Play();
        }
        catch (Exception ex)
        {
            PlaybackError?.Invoke(this, ex.Message);
            Stop();
        }
    }

    private static bool FormatsMatch(WaveFormat a, WaveFormat b)
    {
        return a.SampleRate == b.SampleRate
            && a.Channels == b.Channels
            && a.BitsPerSample == b.BitsPerSample
            && a.Encoding == b.Encoding;
    }

    private void OnWaveOutStopped(object? sender, StoppedEventArgs e)
    {
        IWavePlayer? waveOut;
        WaveStream? reader;
        IDisposable? resampler;
        Timer? timer;

        lock (_lock)
        {
            timer = _positionTimer;
            _positionTimer = null;
            waveOut = _waveOut;
            _waveOut = null;
            reader = _reader;
            _reader = null;
            resampler = _resampler;
            _resampler = null;
        }

        timer?.Dispose();

        Task.Run(() =>
        {
            resampler?.Dispose();
            reader?.Dispose();
            waveOut?.Dispose();
        });

        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        _waveOut?.Pause();
    }

    public void Resume()
    {
        _waveOut?.Play();
    }

    public void Stop()
    {
        IWavePlayer? waveOut;
        WaveStream? reader;
        IDisposable? resampler;
        Timer? timer;

        lock (_lock)
        {
            timer = _positionTimer;
            _positionTimer = null;
            waveOut = _waveOut;
            _waveOut = null;
            reader = _reader;
            _reader = null;
            resampler = _resampler;
            _resampler = null;
        }

        timer?.Dispose();

        if (waveOut != null)
        {
            waveOut.PlaybackStopped -= OnWaveOutStopped;
            try { waveOut.Stop(); } catch { }
            waveOut.Dispose();
        }

        resampler?.Dispose();
        reader?.Dispose();
    }

    public void Seek(TimeSpan position)
    {
        if (_reader != null)
            _reader.CurrentTime = position;
    }

    public void Dispose()
    {
        Stop();
    }
}

// Sums all source channels into a stereo pair. Each output channel receives the average
// of all source channels — this preserves any signal regardless of which channel(s) it's on.
internal class StereoDownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceBuffer = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public StereoDownmixSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int targetFrames = count / 2;
        int sourceSamplesNeeded = targetFrames * _sourceChannels;

        if (_sourceBuffer.Length < sourceSamplesNeeded)
            _sourceBuffer = new float[sourceSamplesNeeded];

        int read = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        int framesRead = read / _sourceChannels;

        float scale = 1f / _sourceChannels;
        for (int frame = 0; frame < framesRead; frame++)
        {
            float sum = 0f;
            int baseIdx = frame * _sourceChannels;
            for (int ch = 0; ch < _sourceChannels; ch++)
                sum += _sourceBuffer[baseIdx + ch];

            float mono = sum * scale;
            buffer[offset + frame * 2] = mono;
            buffer[offset + frame * 2 + 1] = mono;
        }

        return framesRead * 2;
    }
}
