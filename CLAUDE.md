# CLAUDE.md

Guidance for working in this repository.

## What this is

RecordMe — a Windows WPF desktop app (.NET 9, `WinExe`) that auto-records audio + MIDI,
triggered by MIDI input. See `README.md` for the full feature/architecture overview.

## Commands

```powershell
dotnet build RecordMe/RecordMe.csproj          # build the app
dotnet run   --project RecordMe                # run the app (launches a window)
dotnet test  RecordMe.Tests/RecordMe.Tests.csproj   # run the test suite
dotnet build RecordMe.sln                      # build everything
```

The app is a GUI; `dotnet run` opens a window rather than printing to the console.

## Architecture (one-liners)

- `Services/RecordingCoordinator` — Idle → Recording → Tailing state machine driven by MIDI
  events; the heart of the recording flow.
- `Services/AudioCaptureService` — WASAPI capture, pre-roll ring buffer, WAV writing, channel
  extraction/resampling, L/R metering.
- `Services/MidiCaptureService` — MIDI input listening + `.mid` export; raises the note events
  the coordinator reacts to.
- `Services/{PlaybackService,MidiPlaybackService}` — playback of WAV / MIDI.
- `Services/{WavTrimmer,MidiChordAnalyzer,CircularAudioBuffer,AppSettings}` — pure-ish helpers.
- `ViewModels/MainViewModel` — MVVM glue (CommunityToolkit.Mvvm); `Views/MainWindow.xaml` — UI.
- `Data/AppDbContext` + `Models/Recording` — EF Core / SQLite persistence.

## Conventions & notes

- MVVM: keep audio/MIDI logic in services (no view dependencies) so it stays testable.
- Tests live in `RecordMe.Tests` (xUnit) and cover only hardware-independent logic
  (`CircularAudioBuffer`, `WavTrimmer`, `MidiChordAnalyzer`). Anything touching WASAPI/MIDI
  devices or COM (`AudioCaptureService`, `MidiCaptureService`, `PlaybackService`) needs real
  hardware and is verified manually.
- `AppSettings` reads/writes the real `%LocalAppData%\RecordMe\settings.json` — don't unit-test
  it against that path or it will clobber local settings.
- Settings, recordings, and the SQLite DB live in the user profile, not the repo.
