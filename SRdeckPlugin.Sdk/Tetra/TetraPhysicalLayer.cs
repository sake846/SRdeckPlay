using System.Numerics;

namespace SRdeckPlugin.Sdk.Tetra;

/// <summary>ETSI EN 300 392-2 V2.1.1, clauses 5 and 9.4.4 (continuous downlink bursts).</summary>
public static class TetraPhysicalLayer
{
    public const int SymbolRate = 18_000;
    public const int SampleRate = 144_000;
    public const int SamplesPerSymbol = SampleRate / SymbolRate;
    public const int SlotSymbols = 255;
    public const int SlotBits = 510;
    public const int SlotsPerFrame = 4;
    public const int BandwidthHz = 25_000;
    public const double RollOff = 0.35;

    public sealed record SyncPattern(string Channel, ulong Word, int SyncSymbols, int SyncSymbolOffset,
        int FirstPayloadSymbolOffset, int FirstPayloadSymbols, int ControlSymbolOffset,
        int ControlSymbols, int SecondControlSymbolOffset, int SecondControlSymbols,
        int SecondPayloadSymbolOffset, int SecondPayloadSymbols)
    {
        public string SyncHex => Word.ToString($"X{(SyncSymbols * 2 + 3) / 4}");
    }

    // Normal burst: BN245-266; synchronization burst: BN215-252.
    public static IReadOnlyList<SyncPattern> SyncPatterns { get; } = Array.AsReadOnly(new[]
    {
        new SyncPattern("NDB-1", 0b1101000011101001110100UL, 11, 122, 7, 108, 115, 7, 133, 8, 141, 108),
        new SyncPattern("NDB-2", 0b0111101001000011011110UL, 11, 122, 7, 108, 115, 7, 133, 8, 141, 108),
        new SyncPattern("SDB", 0b11000001100111001110100111000001100111UL, 19, 107, 47, 60, 126, 15, 0, 0, 141, 108)
    });

    private static readonly Complex[] CandidatePoints =
    [
        Complex.FromPolarCoordinates(1, Math.PI / 4),
        Complex.FromPolarCoordinates(1, 3 * Math.PI / 4),
        Complex.FromPolarCoordinates(1, -Math.PI / 4),
        Complex.FromPolarCoordinates(1, -3 * Math.PI / 4)
    ];

    public static Complex Map(byte dibit)
    {
        if (dibit > 3) throw new ArgumentOutOfRangeException(nameof(dibit));
        return CandidatePoints[dibit];
    }

    public static byte Slice(Complex value)
    {
        if (!double.IsFinite(value.Real) || !double.IsFinite(value.Imaginary))
            throw new ArgumentException("Non-finite constellation point.", nameof(value));
        return (byte)((value.Imaginary < 0 ? 2 : 0) | (value.Real < 0 ? 1 : 0));
    }

    public static byte SyncDibit(SyncPattern pattern, int index) =>
        (byte)((pattern.Word >> ((pattern.SyncSymbols - 1 - index) * 2)) & 3);

    public static byte[] Pack(ReadOnlySpan<byte> dibits)
    {
        var result = new byte[(dibits.Length + 3) / 4];
        for (int i = 0; i < dibits.Length; i++)
        {
            if (dibits[i] > 3) throw new ArgumentException("Invalid dibit.", nameof(dibits));
            result[i / 4] |= (byte)(dibits[i] << ((3 - i % 4) * 2));
        }
        return result;
    }

    public static byte[] ExtractPayload(ReadOnlySpan<byte> slot, SyncPattern pattern)
    {
        if (slot.Length != SlotSymbols) throw new ArgumentException("Expected 255 symbols.", nameof(slot));
        var dibits = new byte[pattern.FirstPayloadSymbols + pattern.SecondPayloadSymbols];
        slot.Slice(pattern.FirstPayloadSymbolOffset, pattern.FirstPayloadSymbols).CopyTo(dibits);
        slot.Slice(pattern.SecondPayloadSymbolOffset, pattern.SecondPayloadSymbols).CopyTo(dibits.AsSpan(pattern.FirstPayloadSymbols));
        return Pack(dibits);
    }

    public static byte[] ExtractControl(ReadOnlySpan<byte> slot, SyncPattern pattern)
    {
        if (slot.Length != SlotSymbols) throw new ArgumentException("Expected 255 symbols.", nameof(slot));
        var dibits = new byte[pattern.ControlSymbols + pattern.SecondControlSymbols];
        slot.Slice(pattern.ControlSymbolOffset, pattern.ControlSymbols).CopyTo(dibits);
        slot.Slice(pattern.SecondControlSymbolOffset, pattern.SecondControlSymbols).CopyTo(dibits.AsSpan(pattern.ControlSymbols));
        return Pack(dibits);
    }

    public static double[] CreateRootRaisedCosine()
    {
        const int half = 5 * SamplesPerSymbol;
        var taps = new double[half * 2 + 1];
        for (int n = -half; n <= half; n++)
        {
            double t = n / (double)SamplesPerSymbol, a = RollOff;
            taps[n + half] = n == 0 ? 1 + a * (4 / Math.PI - 1)
                : Math.Abs(Math.Abs(4 * a * t) - 1) < 1e-10
                ? a / Math.Sqrt(2) * ((1 + 2 / Math.PI) * Math.Sin(Math.PI / (4 * a)) +
                    (1 - 2 / Math.PI) * Math.Cos(Math.PI / (4 * a)))
                : (Math.Sin(Math.PI * t * (1 - a)) + 4 * a * t * Math.Cos(Math.PI * t * (1 + a))) /
                    (Math.PI * t * (1 - 16 * a * a * t * t));
        }
        double norm = Math.Sqrt(taps.Sum(t => t * t));
        for (int i = 0; i < taps.Length; i++) taps[i] /= norm;
        return taps;
    }
}
