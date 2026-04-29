using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// Loads a user-provided tuning protocol descriptor and builds UART send payloads.
/// </summary>
public interface ITuningProtocolService
{
    Task<TuningProtocolDescriptor> LoadDescriptorAsync(string descriptorPath, CancellationToken cancellationToken = default);

    Task<TuningBuildResult> BuildAsync(
        string binPath,
        TuningProtocolDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken = default);
}

public sealed class TuningBuildResult
{
    public required string BinFilePath { get; init; }

    public required string BinSha256 { get; init; }

    public required IReadOnlyList<TuningSendSegment> SendSegments { get; init; }

    public int PacketCount { get; init; }

    public int DspMessageLength { get; init; }

    public int HeaderInfoLength { get; init; }

    public int DelayBetweenPacketsMs { get; init; }

    public int TotalBytes => SendSegments.Sum(segment => segment.Data.Length);
}

public sealed class TuningSendSegment
{
    public required byte[] Data { get; init; }

    public bool IsPacket { get; init; }
}
