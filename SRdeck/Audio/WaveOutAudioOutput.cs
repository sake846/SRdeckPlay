using System.Runtime.InteropServices;

namespace SRdeck.Audio;

public sealed class WaveOutAudioOutput : IAudioOutput
{
    private const int WaveMapper = -1;
    private const int AudioBitDepth = 16;
    private const int AudioBufferDurationMs = 300;
    private const int WhdrDone = 0x00000001;

    private readonly object _lock = new();
    private readonly List<SubmittedBuffer> _submitted = new();
    private readonly Stack<SubmittedBuffer> _available = new();
    private IntPtr _waveOut = IntPtr.Zero;
    private int _bufferLength;
    private bool _isPaused;

    public int BufferLength => _bufferLength;

    public void Initialize(int sampleRate, int channels)
    {
        lock (_lock)
        {
            DisposeDevice();

            var format = new WAVEFORMATEX
            {
                wFormatTag = 1,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                wBitsPerSample = AudioBitDepth,
                nBlockAlign = (ushort)(channels * (AudioBitDepth / 8)),
                nAvgBytesPerSec = (uint)(sampleRate * channels * (AudioBitDepth / 8)),
                cbSize = 0
            };

            int mmr = waveOutOpen(out _waveOut, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
            if (mmr != 0 || _waveOut == IntPtr.Zero)
            {
                _waveOut = IntPtr.Zero;
                throw new InvalidOperationException($"waveOutOpen failed: {mmr}");
            }

            _bufferLength = (int)(format.nAvgBytesPerSec * AudioBufferDurationMs / 1000u);
        }
    }

    public void Play()
    {
        lock (_lock)
        {
            if (_waveOut == IntPtr.Zero) return;
            _isPaused = false;
            _ = waveOutRestart(_waveOut);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_waveOut == IntPtr.Zero) return;
            _isPaused = false;
            _ = waveOutReset(_waveOut);
            ReleaseCompletedBuffers(forceAll: true);
        }
    }

    public void WriteSamples(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_waveOut == IntPtr.Zero || count <= 0) return;

            ReleaseCompletedBuffers(forceAll: false);

            SubmittedBuffer submitted = RentBuffer(count);
            Buffer.BlockCopy(buffer, offset, submitted.Data, 0, count);
            submitted.Prepare(count);

            int mmr = waveOutPrepareHeader(_waveOut, submitted.HeaderPointer, (uint)Marshal.SizeOf<WAVEHDR>());
            if (mmr != 0)
            {
                ReturnBuffer(submitted);
                return;
            }

            mmr = waveOutWrite(_waveOut, submitted.HeaderPointer, (uint)Marshal.SizeOf<WAVEHDR>());
            if (mmr != 0)
            {
                _ = waveOutUnprepareHeader(_waveOut, submitted.HeaderPointer, (uint)Marshal.SizeOf<WAVEHDR>());
                ReturnBuffer(submitted);
                return;
            }

