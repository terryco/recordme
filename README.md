# RecordMe

A Windows desktop app that **automatically records your audio and MIDI whenever you start
playing** — and stops shortly after you stop. Built for keyboardists and producers who want
every idea captured without reaching for a record button.

Play a note on any connected MIDI device and RecordMe begins capturing both the audio from
your interface and the MIDI performance. Stop playing and, after a short grace period, it
finalizes the take — trims the silence, detects the chords you played, and files it away with
its metadata in a searchable library.

## Features

- **MIDI-triggered recording** — recording starts on the first note-on and ends a configurable
  moment after the last note-off, so you never miss the start of an idea or babysit a button.
- **Pre-roll buffer** — a circular buffer continuously holds the most recent audio, so the
  attack of the very first note is captured even though it occurs *before* the trigger fires.
- **Simultaneous audio + MIDI capture** — WAV (44.1 kHz / 16-bit) plus a Type-1 `.mid` file of
  the exact performance.
- **Auto-trim silence** — leading and trailing silence is removed automatically (with a small
  fade margin so transients aren't clipped). Toggle-able.
- **Chord detection** — the recorded MIDI is analyzed and the most common chords are summarized
  into the recording's notes (e.g. `Chords: C (4x), G7 (2x), Amin (1x)`).
- **Multichannel & stereo-only recording** — works with multichannel interfaces (e.g. a Zoom
  L-6). Record all channels, or pick a single stereo channel pair to capture. A stereo L/R
  level meter reflects exactly what will be recorded.
- **Playback** — recorded takes play back through WASAPI shared mode; multichannel files are
  downmixed to stereo and resampled to the output device's mix format on the fly.
- **Library with metadata** — recordings are stored in SQLite with editable title, instruments,
  patches, key, FX, notes, and a follow-up flag.

## How it works

### Recording lifecycle

`RecordingCoordinator` is a small state machine driven by MIDI activity:

```
        first note-on
 Idle ───────────────▶ Recording
   ▲                      │
   │ tail times out       │ all notes off
   │ (15s, no new notes)  ▼
   └──────────────────  Tailing
            ◀───────────  │
              new note-on │ (cancels tail, back to Recording)
```

- **Idle → Recording**: the first note-on starts the WAV writer (flushing the pre-roll buffer
  first) and the MIDI recorder.
- **Recording → Tailing**: when all notes are released, a 15-second countdown begins.
- **Tailing → Recording**: any new note-on cancels the countdown and resumes the same take.
- **Tailing → Idle**: if the countdown elapses, the take is finalized — audio is trimmed, MIDI
  is written and analyzed for chords, and a `Recording` row is saved.

### Stereo-only / channel pair

Multichannel interfaces expose more than two channels. With **Record stereo only** enabled you
pick a stereo pair (channels 1-2, 3-4, …); only that pair is extracted and written. The level
meter shows the peak of the selected left/right channels so you can confirm the signal before
committing.

## Architecture

MVVM (CommunityToolkit.Mvvm) over a set of focused services. Services are plain classes with no
view dependencies, which keeps the audio/MIDI logic testable.

| Component | Responsibility |
|---|---|
| `Services/AudioCaptureService` | WASAPI capture, pre-roll buffer, WAV writing, channel extraction & resampling, L/R level metering. Falls back to the default device if a saved device is gone. |
| `Services/MidiCaptureService` | Listens to all MIDI inputs, tracks active notes, raises `FirstNoteOn`/`AllNotesOff`/`NoteActivity`, records events and exports a Type-1 `.mid`. |
| `Services/RecordingCoordinator` | The Idle/Recording/Tailing state machine; ties audio + MIDI together and finalizes takes. |
| `Services/CircularAudioBuffer` | Lock-protected ring buffer holding the most recent samples for pre-roll. |
| `Services/WavTrimmer` | Trims leading/trailing silence below a threshold, with a fade margin. |
| `Services/MidiChordAnalyzer` | Clusters near-simultaneous note-ons and matches them against chord templates to summarize a take. |
| `Services/PlaybackService` | WAV playback via `WasapiOut`, with stereo downmix + resample to the device mix format. |
| `Services/MidiPlaybackService` | Plays back the recorded MIDI performance. |
| `Services/AppSettings` | JSON settings in `%LocalAppData%\RecordMe\settings.json`. |
| `Data/AppDbContext` | EF Core (SQLite) context. |
| `Models/Recording` | The recording entity and its metadata. |
| `ViewModels/MainViewModel` | Binds the UI: device selection, monitoring, playback, library CRUD. |
| `Views/MainWindow` | WPF UI (WPF-UI / Fluent styling). |

## Requirements

- Windows
- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- An audio capture device and at least one MIDI input device (for live use)

## Build & run

```powershell
# Build
dotnet build RecordMe/RecordMe.csproj

# Run
dotnet run --project RecordMe
```

By default, recordings are written to `Music\RecordMe`; the location is configurable in-app and
persisted to settings.

## Tests

Deterministic, hardware-independent logic is covered by an xUnit project. (The audio, MIDI, and
WASAPI services require real hardware/COM and are exercised manually.)

```powershell
dotnet test RecordMe.Tests/RecordMe.Tests.csproj
```

Covered:

- `CircularAudioBuffer` — ordering, wrap-around, offsets, clear.
- `WavTrimmer` — silence trimming, signal preservation, all-silence no-op, stereo handling.
- `MidiChordAnalyzer` — major/minor/seventh detection, occurrence counts, `maxChords` limit,
  and empty/missing-input handling.

## Project layout

```
RecordMe.sln
├── RecordMe/                 # WPF app (net9.0-windows, WinExe)
│   ├── App.xaml(.cs)
│   ├── Models/Recording.cs
│   ├── Data/AppDbContext.cs
│   ├── Services/             # audio, MIDI, recording, playback, analysis
│   ├── ViewModels/MainViewModel.cs
│   ├── Views/MainWindow.xaml(.cs)
│   └── Converters/
└── RecordMe.Tests/           # xUnit tests for the hardware-independent logic
```
