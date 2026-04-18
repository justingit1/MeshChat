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

public class Peer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string DisplayName { get; set; } = string.Empty;
    public PeerStatus Status { get; set; } = PeerStatus.Offline;
    public TransportType Transport { get; set; } = TransportType.WiFi;

    // Connection quality (0-100)
    private int _signalStrength = 75; // Default to good
    public int SignalStrength
    {
        get => _signalStrength;
        set
        {
            _signalStrength = Math.Clamp(value, 0, 100);
            OnPropertyChanged(nameof(ConnectionQuality));
        }
    }

    public ConnectionQuality Quality => SignalStrength switch
    {
        >= 80 => ConnectionQuality.Excellent,
        >= 60 => ConnectionQuality.Good,
        >= 40 => ConnectionQuality.Fair,
        > 0 => ConnectionQuality.Poor,
        _ => ConnectionQuality.Unknown
    };

    // WiFi info
    public IPAddress? IpAddress { get; set; }
    public int TcpPort { get; set; }

    // Bluetooth info
    public string? BluetoothAddress { get; set; }

    // Mesh info
    public int HopsAway { get; set; } = 1;  // 1 = direct, 2+ = relayed
    public string? RelayPeerId { get; set; }  // which peer relays to reach this one

    public DateTime LastSeen { get; set; } = DateTime.Now;
    public int UnreadCount { get; set; } = 0;

    public bool IsDirectlyConnected => HopsAway == 1;
    public string HopDescription => HopsAway == 1 ? "Direct" : $"Via {HopsAway - 1} relay";

    // Safe first-letter for avatar — never throws on empty name
    public string InitialLetter => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[0].ToString();

    public event Action? PropertyChanged;
    protected void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke();
}
