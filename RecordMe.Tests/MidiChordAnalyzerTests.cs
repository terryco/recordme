using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Midi;
using RecordMe.Services;

namespace RecordMe.Tests;

public class MidiChordAnalyzerTests : IDisposable
{
    private const int TicksPerQuarter = 480;
    private readonly List<string> _tempFiles = [];

    // MIDI note numbers (middle octave): C4 = 60.
    private const int C = 60, Cs = 61, D = 62, Ds = 63, E = 64, F = 65, G = 67, A = 69, B = 71;

    [Fact]
    public void Analyze_MissingPath_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MidiChordAnalyzer.Analyze("does-not-exist.mid"));
    }

    [Fact]
    public void Analyze_EmptyPath_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MidiChordAnalyzer.Analyze(string.Empty));
    }

    [Fact]
    public void Analyze_FewerThanThreeSimultaneousNotes_ReturnsEmpty()
    {
        // A two-note interval is not enough to form a chord.
        string path = CreateMidi([new[] { C, G }]);
        Assert.Equal(string.Empty, MidiChordAnalyzer.Analyze(path));
    }

    [Fact]
    public void Analyze_MajorTriad_IdentifiesRootMajor()
    {
        string path = CreateMidi([new[] { C, E, G }]);

        string result = MidiChordAnalyzer.Analyze(path);

        Assert.StartsWith("Chords:", result);
        // "maj" renders as the bare root note name.
        Assert.Contains("C (1x)", result);
    }

    [Fact]
    public void Analyze_MinorTriad_IdentifiesMinorQuality()
    {
        // C minor: C, Eb, G
        string path = CreateMidi([new[] { C, Ds, G }]);

        string result = MidiChordAnalyzer.Analyze(path);

        Assert.Contains("Cmin", result);
    }

    [Fact]
    public void Analyze_DominantSeventh_IdentifiesSeventh()
    {
        // G7: G, B, D, F
        string path = CreateMidi([new[] { G, B, D, F }]);

        string result = MidiChordAnalyzer.Analyze(path);

        Assert.Contains("G7", result);
    }

    [Fact]
    public void Analyze_RepeatedChord_CountsOccurrences()
    {
        // Same C major triad played in two well-separated clusters.
        string path = CreateMidi([new[] { C, E, G }, new[] { C, E, G }]);

        string result = MidiChordAnalyzer.Analyze(path);

        Assert.Contains("C (2x)", result);
    }

    [Fact]
    public void Analyze_RespectsMaxChordsLimit()
    {
        // Five distinct chords, but ask for at most two in the summary.
        string path = CreateMidi(
        [
            new[] { C, E, G },
            new[] { D, F, A },
            new[] { E, G, B },
            new[] { F, A, C },
            new[] { G, B, D },
        ]);

        string result = MidiChordAnalyzer.Analyze(path, maxChords: 2);

        // "Chords: X (1x), Y (1x)" => exactly one comma separating two entries.
        Assert.Equal(2, result.Split("(1x)").Length - 1);
    }

    // --- helpers ---

    // Builds a Type-1 MIDI file mirroring MidiCaptureService.WriteMidiFile: a tempo track
    // plus a note track. Each chord is placed one quarter-note apart so the analyzer treats
    // them as separate clusters.
    private string CreateMidi(int[][] chords)
    {
        var collection = new MidiEventCollection(1, TicksPerQuarter);

        collection.AddTrack();
        collection.AddEvent(new TempoEvent(500000, 0), 0);
        collection.AddEvent(new MetaEvent(MetaEventType.EndTrack, 0, 0), 0);

        collection.AddTrack();
        long lastTick = 1;
        for (int c = 0; c < chords.Length; c++)
        {
            long onTick = c * TicksPerQuarter;
            long offTick = onTick + TicksPerQuarter / 2;
            foreach (int note in chords[c])
            {
                collection.AddEvent(new NoteOnEvent(onTick, 1, note, 100, 1), 1);
                collection.AddEvent(new NoteEvent(offTick, 1, MidiCommandCode.NoteOff, note, 0), 1);
            }
            lastTick = offTick + 1;
        }
        collection.AddEvent(new MetaEvent(MetaEventType.EndTrack, 0, lastTick), 1);

        collection.PrepareForExport();

        string path = Path.Combine(Path.GetTempPath(), $"chordtest_{Guid.NewGuid():N}.mid");
        _tempFiles.Add(path);
        MidiFile.Export(path, collection);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
        }
    }
}
