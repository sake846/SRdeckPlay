
// Shared bounded IQ storage used by the host and plugin test/benchmark paths.
namespace SRdeck.DSP;

public sealed class IqSampleRingBuffer
{
    private const int SegmentBits = 22;
    private const int SegmentSize = 1 << SegmentBits;
    private const int SegmentMask = SegmentSize - 1;

    private readonly short[][] _segmentsI;
    private readonly short[][] _segmentsQ;

    public int Capacity { get; }

    public IqSampleRingBuffer(int capacity)
    {
        Capacity = Math.Max(1, capacity);
        int segmentCount = (Capacity + SegmentSize - 1) / SegmentSize;
        _segmentsI = new short[segmentCount][];
        _segmentsQ = new short[segmentCount][];

        for (int i = 0; i < segmentCount; i++)
        {
            int segmentLength = Math.Min(SegmentSize, Capacity - (i * SegmentSize));
            _segmentsI[i] = new short[segmentLength];
            _segmentsQ[i] = new short[segmentLength];
        }
    }

    public void WriteSample(int index, short sampleI, short sampleQ)
    {
        NormalizeIndex(ref index);
        int segment = index >> SegmentBits;
        int offset = index & SegmentMask;
        _segmentsI[segment][offset] = sampleI;
        _segmentsQ[segment][offset] = sampleQ;
    }

    public int Write(int writePosition, ReadOnlySpan<short> sourceI, ReadOnlySpan<short> sourceQ)
    {
        int count = Math.Min(sourceI.Length, sourceQ.Length);
        int sourceOffset = 0;
        NormalizeIndex(ref writePosition);

        while (sourceOffset < count)
        {
            int segment = writePosition >> SegmentBits;
            int offset = writePosition & SegmentMask;
            int chunk = Math.Min(count - sourceOffset, _segmentsI[segment].Length - offset);

            sourceI.Slice(sourceOffset, chunk).CopyTo(_segmentsI[segment].AsSpan(offset, chunk));
            sourceQ.Slice(sourceOffset, chunk).CopyTo(_segmentsQ[segment].AsSpan(offset, chunk));

            sourceOffset += chunk;
            writePosition += chunk;
            if (writePosition == Capacity) writePosition = 0;
        }

        return writePosition;
    }

    public short GetI(int index)
    {
        NormalizeIndex(ref index);
        return _segmentsI[index >> SegmentBits][index & SegmentMask];
    }

    public short GetQ(int index)
    {
        NormalizeIndex(ref index);
        return _segmentsQ[index >> SegmentBits][index & SegmentMask];
    }

    internal ContiguousBlock GetContiguousBlock(int offset, int maxCount)
    {
        int sourceIndex = NormalizeIndex(offset);
        int segment = sourceIndex >> SegmentBits;
        int segmentOffset = sourceIndex & SegmentMask;
        int length = Math.Min(maxCount, _segmentsI[segment].Length - segmentOffset);
        return new ContiguousBlock(_segmentsI[segment], _segmentsQ[segment], segmentOffset, length);
    }

    internal readonly struct ContiguousBlock
    {
        public ContiguousBlock(short[] samplesI, short[] samplesQ, int offset, int length)
        {
            SamplesI = samplesI;
            SamplesQ = samplesQ;
            Offset = offset;
            Length = length;
        }

        public short[] SamplesI { get; }
        public short[] SamplesQ { get; }
        public int Offset { get; }
        public int Length { get; }
    }

    public void CopyTo(int offset, short[] destI, short[] destQ, int destOffset, int count)
    {
        int sourceIndex = NormalizeIndex(offset);
        int copied = 0;

        while (copied < count)
        {
            int segment = sourceIndex >> SegmentBits;
            int segmentOffset = sourceIndex & SegmentMask;
            int chunk = Math.Min(count - copied, _segmentsI[segment].Length - segmentOffset);

            Array.Copy(_segmentsI[segment], segmentOffset, destI, destOffset + copied, chunk);
            Array.Copy(_segmentsQ[segment], segmentOffset, destQ, destOffset + copied, chunk);

            copied += chunk;
            sourceIndex += chunk;
            if (sourceIndex == Capacity) sourceIndex = 0;
        }
    }

    internal unsafe void ApplyWindowToFloat(int offset, float[] window, float[] destI, float[] destQ, int count)
    {
        int sourceIndex = NormalizeIndex(offset);
        int destOffset = 0;

        fixed (float* pWindow = window)
        fixed (float* pDestI = destI)
        fixed (float* pDestQ = destQ)
        {
            while (destOffset < count)
            {
                int segment = sourceIndex >> SegmentBits;
                int segmentOffset = sourceIndex & SegmentMask;
                int chunk = Math.Min(count - destOffset, _segmentsI[segment].Length - segmentOffset);

                short[] sourceI = _segmentsI[segment];
                short[] sourceQ = _segmentsQ[segment];

                fixed (short* pSourceI = sourceI)
                fixed (short* pSourceQ = sourceQ)
                {
                    int i = 0;
                    if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
                    {
                        for (; i <= chunk - 8; i += 8)
                        {
                            var vWin = System.Runtime.Intrinsics.X86.Avx.LoadVector256(pWindow + destOffset + i);
                            
                            // Load 8 shorts
                            var vecI16 = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(pSourceI + segmentOffset + i);
                            var vecQ16 = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(pSourceQ + segmentOffset + i);
                            
                            // Convert to 8 ints (sign extend)
                            var vecI32 = System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int32(vecI16);
                            var vecQ32 = System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int32(vecQ16);
                            
                            // Convert to 8 floats
                            var vecIf32 = System.Runtime.Intrinsics.X86.Avx.ConvertToVector256Single(vecI32);
                            var vecQf32 = System.Runtime.Intrinsics.X86.Avx.ConvertToVector256Single(vecQ32);
                            
                            // Multiply by window
                            var resI = System.Runtime.Intrinsics.X86.Avx.Multiply(vecIf32, vWin);
                            var resQ = System.Runtime.Intrinsics.X86.Avx.Multiply(vecQf32, vWin);
                            
                            System.Runtime.Intrinsics.X86.Avx.Store(pDestI + destOffset + i, resI);
                            System.Runtime.Intrinsics.X86.Avx.Store(pDestQ + destOffset + i, resQ);
                        }
                    }
                    for (; i < chunk; i++)
                    {
                        float scale = pWindow[destOffset + i];
                        pDestI[destOffset + i] = pSourceI[segmentOffset + i] * scale;
                        pDestQ[destOffset + i] = pSourceQ[segmentOffset + i] * scale;
                    }
                }

                destOffset += chunk;
                sourceIndex += chunk;
                if (sourceIndex == Capacity) sourceIndex = 0;
            }
        }
    }

