using NAudio.Midi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RecordMe.Services;

public static class MidiChordAnalyzer
{
    private static readonly string[] NoteNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private static readonly (string Name, int[] Intervals)[] ChordTemplates =
    [
        ("maj",     [0, 4, 7]),
        ("min",     [0, 3, 7]),
        ("dim",     [0, 3, 6]),
        ("aug",     [0, 4, 8]),
        ("sus2",    [0, 2, 7]),
        ("sus4",    [0, 5, 7]),
        ("7",       [0, 4, 7, 10]),
        ("maj7",    [0, 4, 7, 11]),
        ("min7",    [0, 3, 7, 10]),
        ("dim7",    [0, 3, 6, 9]),
        ("m7b5",    [0, 3, 6, 10]),
        ("add9",    [0, 4, 7, 14]),
        ("min9",    [0, 3, 7, 10, 14]),
        ("9",       [0, 4, 7, 10, 14]),
        ("maj9",    [0, 4, 7, 11, 14]),
        ("6",       [0, 4, 7, 9]),
        ("min6",    [0, 3, 7, 9]),
    ];

    /// <summary>
    /// Analyze a MIDI file and return a summary of the most common chords.
    /// </summary>
    public static string Analyze(string midiFilePath, int maxChords = 8)
    {
        if (string.IsNullOrEmpty(midiFilePath) || !File.Exists(midiFilePath))
            return string.Empty;

        try
        {
            var midiFile = new MidiFile(midiFilePath, false);
            var noteEvents = ExtractNoteEvents(midiFile);

            if (noteEvents.Count == 0)
                return string.Empty;

            var chords = DetectChords(noteEvents, midiFile.DeltaTicksPerQuarterNote);
            if (chords.Count == 0)
                return string.Empty;

            var chordCounts = chords
                .GroupBy(c => c)
                .OrderByDescending(g => g.Count())
                .Take(maxChords)
                .Select(g => $"{g.Key} ({g.Count()}x)")
                .ToList();

            return "Chords: " + string.Join(", ", chordCounts);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<(long Tick, int NoteNumber, bool IsOn)> ExtractNoteEvents(MidiFile midiFile)
    {
        var events = new List<(long Tick, int NoteNumber, bool IsOn)>();

        foreach (var track in midiFile.Events)
        {
            foreach (var evt in track)
            {
                if (evt is NoteOnEvent noteOn)
                {
                    events.Add((noteOn.AbsoluteTime, noteOn.NoteNumber, noteOn.Velocity > 0));
                }
                else if (evt.CommandCode == MidiCommandCode.NoteOff && evt is NoteEvent noteOff)
                {
                    events.Add((noteOff.AbsoluteTime, noteOff.NoteNumber, false));
                }
            }
        }

        return events.OrderBy(e => e.Tick).ToList();
    }

    private static List<string> DetectChords(List<(long Tick, int NoteNumber, bool IsOn)> events, int ticksPerQuarter)
    {
        var chords = new List<string>();
        var activeNotes = new Dictionary<int, long>(); // noteNumber -> onTick

        // Group notes that start within a small window as simultaneous
        long chordWindow = ticksPerQuarter / 8; // 1/32 note tolerance

        var onEvents = events.Where(e => e.IsOn).OrderBy(e => e.Tick).ToList();

        // Cluster note-on events by proximity
        var clusters = new List<List<int>>();
        List<int>? currentCluster = null;
        long clusterStart = 0;

        foreach (var evt in onEvents)
        {
            if (currentCluster == null || evt.Tick - clusterStart > chordWindow)
            {
                currentCluster = [evt.NoteNumber];
                clusters.Add(currentCluster);
                clusterStart = evt.Tick;
            }
            else
            {
                currentCluster.Add(evt.NoteNumber);
            }
        }

        // Identify chords from clusters with 3+ notes
        foreach (var cluster in clusters)
        {
            if (cluster.Count < 3) continue;

            var pitchClasses = cluster
                .Select(n => n % 12)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            if (pitchClasses.Count < 3) continue;

            var chord = IdentifyChord(pitchClasses);
            if (chord != null)
                chords.Add(chord);
        }

        return chords;
    }

    private static string? IdentifyChord(List<int> pitchClasses)
    {
        // Try each pitch class as a potential root
        string? bestMatch = null;
        int bestScore = -1;

        foreach (var root in pitchClasses)
        {
            var intervals = pitchClasses
                .Select(p => (p - root + 12) % 12)
                .OrderBy(i => i)
                .Distinct()
                .ToList();

            foreach (var (name, template) in ChordTemplates)
            {
                var matched = template.Select(t => t % 12).Count(t => intervals.Contains(t));

                if (matched >= template.Length && matched > bestScore)
                {
                    bestScore = matched;
                    bestMatch = $"{NoteNames[root]}{(name == "maj" ? "" : name)}";
                }
            }
        }

        return bestMatch;
    }
}
