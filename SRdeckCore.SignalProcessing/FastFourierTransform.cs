using System.Runtime.InteropServices;

// Shared by the host spectrum pipeline and IQ-consuming plugins.
namespace SRdeck.DSP;

/// <summary>
/// CPU 側 FFT 実装を抽象化するクラスです。
/// I/Qベースバンド信号のスペクトル解析を行い、dB値として出力します。
/// </summary>
internal class FastFourierTransform
{
    private static class NativeMethods
    {
        private const string DllName = "sr_fft";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "cpufft_execute_db")]
        public static extern int ExecuteDb([In, Out] Complex[] samples, int sampleSize, int logN, float bias, [Out] float[] outputDb);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "cpufft_execute_power")]
        public static extern int ExecutePower([In, Out] Complex[] samples, int sampleSize, int logN, [Out] float[] outputPower);
    }

    private Complex[] _inputData;
    public Complex[] InputData => _inputData;
    public float[] OutputData;
    private int _sampleSize;
    private bool _nativeDbUnavailable;
    private bool _nativePowerUnavailable;

    public FastFourierTransform(int sampleSize)
    {
        _sampleSize = sampleSize;
        _inputData = new Complex[sampleSize];
        OutputData = new float[sampleSize];
    }

    /// <summary>
    /// 内部バッファ _inputData にデータがセットされている前提で FFT を実行し、結果を対数 (dB) 化して OutputData へ保存します。
    /// logN は 2^m = sampleSize となる m を指します。
    /// </summary>
    public int Execute(int logN, float bias)
    {
        if (!_nativeDbUnavailable)
        {
            try
            {
                int rc = NativeMethods.ExecuteDb(_inputData, _sampleSize, logN, bias, OutputData);
                if (rc == 0) return 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
            {
                // A missing/incompatible binary will not recover on the next
                // symbol. Do not repeat loader exceptions in the managed path.
                _nativeDbUnavailable = true;
            }
        }

        ManagedExecute(logN, bias, outputPower: false);
        return 0;
    }

    public int ExecutePower(int logN)
    {
        if (!_nativePowerUnavailable)
        {
            try
            {
                int rc = NativeMethods.ExecutePower(_inputData, _sampleSize, logN, OutputData);
                if (rc == 0) return 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
            {
                _nativePowerUnavailable = true;
            }
        }

        ManagedExecute(logN, 0.0f, outputPower: true);
        return 0;
    }

    private class FftPlan
    {
        public int[] BitReverse = null!;
        public float[][] Twiddles = null!;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, FftPlan> _planCache = new();

    private static FftPlan GetPlan(int sampleSize, int logN)
    {
        return _planCache.GetOrAdd(sampleSize, size =>
        {
            var plan = new FftPlan
            {
                BitReverse = new int[size],
                Twiddles = new float[logN][]
            };

            for (int i = 0; i < size; ++i)
            {
                int reversed = 0;
                int src = i;
                for (int bit = 0; bit < logN; ++bit)
                {
                    reversed = (reversed << 1) | (src & 1);
                    src >>= 1;
                }
                plan.BitReverse[i] = reversed;
            }

            for (int stage = 0; stage < logN; ++stage)
            {
                int len = 1 << (stage + 1);
                int halfLen = len >> 1;
                plan.Twiddles[stage] = new float[halfLen * 2];

                for (int j = 0; j < halfLen; ++j)
                {
                    float angle = -2.0f * MathF.PI * j / len;
                    plan.Twiddles[stage][j * 2] = MathF.Cos(angle);
                    plan.Twiddles[stage][j * 2 + 1] = MathF.Sin(angle);
                }
            }
            return plan;
        });
    }

    private unsafe void ManagedExecute(int logN, float bias, bool outputPower)
    {
        if ((1 << logN) != _sampleSize) throw new ArgumentOutOfRangeException(nameof(logN));

        var plan = GetPlan(_sampleSize, logN);
        var bitRev = plan.BitReverse;
        
        // Bit reversal inplace
        for (int i = 0; i < _sampleSize; i++)
        {
            int j = bitRev[i];
            if (i < j)
            {
                (_inputData[i], _inputData[j]) = (_inputData[j], _inputData[i]);
            }
        }

        fixed (Complex* pData = _inputData)
        {
            float* pFloats = (float*)pData; 
            
            for (int stage = 0; stage < logN; ++stage)
            {
                int len = 1 << (stage + 1);
                int halfLen = len >> 1;
                float[] twiddles = plan.Twiddles[stage];

                int jStart = 0;
                if (halfLen >= 4 && System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
                {
                    jStart = halfLen & ~3;
                    fixed (float* pTwiddles = twiddles)
                    {
                        for (int j = 0; j <= halfLen - 4; j += 4)
                        {
                            var twVec = System.Runtime.Intrinsics.X86.Avx.LoadVector256(pTwiddles + j * 2);
                            var twReal = System.Runtime.Intrinsics.X86.Avx.Shuffle(twVec, twVec, 0xA0);
                            var twImag = System.Runtime.Intrinsics.X86.Avx.Shuffle(twVec, twVec, 0xF5);

                            for (int baseIdx = 0; baseIdx < _sampleSize; baseIdx += len)
                            {
                                float* evenPtr = pFloats + (baseIdx + j) * 2;
                                float* oddPtr = pFloats + (baseIdx + j + halfLen) * 2;

                                var evenVec = System.Runtime.Intrinsics.X86.Avx.LoadVector256(evenPtr);
                                var oddVec = System.Runtime.Intrinsics.X86.Avx.LoadVector256(oddPtr);

                                var oddSwapped = System.Runtime.Intrinsics.X86.Avx.Shuffle(oddVec, oddVec, 0xB1);
                                var prod = System.Runtime.Intrinsics.X86.Avx.Multiply(oddSwapped, twImag);
                                
                                System.Runtime.Intrinsics.Vector256<float> rotated;
                                if (System.Runtime.Intrinsics.X86.Fma.IsSupported)
                                {
                                    rotated = System.Runtime.Intrinsics.X86.Fma.MultiplyAddSubtract(oddVec, twReal, prod);
                                }
                                else
                                {
                                    var ab = System.Runtime.Intrinsics.X86.Avx.Multiply(oddVec, twReal);
                                    rotated = System.Runtime.Intrinsics.X86.Avx.AddSubtract(ab, prod);
                                }

                                var newEven = System.Runtime.Intrinsics.X86.Avx.Add(evenVec, rotated);
                                var newOdd = System.Runtime.Intrinsics.X86.Avx.Subtract(evenVec, rotated);

                                System.Runtime.Intrinsics.X86.Avx.Store(evenPtr, newEven);
                                System.Runtime.Intrinsics.X86.Avx.Store(oddPtr, newOdd);
                            }
                        }
                    }
                }
                
                // Scalar fallback for remaining
                for (int baseIdx = 0; baseIdx < _sampleSize; baseIdx += len)
                {
                    for (int j = jStart; j < halfLen; j++)
                    {
                        float wCos = twiddles[j * 2];
                        float wSin = twiddles[j * 2 + 1];

                        ref Complex even = ref _inputData[baseIdx + j];
                        ref Complex odd = ref _inputData[baseIdx + j + halfLen];

                        float vX = odd.X * wCos - odd.Y * wSin;
                        float vY = odd.X * wSin + odd.Y * wCos;

                        float uX = even.X;
                        float uY = even.Y;

                        even.X = uX + vX;
                        even.Y = uY + vY;
                        odd.X = uX - vX;
                        odd.Y = uY - vY;
                    }
                }
            }
        }

        int halfSize = _sampleSize / 2;
        for (int j = 0; j < _sampleSize; j++)
        {
            int targetIdx = (j + halfSize) % _sampleSize;
            float magSq = _inputData[j].X * _inputData[j].X + _inputData[j].Y * _inputData[j].Y;
            OutputData[targetIdx] = outputPower
                ? magSq
                : 10f * MathF.Log10(MathF.Max(magSq, 1e-30f)) + bias;
        }
    }
}
