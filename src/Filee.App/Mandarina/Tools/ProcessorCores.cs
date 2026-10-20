// Mandarina: how many physical cores the processor has. FFmpeg's whisper filter runs one thread per logical processor unless told
// otherwise, and on a processor with two threads per core (SMT) that is slower than one thread per core: whisper.cpp's threads wait
// for each other in a spin loop, so the second thread of a core only takes the time of the first. Measured on an 8-core, 16-thread
// laptop processor with the small model: three minutes of audio took 54 s with 4 threads, 45 s with 6, 46 s with 8, 51 s with 10, 61 s with 12
// and 64 s with the default 16 (the text is the same).

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Filee.App.Mandarina.Tools;

public static class ProcessorCores
{
    private const int RelationProcessorCore = 0;

    /// <summary>The physical cores of this PC: at least 1 and never more than the logical processors the program may use.</summary>
    public static int Count { get; } = Math.Clamp(Detect() ?? Environment.ProcessorCount, 1, Math.Max(1, Environment.ProcessorCount));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, byte[]? buffer, ref uint returnedLength);

    private static int? Detect()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            uint length = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref length); // fails on purpose: it says how much room it needs
            if (length == 0)
                return null;
            var buffer = new byte[length];
            return GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length) ? CountCores(buffer.AsSpan(0, (int)length)) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The core records in what GetLogicalProcessorInformationEx wrote for RelationProcessorCore: records one after another, each
    /// starting with its relationship (4 bytes) and its own size in bytes (4 bytes). Zero when the buffer holds none.
    /// </summary>
    internal static int CountCores(ReadOnlySpan<byte> buffer)
    {
        var cores = 0;
        for (var offset = 0; offset + 8 <= buffer.Length;)
        {
            var relationship = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(buffer[(offset + 4)..]);
            if (size < 8)
                break; // not a record: stop rather than loop
            if (relationship == RelationProcessorCore)
                cores++;
            offset += size;
        }
        return cores;
    }
}
