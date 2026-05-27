using NAudio.Midi;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RecordMe.Services;

public class MidiNoteEventArgs : EventArgs
{
    public bool IsNoteOn { get; init; }
    public int NoteNumber { get; init; }
    public int Channel { get; init; }
    public int Velocity { get; init; }
}

public class MidiCaptureService : IDisposable
{
    private readonly List<MidiIn> _midiInputs = [];
    private readonly ConcurrentDictionary<int, HashSet<int>> _activeNotes = new(); // channel -> note numbers
    private readonly List<MidiEventRecord> _recordedEvents = [];
    private readonly object _recordLock = new();
    private bool _isRecording;
    private long _recordingStartTick;
    private DateTime _recordingStartTime;

    public bool HasActiveNotes => _activeNotes.Values.Any(set => set.Count > 0);
    public int ActiveNoteCount => _activeNotes.Values.Sum(set => set.Count);
    public long MessageCount { get; private set; }

    public event EventHandler<MidiNoteEventArgs>? NoteActivity;
    public event EventHandler? AllNotesOff;
    public event EventHandler? FirstNoteOn;

    public int StartListening()
    {
        StopListening();

        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
        {
            try
            {
                var midiIn = new MidiIn(i);
                midiIn.MessageReceived += OnMidiMessageReceived;
                midiIn.ErrorReceived += OnMidiError;
                midiIn.Start();
                _midiInputs.Add(midiIn);
            }
            catch
            {
                // Device may be in use
            }
        }

        return _midiInputs.Count;
    }

    public void StopListening()
    {
        foreach (var input in _midiInputs)
        {
            try
            {
                input.Stop();
                input.Dispose();
            }
            catch { }
        }
        _midiInputs.Clear();
        _activeNotes.Clear();
    }

    public void StartRecording()
    {
        lock (_recordLock)
        {
            _recordedEvents.Clear();
            _recordingStartTick = Environment.TickCount64;
            _recordingStartTime = DateTime.UtcNow;
            _isRecording = true;
        }
    }

    public string StopRecording(string outputDir)
    {
        lock (_recordLock)
        {
            _isRecording = false;

            if (_recordedEvents.Count == 0)
                return string.Empty;

            var fileName = $"recording_{_recordingStartTime.ToLocalTime():yyyyMMdd_HHmmss}.mid";
            var filePath = Path.Combine(outputDir, fileName);
            WriteMidiFile(filePath);
            return filePath;
        }
    }

    private void OnMidiMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        MessageCount++;
        var message = e.MidiEvent;

        // Record all events if recording (except timing/active sensing)
        if (message.CommandCode != MidiCommandCode.TimingClock &&
            message.CommandCode != MidiCommandCode.AutoSensing)
        {
            lock (_recordLock)
            {
                if (_isRecording)
                {
                    _recordedEvents.Add(new MidiEventRecord
                    {
                        TimestampMs = Environment.TickCount64 - _recordingStartTick,
                        CommandCode = message.CommandCode,
                        Channel = message.Channel,
                        Data1 = (message is NoteEvent ne1) ? ne1.NoteNumber : 0,
                        Data2 = (message is NoteEvent ne2) ? ne2.Velocity : 0,
                        RawMessage = e.RawMessage
                    });
                }
            }
        }

        // Track note on/off for triggering
        if (message is NoteOnEvent noteOn)
        {
            if (noteOn.Velocity > 0)
            {
                var notes = _activeNotes.GetOrAdd(noteOn.Channel, _ => []);
                bool wasEmpty = !HasActiveNotes;
                lock (notes) { notes.Add(noteOn.NoteNumber); }

                NoteActivity?.Invoke(this, new MidiNoteEventArgs
                {
                    IsNoteOn = true,
                    NoteNumber = noteOn.NoteNumber,
                    Channel = noteOn.Channel,
                    Velocity = noteOn.Velocity
                });

                if (wasEmpty)
                    FirstNoteOn?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // Note on with velocity 0 = note off
                HandleNoteOff(noteOn.Channel, noteOn.NoteNumber);
            }
        }
        else if (message is NoteEvent noteOff && message.CommandCode == MidiCommandCode.NoteOff)
        {
            HandleNoteOff(noteOff.Channel, noteOff.NoteNumber);
        }
    }

    private void HandleNoteOff(int channel, int noteNumber)
    {
        if (_activeNotes.TryGetValue(channel, out var notes))
        {
            lock (notes) { notes.Remove(noteNumber); }
        }

        NoteActivity?.Invoke(this, new MidiNoteEventArgs
        {
            IsNoteOn = false,
            NoteNumber = noteNumber,
            Channel = channel,
            Velocity = 0
        });

        if (!HasActiveNotes)
            AllNotesOff?.Invoke(this, EventArgs.Empty);
    }

    private void WriteMidiFile(string filePath)
    {
        var collection = new MidiEventCollection(1, 480); // Type 1, 480 ticks per quarter

        // Track 0: tempo track
        collection.AddTrack();
        collection.AddEvent(new TempoEvent(500000, 0), 0); // 120 BPM default
        collection.AddEvent(new MetaEvent(MetaEventType.EndTrack, 0, 0), 0);

        // Track 1: all recorded notes
        collection.AddTrack();

        double msPerTick = 500000.0 / 480.0 / 1000.0; // ms per tick at 120 BPM

        foreach (var evt in _recordedEvents)
        {
            long absoluteTick = (long)(evt.TimestampMs / msPerTick);

            if (evt.CommandCode == MidiCommandCode.NoteOn || evt.CommandCode == MidiCommandCode.NoteOff)
            {
                var midiEvent = new NoteOnEvent(absoluteTick, evt.Channel, evt.Data1, evt.Data2,
                    evt.CommandCode == MidiCommandCode.NoteOff ? 0 : 1);
                if (evt.CommandCode == MidiCommandCode.NoteOff)
                {
                    var noteOffEvent = new NoteEvent(absoluteTick, evt.Channel, MidiCommandCode.NoteOff, evt.Data1, 0);
                    collection.AddEvent(noteOffEvent, 1);
                }
                else
                {
                    collection.AddEvent(midiEvent, 1);
                }
            }
            else if (evt.CommandCode == MidiCommandCode.ControlChange)
            {
                var cc = new ControlChangeEvent(absoluteTick, evt.Channel,
                    (MidiController)evt.Data1, evt.Data2);
                collection.AddEvent(cc, 1);
            }
            else if (evt.CommandCode == MidiCommandCode.PitchWheelChange)
            {
                var pw = new PitchWheelChangeEvent(absoluteTick, evt.Channel, evt.Data1 | (evt.Data2 << 7));
                collection.AddEvent(pw, 1);
            }
        }

        // End track
        long lastTick = _recordedEvents.Count > 0
            ? (long)(_recordedEvents[^1].TimestampMs / msPerTick) + 1
            : 1;
        collection.AddEvent(new MetaEvent(MetaEventType.EndTrack, 0, lastTick), 1);

        collection.PrepareForExport();
        MidiFile.Export(filePath, collection);
    }

    private void OnMidiError(object? sender, MidiInMessageEventArgs e) { }

    public void Dispose()
    {
        StopListening();
    }

    private class MidiEventRecord
    {
        public long TimestampMs { get; init; }
        public MidiCommandCode CommandCode { get; init; }
        public int Channel { get; init; }
        public int Data1 { get; init; }
        public int Data2 { get; init; }
        public int RawMessage { get; init; }
    }
}
