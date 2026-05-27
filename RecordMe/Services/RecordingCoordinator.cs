using RecordMe.Data;
using RecordMe.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RecordMe.Services;

public enum RecordingState
{
    Idle,
    Recording,
    Tailing
}

public class RecordingCoordinator : IDisposable
{
    private readonly AudioCaptureService _audio;
    private readonly MidiCaptureService _midi;
    private readonly string _outputDir;

    private RecordingState _state = RecordingState.Idle;
    private readonly object _stateLock = new();
    private CancellationTokenSource? _tailCts;
    private string _currentWavPath = string.Empty;
    private string _currentMidiPath = string.Empty;
    private DateTime _recordingStartTime;
    private bool _finalizing;

    private const int TailTimeoutSeconds = 15;

    public bool AutoTrimSilence { get; set; } = true;
    public bool RecordStereoOnly
    {
        get => _audio.RecordStereoOnly;
        set => _audio.RecordStereoOnly = value;
    }
    public int SelectedChannelPair
    {
        get => _audio.SelectedChannelPair;
        set => _audio.SelectedChannelPair = value;
    }
    public RecordingState State => _state;

    public event EventHandler<RecordingState>? StateChanged;
    public event EventHandler<Recording>? RecordingCompleted;
    public event EventHandler<int>? TailCountdownTick;
    public event EventHandler<string>? StatusMessage;

    public RecordingCoordinator(AudioCaptureService audio, MidiCaptureService midi, string outputDir)
    {
        _audio = audio;
        _midi = midi;
        _outputDir = outputDir;

        Directory.CreateDirectory(_outputDir);

        _midi.FirstNoteOn += OnFirstNoteOn;
        _midi.AllNotesOff += OnAllNotesOff;
        _midi.NoteActivity += OnNoteActivity;
    }

    public string? AudioDeviceId { get; set; }

    public int Start()
    {
        _audio.StartCapturing(AudioDeviceId);
        return _midi.StartListening();
    }

    public void ForceStartRecording()
    {
        lock (_stateLock)
        {
            if (_state == RecordingState.Idle)
            {
                StartRecording();
            }
        }
    }

    public void ForceFinalize()
    {
        lock (_stateLock)
        {
            if (_state == RecordingState.Tailing || _state == RecordingState.Recording)
            {
                CancelTail();
                FinalizeRecording();
            }
        }
    }

    public void Stop()
    {
        if (_state != RecordingState.Idle)
            FinalizeRecording();

        _audio.StopCapturing();
        _midi.StopListening();
    }

    private void OnFirstNoteOn(object? sender, EventArgs e)
    {
        lock (_stateLock)
        {
            if (_state == RecordingState.Idle)
            {
                StartRecording();
            }
            else if (_state == RecordingState.Tailing)
            {
                CancelTail();
                SetState(RecordingState.Recording);
            }
        }
    }

    private void OnNoteActivity(object? sender, MidiNoteEventArgs e)
    {
        lock (_stateLock)
        {
            if (_state == RecordingState.Tailing && e.IsNoteOn)
            {
                CancelTail();
                SetState(RecordingState.Recording);
            }
        }
    }

    private void OnAllNotesOff(object? sender, EventArgs e)
    {
        lock (_stateLock)
        {
            if (_state == RecordingState.Recording)
            {
                StartTailing();
            }
        }
    }

    private void StartRecording()
    {
        _recordingStartTime = DateTime.UtcNow;
        _currentWavPath = _audio.StartWriting(_outputDir);
        _midi.StartRecording();
        SetState(RecordingState.Recording);
    }

    private void StartTailing()
    {
        SetState(RecordingState.Tailing);
        CancelTail();
        _tailCts?.Dispose();
        _tailCts = new CancellationTokenSource();
        var token = _tailCts.Token;

        Task.Run(async () =>
        {
            try
            {
                for (int i = TailTimeoutSeconds; i > 0; i--)
                {
                    TailCountdownTick?.Invoke(this, i);
                    await Task.Delay(1000, token);
                }

                if (!token.IsCancellationRequested)
                {
                    FinalizeRecording();
                }
            }
            catch (OperationCanceledException)
            {
                // Tailing was cancelled because new notes came in
            }
        }, token);
    }

    private void FinalizeRecording()
    {
        lock (_stateLock)
        {
            if (_state == RecordingState.Idle || _finalizing) return;
            _finalizing = true;
        }

        _audio.StopWriting();
        _currentMidiPath = _midi.StopRecording(_outputDir);

        if (AutoTrimSilence && !string.IsNullOrEmpty(_currentWavPath))
        {
            try
            {
                WavTrimmer.TrimSilence(_currentWavPath);
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke(this, $"Silence trim failed: {ex.Message}");
            }
        }

        var duration = DateTime.UtcNow - _recordingStartTime;

        var chordSummary = MidiChordAnalyzer.Analyze(_currentMidiPath);

        var recording = new Recording
        {
            Title = $"Recording {_recordingStartTime.ToLocalTime():g}",
            CreatedAt = _recordingStartTime,
            Duration = duration,
            WavFilePath = _currentWavPath,
            MidiFilePath = _currentMidiPath,
            Notes = chordSummary,
            SampleRate = _audio.WaveFormat?.SampleRate ?? 44100,
            Channels = _audio.WaveFormat?.Channels ?? 2,
            BitsPerSample = 16
        };

        lock (_stateLock) { _finalizing = false; }
        SetState(RecordingState.Idle);
        RecordingCompleted?.Invoke(this, recording);
    }

    private void CancelTail()
    {
        try { _tailCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void SetState(RecordingState state)
    {
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        Stop();
        CancelTail();
        _tailCts?.Dispose();

        _midi.FirstNoteOn -= OnFirstNoteOn;
        _midi.AllNotesOff -= OnAllNotesOff;
        _midi.NoteActivity -= OnNoteActivity;
    }
}
