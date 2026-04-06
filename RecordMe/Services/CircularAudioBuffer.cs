using System;

namespace RecordMe.Services;

public class CircularAudioBuffer
{
    private readonly float[] _buffer;
    private int _writePos;
    private int _count;
    private readonly object _lock = new();

    public int Capacity => _buffer.Length;

    public CircularAudioBuffer(int capacitySamples)
    {
        _buffer = new float[capacitySamples];
    }

    public void Write(float[] data, int offset, int count)
    {
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                _buffer[_writePos] = data[offset + i];
                _writePos = (_writePos + 1) % _buffer.Length;
            }
            _count = Math.Min(_count + count, _buffer.Length);
        }
    }

    public float[] ReadAll()
    {
        lock (_lock)
        {
            var result = new float[_count];
            int readStart = (_writePos - _count + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < _count; i++)
            {
                result[i] = _buffer[(readStart + i) % _buffer.Length];
            }
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _writePos = 0;
            _count = 0;
        }
    }
}
