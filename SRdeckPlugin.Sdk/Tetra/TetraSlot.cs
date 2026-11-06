namespace SRdeckPlugin.Sdk.Tetra;

/// <summary>A physical slot candidate, not a decoded or CRC-verified message.</summary>
public sealed record TetraSlot(
    long Number, DateTimeOffset ReceivedAt, Guid StreamId, long SourceSamplePosition,
    int SourceSampleRateHz, long FrequencyHz, string Direction, string Channel, int? SlotIndex,
    string SyncHex, string SlotHex, string ControlHex, int ControlBitLength,
    string PayloadHex, int PayloadBitLength,
    double FrequencyOffsetHz, double SyncQuality, double Evm, bool InvertQ)
{
    public string MappingProfile => "tetra-pi4-dqpsk-etsi-en-300-392-2";
    public int SlotHexBitOffset { get; init; }
    public int SlotHexBitLength { get; init; } = 510;
    public bool PayloadVerified => false;
    public string Validation => "TETRA下り物理バースト候補 / FEC・CRC・音声未復号";
    public string Time => ReceivedAt.ToLocalTime().ToString("HH:mm:ss.fff");
    public string SlotText => SlotIndex?.ToString() ?? "—";
}
