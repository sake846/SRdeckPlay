namespace SRdeckCore.SignalProcessing;

/// <summary>Lightweight single-precision complex value for allocation-free DSP kernels.</summary>
public readonly record struct ComplexF(float Real, float Imaginary)
{
    public float MagnitudeSquared => Real * Real + Imaginary * Imaginary;
    public ComplexF Conjugate() => new(Real, -Imaginary);

    public static ComplexF operator +(ComplexF left, ComplexF right) =>
        new(left.Real + right.Real, left.Imaginary + right.Imaginary);

    public static ComplexF operator -(ComplexF left, ComplexF right) =>
        new(left.Real - right.Real, left.Imaginary - right.Imaginary);

    public static ComplexF operator *(ComplexF left, ComplexF right) =>
        new(left.Real * right.Real - left.Imaginary * right.Imaginary,
            left.Real * right.Imaginary + left.Imaginary * right.Real);

    public static ComplexF operator *(ComplexF value, float scalar) =>
        new(value.Real * scalar, value.Imaginary * scalar);

    public static ComplexF operator /(ComplexF value, float scalar) =>
        new(value.Real / scalar, value.Imaginary / scalar);

    public static ComplexF Divide(ComplexF value, ComplexF divisor)
    {
        float power = Math.Max(1e-12f, divisor.MagnitudeSquared);
        return value * divisor.Conjugate() / power;
    }

    public static ComplexF Unit(double radians) =>
        new((float)Math.Cos(radians), (float)Math.Sin(radians));
}
