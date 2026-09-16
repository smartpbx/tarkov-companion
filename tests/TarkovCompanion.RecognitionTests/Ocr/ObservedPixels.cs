using System.Buffers;

namespace TarkovCompanion.RecognitionTests.Ocr;

/// <summary>Pixels that count their reads and run an action at one chosen read.</summary>
/// <remarks>
/// Every luminance read takes the span again, so the count is a count of pixel reads. That is
/// what lets a test stop pixel work at a known point and then measure how far it ran past it.
/// </remarks>
internal sealed class ObservedPixels(byte[] pixels, int atRead = 0, Action? action = null) : MemoryManager<byte>
{
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    // The base property takes the span to learn the length, which would count as a pixel read.
    public override Memory<byte> Memory => CreateMemory(pixels.Length);

    public override Span<byte> GetSpan()
    {
        if (Interlocked.Increment(ref _reads) == atRead)
        {
            action?.Invoke();
        }

        return pixels;
    }

    public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }
}
