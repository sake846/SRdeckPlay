using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Sdk;

/// <summary>
/// Writes a duration-bounded stream of complex samples as stereo 16-bit PCM WAV.
/// The left channel contains I and the right channel contains Q.
/// </summary>
public class BoundedIqWavWriter : IDisposable
{
    private const int BytesPerIqSample = sizeof(short) * 2;
    private const int PcmChunkSamples = 16_384;

    private readonly BinaryWriter writer;
    private readonly long maximumSamples;
    private long samplesWritten;
    private bool disposed;

    public BoundedIqWavWriter(string path, int sampleRateHz, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (sampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        maximumSamples = checked((long)Math.Round(sampleRateHz * duration.TotalSeconds));
        if (maximumSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(duration));

        writer = new BinaryWriter(
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), Encoding.ASCII);
        Path = path;
        SampleRateHz = sampleRateHz;
        WriteHeader();
    }

    public string Path { get; }
    public int SampleRateHz { get; }
    public bool IsComplete => samplesWritten >= maximumSamples;

    public void Write(ReadOnlySpan<Complex32> samples)
    {
        int count = GetWritableSampleCount(samples.Length);
        if (count == 0)
            return;

        byte[] bytes = ArrayPool<byte>.Shared.Rent(count * BytesPerIqSample);
        try
        {
            Span<byte> destination = bytes.AsSpan(0, count * BytesPerIqSample);
            for (int index = 0; index < count; index++)
            {
                int offset = index * BytesPerIqSample;
                BinaryPrimitives.WriteInt16LittleEndian(
                    destination.Slice(offset, sizeof(short)), Quantize(samples[index].I));
                BinaryPrimitives.WriteInt16LittleEndian(
                    destination.Slice(offset + sizeof(short), sizeof(short)), Quantize(samples[index].Q));
            }
            writer.Write(bytes, 0, destination.Length);
            samplesWritten += count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public void WritePcm(ReadOnlySpan<short> interleavedIq)
    {
        int count = GetWritableSampleCount(interleavedIq.Length / 2);
        if (count == 0)
            return;

        byte[] bytes = ArrayPool<byte>.Shared.Rent(PcmChunkSamples * BytesPerIqSample);
        try
        {
            int written = 0;
            while (written < count)
            {
                int chunk = Math.Min(PcmChunkSamples, count - written);
                Span<byte> destination = bytes.AsSpan(0, chunk * BytesPerIqSample);
                ReadOnlySpan<short> source = interleavedIq.Slice(written * 2, chunk * 2);
                for (int index = 0; index < source.Length; index++)
                    BinaryPrimitives.WriteInt16LittleEndian(
                        destination.Slice(index * sizeof(short), sizeof(short)), source[index]);
                writer.Write(bytes, 0, destination.Length);
                written += chunk;
            }
            samplesWritten += count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        try
        {
            writer.Flush();
            long dataLength = samplesWritten * BytesPerIqSample;
            writer.BaseStream.Position = 4;
            writer.Write((uint)Math.Min(36 + dataLength, uint.MaxValue));
            writer.BaseStream.Position = 40;
            writer.Write((uint)Math.Min(dataLength, uint.MaxValue));
        }
        finally
        {
            writer.Dispose();
        }
    }

    private int GetWritableSampleCount(int requested) =>
        (int)Math.Min(requested, maximumSamples - samplesWritten);

    private void WriteHeader()
    {
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(SampleRateHz);
        writer.Write(checked(SampleRateHz * BytesPerIqSample));
        writer.Write((short)BytesPerIqSample);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(0);
    }

    private static short Quantize(float value) =>
        (short)MathF.Round(Math.Clamp(value, -1f, 1f) * short.MaxValue);
}
