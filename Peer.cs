using System;
using System.Net;

namespace MeshChat.Models;

public enum PeerStatus
{
    Online,
    Away,
    Offline
}

public enum TransportType
{
    WiFi,
    Bluetooth,
    Both
}

public enum ConnectionQuality
{
    Excellent,
    Good,
    Fair,
    Poor,
    Unknown
}

public record Peer
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string DisplayName { get; init; } = string.Empty;
    public PeerStatus Status { get; init; } = PeerStatus.Offline;
    public TransportType Transport { get; init; } = TransportType.WiFi;

    // Connection quality (0-100)
    public int SignalStrength { get; init; } = 75; // Default to good

    public ConnectionQuality Quality => Math.Clamp(SignalStrength, 0, 100) switch
    {
        >= 80 => ConnectionQuality.Excellent,
        >= 60 => ConnectionQuality.Good,
        >= 40 => ConnectionQuality.Fair,
        > 0 => ConnectionQuality.Poor,
        _ => ConnectionQuality.Unknown
    };

    // WiFi info
    public IPAddress? IpAddress { get; init; }
    public int TcpPort { get; init; }

    // Bluetooth info
    public string? BluetoothAddress { get; init; }

    // Mesh info
    public int HopsAway { get; init; } = 1;
    public string? RelayPeerId { get; init; }
    public DateTime LastSeen { get; init; } = DateTime.Now;
    public int UnreadCount { get; init; }
    public TrustState TrustState { get; init; } = TrustState.Unknown;
    public string FingerprintShort { get; init; } = string.Empty;
    public string SecurityStatus { get; init; } = "Unverified";

    public bool IsDirectlyConnected => HopsAway == 1;
    public string HopDescription => HopsAway == 1 ? "Direct" : $"Via {HopsAway - 1} relay";

    // Safe first-letter for avatar - never throws on empty name
    public string InitialLetter => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[0].ToString();
}
