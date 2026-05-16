using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InTheHand.Net.Sockets;
using InTheHand.Net.Bluetooth;
using MeshChat.Models;
using Newtonsoft.Json;

namespace MeshChat.Services;

public class BluetoothService : IDisposable
{
    private const int MaxPacketIdCacheSize = 10000; // Prevent unbounded growth
    private BluetoothListener? _listener;
    private bool _running;
    private readonly Guid _serviceGuid = new Guid("7b713000-019d-4001-923f-917300f8623d");
    private CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<string, BluetoothClient> _connections = new();
    private readonly ConcurrentDictionary<string, byte> _seenPacketIds = new();
    private readonly ConcurrentQueue<string> _seenPacketOrder = new();

    public string LocalName { get; set; } = Environment.MachineName;
    public string LocalId { get; set; } = string.Empty;
    public bool IsAvailable { get; private set; }
    public bool IsRunning { get; private set; }

    public event Action<Peer>? PeerDiscovered;
    public event Action<string>? PeerLost;
    public event Action<NetworkPacket>? PacketReceived;
    public event Action<string>? LogMessage;

    public Task StartAsync()
    {
        try
        {
            // Check whether a Bluetooth radio is present before doing anything
            // Use Task.Run with timeout to prevent hanging on systems without BT
            var radioTask = Task.Run(() => BluetoothRadio.Default);
            if (!radioTask.Wait(TimeSpan.FromSeconds(3)))
            {
                Log("Bluetooth check timed out — disabling BT");
                IsAvailable = false;
                return Task.CompletedTask;
            }

            var radio = radioTask.Result;
            if (radio == null)
            {
                Log("No Bluetooth radio found — Bluetooth disabled.");
                IsAvailable = false;
                return Task.CompletedTask;
            }

            _cts = new CancellationTokenSource();
            _listener = new BluetoothListener(_serviceGuid);
            _listener.Start();
            _running = true;
            IsAvailable = true;
            IsRunning = true;
            Log("Bluetooth listener started");

            // Start accepting incoming connections
            _ = AcceptConnectionsAsync(_cts.Token);

            // Start device discovery after a short delay to not block startup
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000); // Wait for app to fully start
                await DiscoveryLoop();
            });
        }
        catch (Exception ex)
        {
            Log($"BT Error: {ex.Message}");
            IsAvailable = false;
        }
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _running = false;
        _cts.Cancel();
        try { _listener?.Stop(); } catch (Exception ex) { Logger.Error("Bluetooth stop error", ex); }

        foreach (var conn in _connections.Values)
        {
            try { conn.Close(); } catch (Exception ex) { Logger.Error("Bluetooth conn close error", ex); }
        }
        _connections.Clear();
        IsRunning = false;
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _running)
        {
            try
            {
                var client = _listener!.AcceptBluetoothClient();
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"BT accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(BluetoothClient client, CancellationToken ct)
    {
        string? peerId = null;
        string? peerName = null;
        string? deviceAddress = null;

        try
        {
            // Get remote device address
            try
            {
                deviceAddress = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            }
            catch { deviceAddress = "unknown"; }

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!ct.IsCancellationRequested && client.Connected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;

                var packet = JsonConvert.DeserializeObject<NetworkPacket>(line);
                if (packet == null) continue;

                // Register peer on Hello
                if (packet.Type == PacketType.Hello)
                {
                    peerId = packet.SenderId;
                    peerName = packet.SenderName;
                    _connections[peerId] = client;

                    PeerDiscovered?.Invoke(new Peer
                    {
                        Id = packet.SenderId,
                        DisplayName = packet.SenderName,
                        Status = PeerStatus.Online,
                        Transport = MeshChat.Models.TransportType.Bluetooth,
                        BluetoothAddress = deviceAddress,
                        HopsAway = 1
                    });

                    // Send HelloAck
                    await SendPacketToClientAsync(client, new NetworkPacket
                    {
                        Type = PacketType.HelloAck,
                        SenderId = LocalId,
                        SenderName = LocalName,
                        TargetId = peerId
                    });
                }

                // Register peer on HelloAck
                if (packet.Type == PacketType.HelloAck && peerId == null)
                {
                    peerId = packet.SenderId;
                    peerName = packet.SenderName;
                    _connections[peerId] = client;

                    PeerDiscovered?.Invoke(new Peer
                    {
                        Id = packet.SenderId,
                        DisplayName = packet.SenderName,
                        Status = PeerStatus.Online,
                        Transport = MeshChat.Models.TransportType.Bluetooth,
                        BluetoothAddress = deviceAddress,
                        HopsAway = 1
                    });
                }

                if (!MarkPacketSeen(packet.Id)) continue;

                // Relay if needed
                if (packet.Ttl > 1 && packet.TargetId != LocalId && packet.TargetId != null)
                {
                    packet.Ttl--;
                    var visited = packet.VisitedNodes.ToList();
                    visited.Add(LocalId);
                    packet.VisitedNodes = visited.ToArray();
                    await RelayPacketAsync(packet);
                }

                PacketReceived?.Invoke(packet);
            }
        }
        catch (Exception ex)
        {
            Log($"BT client error: {ex.Message}");
        }
        finally
        {
            if (peerId != null)
            {
                _connections.TryRemove(peerId, out _);
                PeerLost?.Invoke(peerId);
            }
            client.Dispose();
        }
    }

    private async Task RelayPacketAsync(NetworkPacket packet)
    {
        var tasks = _connections
            .Where(kvp => !packet.VisitedNodes.Contains(kvp.Key))
            .Select(kvp => SendPacketToClientAsync(kvp.Value, packet));
        await Task.WhenAll(tasks);
    }

    private bool MarkPacketSeen(string packetId)
    {
        if (!_seenPacketIds.TryAdd(packetId, 0))
            return false;

        _seenPacketOrder.Enqueue(packetId);
        while (_seenPacketIds.Count > MaxPacketIdCacheSize &&
               _seenPacketOrder.TryDequeue(out var oldPacketId))
        {
            _seenPacketIds.TryRemove(oldPacketId, out _);
        }

        return true;
    }

    public async Task SendToAllAsync(NetworkPacket packet)
    {
        packet.SenderId = LocalId;
        packet.SenderName = LocalName;
        MarkPacketSeen(packet.Id);

        var tasks = _connections.Values.Select(c => SendPacketToClientAsync(c, packet));
        await Task.WhenAll(tasks);
    }

    public async Task SendToPeerAsync(string peerId, NetworkPacket packet)
    {
        packet.SenderId = LocalId;
        packet.SenderName = LocalName;
        MarkPacketSeen(packet.Id);

        if (_connections.TryGetValue(peerId, out var client))
            await SendPacketToClientAsync(client, packet);
    }

    private async Task SendPacketToClientAsync(BluetoothClient client, NetworkPacket packet)
    {
        try
        {
            var json = JsonConvert.SerializeObject(packet) + "\n";
            var bytes = Encoding.UTF8.GetBytes(json);
            await client.GetStream().WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            Log($"BT send error: {ex.Message}");
        }
    }

    public async Task ConnectToPeerAsync(string bluetoothAddress)
    {
        // Note: Direct Bluetooth connections require pairing first
        // The device will be discovered automatically via DiscoveryLoop
        // This method triggers an immediate discovery to find the device
        Log($"Bluetooth connect requested for {bluetoothAddress} - device should appear via discovery");
    }

    private async Task DiscoveryLoop()
    {
        while (_running)
        {
            try
            {
                // Use async device discovery with timeout to avoid hanging
                var discoveryTask = Task.Run(() =>
                {
                    using var client = new BluetoothClient();
                    return client.DiscoverDevices();
                });

                var timeoutTask = Task.Delay(8000);
                var completedTask = await Task.WhenAny(discoveryTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    Log("BT discovery timed out");
                    continue;
                }

                var devices = await discoveryTask;

                if (devices.Count > 0)
                {
                    Log($"Found {devices.Count} Bluetooth device(s)");
                }

                foreach (var device in devices)
                {
                    var address = device.DeviceAddress.ToString();

                    // Only announce discovery if we don't already have a connection
                    if (!_connections.ContainsKey(address))
                    {
                        PeerDiscovered?.Invoke(new Peer
                        {
                            Id = address,
                            DisplayName = device.DeviceName,
                            Status = PeerStatus.Online,
                            Transport = MeshChat.Models.TransportType.Bluetooth,
                            BluetoothAddress = address,
                            HopsAway = 1
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"BT discovery error: {ex.Message}");
            }

            await Task.Delay(10000);
        }
    }

    private void Log(string msg) => LogMessage?.Invoke($"[Bluetooth] {msg}");
    public void Dispose() => Stop();
}
