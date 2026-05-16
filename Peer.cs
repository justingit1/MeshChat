using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;

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

public class Peer : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString();
    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    private string _displayName = string.Empty;
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
                OnPropertyChanged(nameof(InitialLetter));
        }
    }

    private PeerStatus _status = PeerStatus.Offline;
    public PeerStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    private TransportType _transport = TransportType.WiFi;
    public TransportType Transport
    {
        get => _transport;
        set => SetProperty(ref _transport, value);
    }

    // Connection quality (0-100)
    private int _signalStrength = 75; // Default to good
    public int SignalStrength
    {
        get => _signalStrength;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _signalStrength, clamped))
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
    private IPAddress? _ipAddress;
    public IPAddress? IpAddress
    {
        get => _ipAddress;
        set => SetProperty(ref _ipAddress, value);
    }

    private int _tcpPort;
    public int TcpPort
    {
        get => _tcpPort;
        set => SetProperty(ref _tcpPort, value);
    }

    // Bluetooth info
    private string? _bluetoothAddress;
    public string? BluetoothAddress
    {
        get => _bluetoothAddress;
        set => SetProperty(ref _bluetoothAddress, value);
    }

    // Mesh info
    private int _hopsAway = 1;
    public int HopsAway
    {
        get => _hopsAway;
        set
        {
            if (SetProperty(ref _hopsAway, value))
            {
                OnPropertyChanged(nameof(IsDirectlyConnected));
                OnPropertyChanged(nameof(HopDescription));
            }
        }
    }

    private string? _relayPeerId;
    public string? RelayPeerId
    {
        get => _relayPeerId;
        set => SetProperty(ref _relayPeerId, value);
    }

    private DateTime _lastSeen = DateTime.Now;
    public DateTime LastSeen
    {
        get => _lastSeen;
        set => SetProperty(ref _lastSeen, value);
    }

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set => SetProperty(ref _unreadCount, value);
    }

    public bool IsDirectlyConnected => HopsAway == 1;
    public string HopDescription => HopsAway == 1 ? "Direct" : $"Via {HopsAway - 1} relay";

    // Safe first-letter for avatar — never throws on empty name
    public string InitialLetter => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[0].ToString();

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
