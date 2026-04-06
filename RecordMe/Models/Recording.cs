using System;
using System.ComponentModel.DataAnnotations;

namespace RecordMe.Models;

public class Recording
{
    [Key]
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TimeSpan Duration { get; set; }

    public string WavFilePath { get; set; } = string.Empty;

    public string MidiFilePath { get; set; } = string.Empty;

    public string Instruments { get; set; } = string.Empty;

    public string Patches { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string FxInfo { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public bool FollowUp { get; set; }

    public int SampleRate { get; set; } = 44100;

    public int Channels { get; set; } = 2;

    public int BitsPerSample { get; set; } = 16;
}
