using NAudio.Midi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RecordMe.Services;

public class MidiPlaybackService : IDisposable
{
    private MidiOut? _midiOut;
    private CancellationTokenSource? _cts;
    private bool _isPlaying;
    private readonly Dictionary<int, int> _channelMap = new(); // original channel -> remapped channel

    public bool IsPlaying => _isPlaying;
    public event EventHandler? PlaybackStopped;
    public event EventHandler<double>? ProgressChanged;

    public void SetChannelMapping(int fromChannel, int toChannel)
    {
        _channelMap[fromChannel] = toChannel;
    }

    public void ClearChannelMappings()
    {
        _channelMap.Clear();
    }

    public Dictionary<int, int> GetChannelMappings() => new(_channelMap);

    public static List<string> GetMidiOutputDevices()
    {
        var devices = new List<string>();
        for (int i = 0; i < MidiOut.NumberOfDevices; i++)
        {
            devices.Add(MidiOut.DeviceInfo(i).ProductName);
        }
        return devices;
    }

    public void Play(string midiFilePath, int outputDeviceIndex = 0)
    {
        Stop();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            try
            {
                _isPlaying = true;
                _midiOut = new MidiOut(outputDeviceIndex);

                var midiFile = new MidiFile(midiFilePath, false);
                var events = new List<(long absoluteTime, MidiEvent evt)>();

                for (int track = 0; track < midiFile.Tracks; track++)
                {
                    foreach (var evt in midiFile.Events[track])
                    {
                        events.Add((evt.AbsoluteTime, evt));
                    }
                }

                events.Sort((a, b) => a.absoluteTime.CompareTo(b.absoluteTime));

                double ticksPerQuarterNote = midiFile.DeltaTicksPerQuarterNote;
                double microsecondsPerQuarterNote = 500000; // 120 BPM default
                long totalTicks = events.Count > 0 ? events[^1].absoluteTime : 0;
                long lastTick = 0;

                foreach (var (absoluteTime, evt) in events)
                {
                    if (token.IsCancellationRequested) break;

                    // Handle tempo changes
                    if (evt is TempoEvent tempo)
                    {
                        microsecondsPerQuarterNote = tempo.MicrosecondsPerQuarterNote;
                        continue;
                    }

                    if (evt is MetaEvent) continue;

                    // Calculate delay
                    long deltaTicks = absoluteTime - lastTick;
                    if (deltaTicks > 0)
                    {
                        double microsecondsPerTick = microsecondsPerQuarterNote / ticksPerQuarterNote;
                        int delayMs = (int)(deltaTicks * microsecondsPerTick / 1000.0);
                        if (delayMs > 0)
                            await Task.Delay(delayMs, token);
                    }
                    lastTick = absoluteTime;

                    // Report progress
                    if (totalTicks > 0)
                        ProgressChanged?.Invoke(this, (double)absoluteTime / totalTicks);

                    // Apply channel remapping and send
                    int channel = evt.Channel;
                    if (_channelMap.TryGetValue(channel, out int mappedChannel))
                        channel = mappedChannel;

                    if (evt is NoteOnEvent noteOn)
                    {
                        SendNoteMessage(channel, noteOn.NoteNumber, noteOn.Velocity, true);
                    }
                    else if (evt.CommandCode == MidiCommandCode.NoteOff && evt is NoteEvent noteOff)
                    {
                        SendNoteMessage(channel, noteOff.NoteNumber, 0, false);
                    }
                    else if (evt is ControlChangeEvent cc)
                    {
                        _midiOut?.Send(MidiMessage.ChangeControl(
                            (int)cc.Controller, cc.ControllerValue, channel + 1).RawData);
                    }
                    else if (evt is PitchWheelChangeEvent pw)
                    {
                        _midiOut?.Send(pw.GetAsShortMessage());
                    }
                }

                _isPlaying = false;
                PlaybackStopped?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            finally
            {
                _isPlaying = false;
            }
        }, token);
    }

    private void SendNoteMessage(int channel, int noteNumber, int velocity, bool noteOn)
    {
        if (_midiOut == null) return;
        int status = (noteOn ? 0x90 : 0x80) | (channel & 0x0F);
        int message = status | (noteNumber << 8) | (velocity << 16);
        _midiOut.Send(message);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        // Send all notes off on all channels
        if (_midiOut != null)
        {
            for (int ch = 0; ch < 16; ch++)
            {
                _midiOut.Send(MidiMessage.ChangeControl(123, 0, ch + 1).RawData); // All Notes Off
            }
            _midiOut.Dispose();
            _midiOut = null;
        }

        _isPlaying = false;
    }

    public void Dispose()
    {
        Stop();
    }
}
