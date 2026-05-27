using RecordMe.Services;

namespace RecordMe.Tests;

public class CircularAudioBufferTests
{
    [Fact]
    public void Capacity_ReflectsConstructorArgument()
    {
        var buffer = new CircularAudioBuffer(8);
        Assert.Equal(8, buffer.Capacity);
    }

    [Fact]
    public void ReadAll_OnEmptyBuffer_ReturnsEmpty()
    {
        var buffer = new CircularAudioBuffer(4);
        Assert.Empty(buffer.ReadAll());
    }

    [Fact]
    public void Write_BelowCapacity_PreservesOrderAndCount()
    {
        var buffer = new CircularAudioBuffer(8);
        var input = new[] { 0.1f, 0.2f, 0.3f };

        buffer.Write(input, 0, input.Length);

        Assert.Equal(input, buffer.ReadAll());
    }

    [Fact]
    public void Write_RespectsOffset()
    {
        var buffer = new CircularAudioBuffer(8);
        var input = new[] { 9f, 9f, 0.5f, 0.6f };

        // Skip the first two samples via the offset.
        buffer.Write(input, 2, 2);

        Assert.Equal(new[] { 0.5f, 0.6f }, buffer.ReadAll());
    }

    [Fact]
    public void Write_ExceedingCapacity_KeepsMostRecentSamplesInOrder()
    {
        var buffer = new CircularAudioBuffer(4);

        buffer.Write(new[] { 1f, 2f, 3f, 4f, 5f, 6f }, 0, 6);

        // Only the last `Capacity` samples survive, oldest-to-newest.
        Assert.Equal(new[] { 3f, 4f, 5f, 6f }, buffer.ReadAll());
    }

    [Fact]
    public void Write_AcrossMultipleCalls_WrapsCorrectly()
    {
        var buffer = new CircularAudioBuffer(4);

        buffer.Write(new[] { 1f, 2f, 3f }, 0, 3);
        buffer.Write(new[] { 4f, 5f }, 0, 2);

        // 5 samples written into a capacity-4 buffer => drop the first.
        Assert.Equal(new[] { 2f, 3f, 4f, 5f }, buffer.ReadAll());
    }

    [Fact]
    public void Clear_EmptiesBuffer()
    {
        var buffer = new CircularAudioBuffer(4);
        buffer.Write(new[] { 1f, 2f }, 0, 2);

        buffer.Clear();

        Assert.Empty(buffer.ReadAll());
    }

    [Fact]
    public void WriteAfterClear_StartsFresh()
    {
        var buffer = new CircularAudioBuffer(4);
        buffer.Write(new[] { 1f, 2f }, 0, 2);
        buffer.Clear();

        buffer.Write(new[] { 7f, 8f }, 0, 2);

        Assert.Equal(new[] { 7f, 8f }, buffer.ReadAll());
    }
}
