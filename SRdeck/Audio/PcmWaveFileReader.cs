using System.IO;
using System.Text;

namespace SRdeck.Audio;

internal sealed class PcmWaveFileReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryReader _reader;
    private readonly long _dataOffset;
    private readonly long _dataLength;

    public PcmWaveFileReader(string filePath)
    {
        _stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new BinaryReader(_stream, Encoding.ASCII, leaveOpen: true);
        try
        {
            (WaveFormat, _dataOffset, _dataLength) = ReadHeader(_reader);
            _stream.Position = _dataOffset;
        }
        catch
        {
            _reader.Dispose();
            _stream.Dispose();
            throw;
        }
    }

    public PcmWaveFormat WaveFormat { get; }
    public TimeSpan TotalTime => TimeSpan.FromSeconds((double)_dataLength / WaveFormat.AverageBytesPerSecond);
    public long TotalSamples => _dataLength / WaveFormat.BlockAlign;

    public TimeSpan CurrentTime
    {
        get => TimeSpan.FromSeconds((double)Position / WaveFormat.AverageBytesPerSecond);
        set
        {
            long byteOffset = (long)(value.TotalSeconds * WaveFormat.AverageBytesPerSecond);
            byteOffset -= byteOffset % WaveFormat.BlockAlign;
            Position = byteOffset;
        }
    }

    public long Position
    {
        get => _stream.Position - _dataOffset;
        set
        {
            long clamped = Math.Clamp(value, 0, _dataLength);
            clamped -= clamped % WaveFormat.BlockAlign;
            _stream.Position = _dataOffset + clamped;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        long remaining = _dataLength - Position;
        if (remaining <= 0) return 0;
        int toRead = (int)Math.Min(count, remaining);
        return _stream.Read(buffer, offset, toRead);
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }

    private static (PcmWaveFormat format, long dataOffset, long dataLength) ReadHeader(BinaryReader reader)
    {
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Invalid RIFF header.");
        _ = reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Invalid WAVE header.");

        PcmWaveFormat? format = null;
        long dataOffset = -1;
        long dataLength = 0;

        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            string chunkId = new string(reader.ReadChars(4));
            uint chunkSize = reader.ReadUInt32();
            long nextChunkPosition = reader.BaseStream.Position + chunkSize + (chunkSize & 1);
            if (nextChunkPosition > reader.BaseStream.Length)
            {
                throw new InvalidDataException($"WAV chunk '{chunkId}' exceeds the file length.");
            }

            switch (chunkId)
            {
                case "fmt ":
                    if (chunkSize < 16) throw new InvalidDataException("WAV fmt chunk is too short.");
                    short audioFormat = reader.ReadInt16();
                    short channels = reader.ReadInt16();
                    int sampleRate = reader.ReadInt32();
                    int avgBytesPerSec = reader.ReadInt32();
                    short blockAlign = reader.ReadInt16();
                    short bitsPerSample = reader.ReadInt16();
                    if (audioFormat != 1) throw new InvalidDataException("Only PCM WAV is supported.");
                    format = new PcmWaveFormat(sampleRate, channels, bitsPerSample);
                    if (format.AverageBytesPerSecond != avgBytesPerSec || format.BlockAlign != blockAlign)
                    {
                        throw new InvalidDataException("Unsupported WAV format.");
                    }
                    break;

                case "data":
                    dataOffset = reader.BaseStream.Position;
                    dataLength = chunkSize;
                    break;
            }

            reader.BaseStream.Position = nextChunkPosition;
            if (format != null && dataOffset >= 0) break;
        }

        if (format == null || dataOffset < 0) throw new InvalidDataException("WAV fmt/data chunk not found.");
        if (dataLength % format.BlockAlign != 0) throw new InvalidDataException("WAV data ends inside a sample frame.");
        return (format, dataOffset, dataLength);
    }
}
