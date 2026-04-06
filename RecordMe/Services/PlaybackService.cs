using NAudio.Wave;
using System;
using System.Threading;

namespace RecordMe.Services;

public class PlaybackService : IDisposable
{
    private WaveOutEvent? _waveOut;
    private WaveStream? _reader;

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

            _waveOut = new WaveOutEvent();

            // If the reader format isn't directly playable, convert it
            if (_reader.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat &&
                _reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
            {
                var converted = new WaveFormatConversionStream(
                    new WaveFormat(_reader.WaveFormat.SampleRate, 16, _reader.WaveFormat.Channels),
                    _reader);
                _waveOut.Init(converted);
            }
            else
            {
                _waveOut.Init(_reader);
            }

            _waveOut.PlaybackStopped += (s, e) =>
            {
                _positionTimer?.Dispose();
                PlaybackStopped?.Invoke(this, EventArgs.Empty);
            };

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
        _positionTimer?.Dispose();
        _positionTimer = null;
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _reader?.Dispose();
        _reader = null;
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