            _submitted.Add(submitted);
        }
    }

    public void SetPlaybackPaused(bool paused)
    {
        lock (_lock)
        {
            if (_waveOut == IntPtr.Zero) return;
            if (_isPaused == paused) return;
            _isPaused = paused;
            _ = paused ? waveOutPause(_waveOut) : waveOutRestart(_waveOut);
        }
    }

    public int GetBufferedBytes()
    {
        lock (_lock)
        {
            ReleaseCompletedBuffers(forceAll: false);
            int total = 0;
            foreach (SubmittedBuffer buffer in _submitted)
            {
                total += buffer.ByteCount;
            }
            return total;
        }
    }

    public void ClearBuffer()
    {
        lock (_lock)
        {
            if (_waveOut == IntPtr.Zero) return;
            _ = waveOutReset(_waveOut);
            ReleaseCompletedBuffers(forceAll: true);
            if (!_isPaused)
            {
                _ = waveOutRestart(_waveOut);
            }
        }
    }

    public void TrimBufferedBytes(int targetBytes)
    {
        lock (_lock)
        {
            if (_waveOut == IntPtr.Zero) return;

            ReleaseCompletedBuffers(forceAll: false);

            int totalBytes = 0;
            foreach (SubmittedBuffer buffer in _submitted)
            {
                totalBytes += buffer.ByteCount;
            }
            if (totalBytes <= targetBytes) return;

            var keep = new List<SubmittedBuffer>();
            int keptBytes = 0;
            for (int i = _submitted.Count - 1; i >= 0; i--)
            {
                SubmittedBuffer submitted = _submitted[i];
                if (keptBytes + submitted.ByteCount > targetBytes) continue;
                keep.Add(submitted);
                keptBytes += submitted.ByteCount;
            }
            keep.Reverse();

            _ = waveOutReset(_waveOut);
            foreach (SubmittedBuffer submitted in _submitted)
            {
                _ = waveOutUnprepareHeader(_waveOut, submitted.HeaderPointer, (uint)Marshal.SizeOf<WAVEHDR>());
                if (!keep.Contains(submitted))
                {
                    ReturnBuffer(submitted);
                }
            }
            _submitted.Clear();

            foreach (SubmittedBuffer submitted in keep)
            {
                int byteCount = submitted.ByteCount;
                submitted.Prepare(byteCount);
                int mmr = waveOutPrepareHeader(_waveOut, submitted.HeaderPointer, (uint)Marshal.SizeOf<WAVEHDR>());
                if (mmr == 0)
                {
                    mmr = waveOutWrite(_waveOut, submitted.HeaderPointer, (uint)Marshal.SizeOf<WAVEHDR>());
                }
                if (mmr != 0)
                {
                    _ = waveOutUnprepareHeader(_waveOut, submitted.HeaderPointer, (uint)Marshal.SizeOf<WAVEHDR>());
                    ReturnBuffer(submitted);
                    continue;
                }
                _submitted.Add(submitted);
            }
            if (!_isPaused)
            {
                _ = waveOutRestart(_waveOut);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            DisposeDevice();
        }
    }

    private void DisposeDevice()
    {
        using (SRdeck.Services.ShutdownDiagnosticLog.Scope("WaveOutAudioOutput.DisposeDevice"))
        {
            if (_waveOut != IntPtr.Zero)
            {
                _ = waveOutReset(_waveOut);
                ReleaseCompletedBuffers(forceAll: true);
                int res = waveOutClose(_waveOut);
                if (res != 0)
                {
                    SRdeck.Services.ShutdownDiagnosticLog.Write("WaveOutAudioOutput.DisposeDevice", "waveOutClose returned " + res + ", retrying after sleep...");
                    System.Threading.Thread.Sleep(15);
                    _ = waveOutReset(_waveOut);
                    ReleaseCompletedBuffers(forceAll: true);
                    res = waveOutClose(_waveOut);
                    SRdeck.Services.ShutdownDiagnosticLog.Write("WaveOutAudioOutput.DisposeDevice", "retry waveOutClose result: " + res);
                }
                _waveOut = IntPtr.Zero;
            }

            _bufferLength = 0;
            _isPaused = false;
            FreePooledBuffers();
        }
    }

    private void ReleaseCompletedBuffers(bool forceAll)
    {
        for (int i = _submitted.Count - 1; i >= 0; i--)
        {
            SubmittedBuffer submitted = _submitted[i];
            WAVEHDR header = Marshal.PtrToStructure<WAVEHDR>(submitted.HeaderPointer);
            bool isDone = forceAll || ((header.dwFlags & WhdrDone) != 0);
            if (!isDone) continue;

            if (_waveOut != IntPtr.Zero)
            {
                _ = waveOutUnprepareHeader(_waveOut, submitted.HeaderPointer, (uint)Marshal.SizeOf<WAVEHDR>());
            }

            _submitted.RemoveAt(i);
            ReturnBuffer(submitted);
        }
    }

    private SubmittedBuffer RentBuffer(int byteCount)
    {
        while (_available.Count > 0)
        {
            SubmittedBuffer pooled = _available.Pop();
            if (pooled.Capacity >= byteCount) return pooled;
            pooled.Dispose();
        }

        return new SubmittedBuffer(byteCount);
    }

    private void ReturnBuffer(SubmittedBuffer buffer)
    {
        buffer.ByteCount = 0;
        _available.Push(buffer);
    }

    private void FreePooledBuffers()
    {
        while (_available.Count > 0)
        {
            _available.Pop().Dispose();
        }
    }

    private sealed class SubmittedBuffer
    {
        public SubmittedBuffer(int byteCount)
        {
            Data = new byte[byteCount];
            DataHandle = GCHandle.Alloc(Data, GCHandleType.Pinned);
            HeaderPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
        }

        public byte[] Data { get; }
        public int Capacity => Data.Length;
        public GCHandle DataHandle { get; }
        public IntPtr HeaderPointer { get; }
        public int ByteCount { get; set; }

        public void Prepare(int byteCount)
        {
            ByteCount = byteCount;
            var header = new WAVEHDR
            {
                lpData = DataHandle.AddrOfPinnedObject(),
                dwBufferLength = (uint)byteCount
            };
            Marshal.StructureToPtr(header, HeaderPointer, false);
        }

        public void Dispose()
        {
            if (DataHandle.IsAllocated) DataHandle.Free();
            Marshal.FreeHGlobal(HeaderPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr hWaveOut, int uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutRestart(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutPause(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hWaveOut);
}
