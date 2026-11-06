using System.Numerics;

namespace SRdeckCore.SignalProcessing;

/// <summary>
/// Reusable radix-2 FFT plan for split single-precision, interleaved
/// single-precision, and <see cref="Complex"/> buffers.
/// </summary>
public sealed class Radix2Fft
{
    private readonly int size;
    private readonly int[] reversed;
    private float[]? cosine;
    private float[]? sine;
    private double[]? doubleCosine;
    private double[]? doubleSine;

    public Radix2Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size), "FFT size must be a power of two.");

        this.size = size;
        int bits = BitOperations.Log2((uint)size);
        reversed = new int[size];
        for (int index = 0; index < size; index++)
            reversed[index] = Reverse(index, bits);
    }

    public int Size => size;

    public void Transform(float[] real, float[] imaginary)
    {
        ArgumentNullException.ThrowIfNull(real);
        ArgumentNullException.ThrowIfNull(imaginary);
        if (real.Length != size || imaginary.Length != size)
            throw new ArgumentException("FFT buffers have an invalid size.");

        EnsureFloatTwiddles();
        for (int index = 0; index < size; index++)
        {
            int target = reversed[index];
            if (index >= target) continue;
            (real[index], real[target]) = (real[target], real[index]);
            (imaginary[index], imaginary[target]) = (imaginary[target], imaginary[index]);
        }

        for (int width = 2; width <= size; width <<= 1)
        {
            int half = width >> 1;
            int twiddleStep = size / width;
            for (int start = 0; start < size; start += width)
            {
                for (int offset = 0; offset < half; offset++)
                {
                    int twiddle = offset * twiddleStep;
                    int even = start + offset;
                    int odd = even + half;
                    float oddReal = real[odd] * cosine![twiddle] - imaginary[odd] * sine![twiddle];
                    float oddImaginary = real[odd] * sine[twiddle] + imaginary[odd] * cosine[twiddle];
                    float evenReal = real[even];
                    float evenImaginary = imaginary[even];
                    real[even] = evenReal + oddReal;
                    imaginary[even] = evenImaginary + oddImaginary;
                    real[odd] = evenReal - oddReal;
                    imaginary[odd] = evenImaginary - oddImaginary;
                }
            }
        }
    }

    public void Forward(ReadOnlySpan<ComplexF> input, Span<ComplexF> output)
    {
        if (input.Length < size || output.Length < size)
            throw new ArgumentException("The FFT buffers are smaller than the configured transform.");

        EnsureFloatTwiddles();
        for (int index = 0; index < size; index++)
            output[reversed[index]] = input[index];

        for (int width = 2; width <= size; width <<= 1)
        {
            int half = width >> 1;
            int twiddleStep = size / width;
            for (int start = 0; start < size; start += width)
            {
                for (int offset = 0; offset < half; offset++)
                {
                    int twiddle = offset * twiddleStep;
                    int evenIndex = start + offset;
                    int oddIndex = evenIndex + half;
                    ComplexF even = output[evenIndex];
                    ComplexF odd = output[oddIndex];
                    float oddReal = odd.Real * cosine![twiddle] - odd.Imaginary * sine![twiddle];
                    float oddImaginary = odd.Real * sine[twiddle] + odd.Imaginary * cosine[twiddle];
                    var rotated = new ComplexF(oddReal, oddImaginary);
                    output[evenIndex] = even + rotated;
                    output[oddIndex] = even - rotated;
                }
            }
        }
    }

    public void Forward(Span<Complex> values)
    {
        if (values.Length != size)
            throw new ArgumentException("FFT buffer has an invalid size.", nameof(values));

        EnsureDoubleTwiddles();
        for (int index = 0; index < size; index++)
        {
            int target = reversed[index];
            if (index < target)
                (values[index], values[target]) = (values[target], values[index]);
        }

        for (int width = 2; width <= size; width <<= 1)
        {
            int half = width >> 1;
            int twiddleStep = size / width;
            for (int start = 0; start < size; start += width)
            {
                for (int offset = 0; offset < half; offset++)
                {
                    int twiddle = offset * twiddleStep;
                    Complex even = values[start + offset];
                    Complex odd = values[start + offset + half] *
                        new Complex(doubleCosine![twiddle], doubleSine![twiddle]);
                    values[start + offset] = even + odd;
                    values[start + offset + half] = even - odd;
                }
            }
        }
    }

    private void EnsureFloatTwiddles()
    {
        if (cosine is not null) return;
        var newCosine = new float[size / 2];
        var newSine = new float[size / 2];
        for (int index = 0; index < newCosine.Length; index++)
        {
            float angle = -2 * MathF.PI * index / size;
            newCosine[index] = MathF.Cos(angle);
            newSine[index] = MathF.Sin(angle);
        }
        cosine = newCosine;
        sine = newSine;
    }

    private void EnsureDoubleTwiddles()
    {
        if (doubleCosine is not null) return;
        var newCosine = new double[size / 2];
        var newSine = new double[size / 2];
        for (int index = 0; index < newCosine.Length; index++)
        {
            double angle = -2 * Math.PI * index / size;
            newCosine[index] = Math.Cos(angle);
            newSine[index] = Math.Sin(angle);
        }
        doubleCosine = newCosine;
        doubleSine = newSine;
    }

    private static int Reverse(int value, int bitCount)
    {
        int result = 0;
        for (int bit = 0; bit < bitCount; bit++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }
}
