using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using RecordMe.Data;
using RecordMe.Models;
using RecordMe.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace RecordMe.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AudioCaptureService _audioCaptureService;
    private readonly MidiCaptureService _midiCaptureService;
    private readonly PlaybackService _playbackService;
    private readonly MidiPlaybackService _midiPlaybackService;
    private RecordingCoordinator? _coordinator;
    private readonly Func<AppDbContext> _dbFactory;
    private readonly AppSettings _settings;
    private string _outputDir;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty] private string _statusText = "Ready — Waiting for MIDI input";
    [ObservableProperty] private RecordingState _recordingState = RecordingState.Idle;
    [ObservableProperty] private int _tailCountdown;
    [ObservableProperty] private float _audioLevel;
    [ObservableProperty] private int _activeNoteCount;
    [ObservableProperty] private bool _isMonitoring;
    [ObservableProperty] private bool _autoTrimSilence = true;
    [ObservableProperty] private bool _isPlayingAudio;
    [ObservableProperty] private bool _isPlayingMidi;
    [ObservableProperty] private string _playbackPosition = "00:00";
    [ObservableProperty] private string _playbackDuration = "00:00";
    [ObservableProperty] private double _playbackProgress;
    [ObservableProperty] private Recording? _selectedRecording;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _filterFollowUp;
    [ObservableProperty] private string _recordingLocation = string.Empty;
    [ObservableProperty] private AudioDeviceInfo? _selectedAudioDevice;

    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; } = [];

    public ObservableCollection<Recording> Recordings { get; } = [];

    // Detail editing fields
    [ObservableProperty] private string _editTitle = string.Empty;
    [ObservableProperty] private string _editInstruments = string.Empty;
    [ObservableProperty] private string _editPatches = string.Empty;
    [ObservableProperty] private string _editKey = string.Empty;
    [ObservableProperty] private string _editFxInfo = string.Empty;
    [ObservableProperty] private string _editNotes = string.Empty;
    [ObservableProperty] private bool _editFollowUp;

    // MIDI channel remapping (16 channels)
    public ObservableCollection<ChannelMapping> ChannelMappings { get; } = [];

    public MainViewModel(Func<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
        _dispatcher = Application.Current.Dispatcher;

        _audioCaptureService = new AudioCaptureService();
        _midiCaptureService = new MidiCaptureService();
        _playbackService = new PlaybackService();
        _midiPlaybackService = new MidiPlaybackService();

        _settings = AppSettings.Load();
        _outputDir = _settings.RecordingOutputDir;
        RecordingLocation = _outputDir;
        Directory.CreateDirectory(_outputDir);

        // Initialize 16 MIDI channel mappings
        for (int i = 0; i < 16; i++)
        {
            ChannelMappings.Add(new ChannelMapping { FromChannel = i + 1, ToChannel = i + 1 });
        }

        RefreshAudioDevices();

        _audioCaptureService.LevelChanged += (_, level) =>
            _dispatcher.BeginInvoke(() => AudioLevel = level);

        _midiCaptureService.NoteActivity += (_, e) =>
            _dispatcher.BeginInvoke(() => ActiveNoteCount = _midiCaptureService.ActiveNoteCount);

        _playbackService.PositionChanged += (_, pos) =>
            _dispatcher.BeginInvoke(() =>
            {
                PlaybackPosition = pos.ToString(@"mm\:ss");
                PlaybackDuration = _playbackService.TotalDuration.ToString(@"mm\:ss");
                if (_playbackService.TotalDuration.TotalSeconds > 0)
                    PlaybackProgress = pos.TotalSeconds / _playbackService.TotalDuration.TotalSeconds;
            });

        _playbackService.PlaybackStopped += (_, _) =>
            _dispatcher.BeginInvoke(() => IsPlayingAudio = false);

        _playbackService.PlaybackError += (_, msg) =>
            _dispatcher.BeginInvoke(() => { StatusText = $"Playback error: {msg}"; IsPlayingAudio = false; });

        _midiPlaybackService.PlaybackStopped += (_, _) =>
            _dispatcher.BeginInvoke(() => IsPlayingMidi = false);

        _midiPlaybackService.ProgressChanged += (_, progress) =>
            _dispatcher.BeginInvoke(() => PlaybackProgress = progress);
    }

    public async Task InitializeAsync()
    {
        using var db = _dbFactory();
        await db.Database.EnsureCreatedAsync();
        await ValidateIntegrityAsync();
        await LoadRecordingsAsync();
    }

    private async Task ValidateIntegrityAsync()
    {
        using var db = _dbFactory();
        var allRecordings = await db.Recordings.ToListAsync();

        // Find DB entries with missing files
        var brokenRecordings = allRecordings.Where(r =>
            (!string.IsNullOrEmpty(r.WavFilePath) && !File.Exists(r.WavFilePath)) ||
            (!string.IsNullOrEmpty(r.MidiFilePath) && !File.Exists(r.MidiFilePath))
        ).ToList();

        // Find orphaned files (files with no DB entry)
        var knownWavs = new HashSet<string>(allRecordings.Select(r => r.WavFilePath), StringComparer.OrdinalIgnoreCase);
        var knownMids = new HashSet<string>(allRecordings.Select(r => r.MidiFilePath), StringComparer.OrdinalIgnoreCase);

        var orphanedFiles = new List<string>();
        if (Directory.Exists(_outputDir))
        {
            foreach (var file in Directory.GetFiles(_outputDir))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".wav" && !knownWavs.Contains(file))
                    orphanedFiles.Add(file);
                else if (ext == ".mid" && !knownMids.Contains(file))
                    orphanedFiles.Add(file);
            }
        }

        if (brokenRecordings.Count == 0 && orphanedFiles.Count == 0)
            return;

        var message = "";

        if (brokenRecordings.Count > 0)
        {
            message += $"{brokenRecordings.Count} recording(s) have missing files:\n";
            foreach (var r in brokenRecordings.Take(5))
                message += $"  - {r.Title}\n";
            if (brokenRecordings.Count > 5)
                message += $"  ... and {brokenRecordings.Count - 5} more\n";
            message += "\n";
        }

        if (orphanedFiles.Count > 0)
        {
            message += $"{orphanedFiles.Count} file(s) have no database entry:\n";
            foreach (var f in orphanedFiles.Take(5))
                message += $"  - {Path.GetFileName(f)}\n";
            if (orphanedFiles.Count > 5)
                message += $"  ... and {orphanedFiles.Count - 5} more\n";
            message += "\n";
        }

        message += "Remove these invalid references?";

        var result = MessageBox.Show(message, "RecordMe — Integrity Check",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        if (brokenRecordings.Count > 0)
        {
            using var dbDel = _dbFactory();
            foreach (var r in brokenRecordings)
            {
                var entity = await dbDel.Recordings.FindAsync(r.Id);
                if (entity != null)
                    dbDel.Recordings.Remove(entity);
            }
            await dbDel.SaveChangesAsync();
        }

        foreach (var file in orphanedFiles)
        {
            try { File.Delete(file); } catch { }
        }

        StatusText = $"Cleaned up {brokenRecordings.Count} broken records and {orphanedFiles.Count} orphaned files";
    }

    [RelayCommand]
    private void ToggleMonitoring()
    {
        if (IsMonitoring)
        {
            StopMonitoring();
        }
        else
        {
            StartMonitoring();
        }
    }

    [RelayCommand]
    private void ForceFinalize()
    {
        if (_coordinator == null) return;

        if (RecordingState == RecordingState.Idle)
        {
            _coordinator.ForceStartRecording();
        }
        else
        {
            _coordinator.ForceFinalize();
        }
    }

    [RelayCommand]
    private void BrowseRecordingLocation()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Recording Location",
            InitialDirectory = _outputDir
        };

        if (dialog.ShowDialog() == true)
        {
            _outputDir = dialog.FolderName;
            RecordingLocation = _outputDir;
            _settings.RecordingOutputDir = _outputDir;
            _settings.Save();
            Directory.CreateDirectory(_outputDir);
            StatusText = $"Recording location: {_outputDir}";
        }
    }

    [RelayCommand]
    private void RefreshAudioDevices()
    {
        AudioDevices.Clear();
        foreach (var device in AudioCaptureService.GetCaptureDevices())
        {
            AudioDevices.Add(device);
        }
        if (SelectedAudioDevice == null && AudioDevices.Count > 0)
            SelectedAudioDevice = AudioDevices[0];
    }

    private void StartMonitoring()
    {
        _coordinator = new RecordingCoordinator(_audioCaptureService, _midiCaptureService, _outputDir);
        _coordinator.AudioDeviceId = SelectedAudioDevice?.Id;
        _coordinator.AutoTrimSilence = AutoTrimSilence;

        _coordinator.StateChanged += (_, state) =>
            _dispatcher.BeginInvoke(() =>
            {
                RecordingState = state;
                StatusText = state switch
                {
                    RecordingState.Idle => "Monitoring — Waiting for MIDI input",
                    RecordingState.Recording => "Recording...",
                    RecordingState.Tailing => $"Tail recording — stopping in {TailCountdown}s",
                    _ => StatusText
                };
            });

        _coordinator.TailCountdownTick += (_, seconds) =>
            _dispatcher.BeginInvoke(() =>
            {
                TailCountdown = seconds;
                StatusText = $"Tail recording — stopping in {seconds}s";
            });

        _coordinator.RecordingCompleted += async (_, recording) =>
        {
            await _dispatcher.InvokeAsync(async () =>
            {
                using var db = _dbFactory();
                db.Recordings.Add(recording);
                await db.SaveChangesAsync();
                Recordings.Insert(0, recording);
                StatusText = $"Recording saved: {recording.Title}";
            });
        };

        _coordinator.Start();
        IsMonitoring = true;
        StatusText = "Monitoring — Waiting for MIDI input";
    }

    private void StopMonitoring()
    {
        _coordinator?.Stop();
        _coordinator?.Dispose();
        _coordinator = null;
        IsMonitoring = false;
        StatusText = "Ready — Monitoring stopped";
        RecordingState = RecordingState.Idle;
    }

    [RelayCommand]
    private void PlayAudio()
    {
        if (SelectedRecording == null || string.IsNullOrEmpty(SelectedRecording.WavFilePath)) return;
        if (!File.Exists(SelectedRecording.WavFilePath))
        {
            StatusText = "WAV file not found";
            return;
        }

        if (IsPlayingAudio)
        {
            _playbackService.Stop();
            IsPlayingAudio = false;
        }
        else
        {
            _playbackService.Play(SelectedRecording.WavFilePath);
            IsPlayingAudio = true;
        }
    }

    [RelayCommand]
    private void StopAudio()
    {
        _playbackService.Stop();
        IsPlayingAudio = false;
    }

    [RelayCommand]
    private void PlayMidi()
    {
        if (SelectedRecording == null || string.IsNullOrEmpty(SelectedRecording.MidiFilePath)) return;
        if (!File.Exists(SelectedRecording.MidiFilePath))
        {
            StatusText = "MIDI file not found";
            return;
        }

        if (IsPlayingMidi)
        {
            _midiPlaybackService.Stop();
            IsPlayingMidi = false;
        }
        else
        {
            // Apply channel mappings
            _midiPlaybackService.ClearChannelMappings();
            foreach (var mapping in ChannelMappings)
            {
                if (mapping.FromChannel != mapping.ToChannel)
                {
                    _midiPlaybackService.SetChannelMapping(mapping.FromChannel - 1, mapping.ToChannel - 1);
                }
            }

            _midiPlaybackService.Play(SelectedRecording.MidiFilePath);
            IsPlayingMidi = true;
        }
    }

    [RelayCommand]
    private void StopMidi()
    {
        _midiPlaybackService.Stop();
        IsPlayingMidi = false;
    }

    [RelayCommand]
    private Task RecaptureAudio()
    {
        if (SelectedRecording == null || string.IsNullOrEmpty(SelectedRecording.MidiFilePath)) return Task.CompletedTask;
        if (!IsMonitoring)
        {
            StatusText = "Start monitoring first to recapture audio";
            return Task.CompletedTask;
        }

        StatusText = "Recapturing — Playing MIDI and recording audio...";

        // Apply channel mappings and play MIDI — the coordinator will capture the audio
        _midiPlaybackService.ClearChannelMappings();
        foreach (var mapping in ChannelMappings)
        {
            if (mapping.FromChannel != mapping.ToChannel)
            {
                _midiPlaybackService.SetChannelMapping(mapping.FromChannel - 1, mapping.ToChannel - 1);
            }
        }

        _midiPlaybackService.Play(SelectedRecording.MidiFilePath);
        IsPlayingMidi = true;
        return Task.CompletedTask;
    }

    partial void OnSelectedRecordingChanged(Recording? value)
    {
        if (value == null) return;
        EditTitle = value.Title;
        EditInstruments = value.Instruments;
        EditPatches = value.Patches;
        EditKey = value.Key;
        EditFxInfo = value.FxInfo;
        EditNotes = value.Notes;
        EditFollowUp = value.FollowUp;
    }

    partial void OnAutoTrimSilenceChanged(bool value)
    {
        if (_coordinator != null)
            _coordinator.AutoTrimSilence = value;
    }

    [RelayCommand]
    private async Task SaveRecording()
    {
        if (SelectedRecording == null) return;

        SelectedRecording.Title = EditTitle;
        SelectedRecording.Instruments = EditInstruments;
        SelectedRecording.Patches = EditPatches;
        SelectedRecording.Key = EditKey;
        SelectedRecording.FxInfo = EditFxInfo;
        SelectedRecording.Notes = EditNotes;
        SelectedRecording.FollowUp = EditFollowUp;

        using var db = _dbFactory();
        db.Recordings.Update(SelectedRecording);
        await db.SaveChangesAsync();

        StatusText = "Recording saved";

        // Refresh the list item
        var idx = Recordings.IndexOf(SelectedRecording);
        if (idx >= 0)
        {
            Recordings[idx] = SelectedRecording;
            SelectedRecording = Recordings[idx];
        }
    }

    [RelayCommand]
    private async Task DeleteRecording()
    {
        if (SelectedRecording == null) return;

        var result = MessageBox.Show(
            $"Delete '{SelectedRecording.Title}'?\nThis will also delete the audio and MIDI files.",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var wavPath = SelectedRecording.WavFilePath;
        var midPath = SelectedRecording.MidiFilePath;
        var id = SelectedRecording.Id;

        // Delete from database
        using var db = _dbFactory();
        var entity = await db.Recordings.FindAsync(id);
        if (entity != null)
        {
            db.Recordings.Remove(entity);
            await db.SaveChangesAsync();
        }

        // Delete files
        try
        {
            if (!string.IsNullOrEmpty(wavPath) && File.Exists(wavPath))
                File.Delete(wavPath);
            if (!string.IsNullOrEmpty(midPath) && File.Exists(midPath))
                File.Delete(midPath);
        }
        catch { }

        Recordings.Remove(SelectedRecording);
        SelectedRecording = Recordings.FirstOrDefault();
        StatusText = "Recording deleted";
    }

    [RelayCommand]
    private async Task Search()
    {
        await LoadRecordingsAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = LoadRecordingsAsync();
    }

    partial void OnFilterFollowUpChanged(bool value)
    {
        _ = LoadRecordingsAsync();
    }

    private async Task LoadRecordingsAsync()
    {
        using var db = _dbFactory();
        var query = db.Recordings.AsQueryable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.ToLower();
            query = query.Where(r =>
                r.Title.ToLower().Contains(search) ||
                r.Instruments.ToLower().Contains(search) ||
                r.Patches.ToLower().Contains(search) ||
                r.Key.ToLower().Contains(search) ||
                r.Notes.ToLower().Contains(search));
        }

        if (FilterFollowUp)
        {
            query = query.Where(r => r.FollowUp);
        }

        var recordings = await query.OrderByDescending(r => r.CreatedAt).ToListAsync();

        Recordings.Clear();
        foreach (var r in recordings)
        {
            Recordings.Add(r);
        }
    }

    public void Dispose()
    {
        StopMonitoring();
        _playbackService.Dispose();
        _midiPlaybackService.Dispose();
        _audioCaptureService.Dispose();
        _midiCaptureService.Dispose();
    }
}

public partial class ChannelMapping : ObservableObject
{
    [ObservableProperty] private int _fromChannel;
    [ObservableProperty] private int _toChannel;
}