    internal unsafe void ApplyWindowToComplex(int offset, float[] window, Complex[] dest, int count)
    {
        int sourceIndex = NormalizeIndex(offset);
        int destOffset = 0;

        fixed (float* pWindow = window)
        fixed (Complex* pDest = dest)
        {
            float* pDestFloat = (float*)pDest; // Complex は struct { float X; float Y; } のためそのままfloatポインタとして扱える
            while (destOffset < count)
            {
                int segment = sourceIndex >> SegmentBits;
                int segmentOffset = sourceIndex & SegmentMask;
                int chunk = Math.Min(count - destOffset, _segmentsI[segment].Length - segmentOffset);

                short[] sourceI = _segmentsI[segment];
                short[] sourceQ = _segmentsQ[segment];

                fixed (short* pSourceI = sourceI)
                fixed (short* pSourceQ = sourceQ)
                {
                    int i = 0;
                    if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
                    {
                        for (; i <= chunk - 8; i += 8)
                        {
                            var vWin = System.Runtime.Intrinsics.X86.Avx.LoadVector256(pWindow + destOffset + i);
                            
                            var vecI16 = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(pSourceI + segmentOffset + i);
                            var vecQ16 = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(pSourceQ + segmentOffset + i);
                            
                            var vecI32 = System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int32(vecI16);
                            var vecQ32 = System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int32(vecQ16);
                            
                            var vecIf32 = System.Runtime.Intrinsics.X86.Avx.ConvertToVector256Single(vecI32);
                            var vecQf32 = System.Runtime.Intrinsics.X86.Avx.ConvertToVector256Single(vecQ32);
                            
                            var resI = System.Runtime.Intrinsics.X86.Avx.Multiply(vecIf32, vWin);
                            var resQ = System.Runtime.Intrinsics.X86.Avx.Multiply(vecQf32, vWin);
                            
                            // I Q I Q インターリーブして Store
                            var unpckL = System.Runtime.Intrinsics.X86.Avx.UnpackLow(resI, resQ);
                            var unpckH = System.Runtime.Intrinsics.X86.Avx.UnpackHigh(resI, resQ);
                            // AVX の UnpackLow/High は 128ビットレーンごとに行われるので、Permute2x128 などが必要
                            // 簡易的に _mm256_storeu_ps を2回使うために Permute を挟む
                            // float4 [i0, q0, i1, q1, i4, q4, i5, q5] ...
                            // 複雑になるため、Sseでインターリーブしてストアする
                            var lo128I = System.Runtime.Intrinsics.X86.Avx.ExtractVector128(resI, 0);
                            var lo128Q = System.Runtime.Intrinsics.X86.Avx.ExtractVector128(resQ, 0);
                            var hi128I = System.Runtime.Intrinsics.X86.Avx.ExtractVector128(resI, 1);
                            var hi128Q = System.Runtime.Intrinsics.X86.Avx.ExtractVector128(resQ, 1);
                            
                            var iq01 = System.Runtime.Intrinsics.X86.Sse.UnpackLow(lo128I, lo128Q);
                            var iq23 = System.Runtime.Intrinsics.X86.Sse.UnpackHigh(lo128I, lo128Q);
                            var iq45 = System.Runtime.Intrinsics.X86.Sse.UnpackLow(hi128I, hi128Q);
                            var iq67 = System.Runtime.Intrinsics.X86.Sse.UnpackHigh(hi128I, hi128Q);
                            
                            System.Runtime.Intrinsics.X86.Sse.Store(pDestFloat + (destOffset + i) * 2 + 0, iq01);
                            System.Runtime.Intrinsics.X86.Sse.Store(pDestFloat + (destOffset + i) * 2 + 4, iq23);
                            System.Runtime.Intrinsics.X86.Sse.Store(pDestFloat + (destOffset + i) * 2 + 8, iq45);
                            System.Runtime.Intrinsics.X86.Sse.Store(pDestFloat + (destOffset + i) * 2 + 12, iq67);
                        }
                    }
                    for (; i < chunk; i++)
                    {
                        float scale = pWindow[destOffset + i];
                        pDest[destOffset + i].X = pSourceI[segmentOffset + i] * scale;
                        pDest[destOffset + i].Y = pSourceQ[segmentOffset + i] * scale;
                    }
                }

                destOffset += chunk;
                sourceIndex += chunk;
                if (sourceIndex == Capacity) sourceIndex = 0;
            }
        }
    }

    private void NormalizeIndex(ref int index)
    {
        if ((uint)index < (uint)Capacity) return;
        index %= Capacity;
        if (index < 0) index += Capacity;
    }

    private int NormalizeIndex(int index)
    {
        NormalizeIndex(ref index);
        return index;
    }
}
