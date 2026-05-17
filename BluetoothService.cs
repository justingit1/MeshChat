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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace MeshChat.Services;

public class BluetoothService : INetworkService
{
    private const int MaxPacketIdCacheSize = 10000; // Prevent unbounded growth
    private const string TransportName = "Bluetooth";
    private readonly ILogger<BluetoothService> _logger;
    private BluetoothListener? _listener;
    private bool _running;
    private readonly Guid _serviceGuid = new Guid("7b713000-019d-4001-923f-917300f8623d");
    private CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<string, BluetoothClient> _connections = new();
    private readonly ConcurrentDictionary<string, byte> _seenPacketIds = new();
    private readonly ConcurrentQueue<string> _seenPacketOrder = new();

    public string LocalName { get; set; } = Environment.MachineName;
    public string LocalId { get; set; } = string.Empty;
    public int ListenPort => 0;
    public bool IsAvailable { get; private set; }
    public bool IsRunning { get; private set; }

    public event Action<Peer>? PeerDiscovered;
    public event Action<string>? PeerLost;
    public event Action<NetworkPacket>? PacketReceived;
    public event Action<string>? LogMessage;

    public BluetoothService(ILogger<BluetoothService>? logger = null)
    {
        _logger = logger ?? NullLogger<BluetoothService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Check whether a Bluetooth radio is present before doing anything
            // Use Task.Run with timeout to prevent hanging on systems without BT
            var radioTask = Task.Run(() => BluetoothRadio.Default, cancellationToken);
            if (!radioTask.Wait(TimeSpan.FromSeconds(3), cancellationToken))
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

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
                await Task.Delay(2000, _cts.Token); // Wait for app to fully start
                await DiscoveryLoop(_cts.Token);
            }, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            IsAvailable = false;
        }
        catch (Exception ex)
        {
            LogWarning(ex, "BT Error: {Message}", ex.Message);
            IsAvailable = false;
        }
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _running = false;
        _cts.Cancel();
        try { _listener?.Stop(); } catch (Exception ex) { _logger.LogError(ex, "Bluetooth stop error"); }

        foreach (var conn in _connections.Values)
        {
            try { conn.Close(); } catch (Exception ex) { _logger.LogError(ex, "Bluetooth conn close error"); }
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
                LogWarning(ex, "BT accept error: {Message}", ex.Message);
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
                    }, ct);
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
                    await RelayPacketAsync(packet, ct);
                }

                PacketReceived?.Invoke(packet);
            }
        }
        catch (Exception ex)
        {
            LogWarning(ex, "BT client error: {Message}", ex.Message);
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

    private async Task RelayPacketAsync(NetworkPacket packet, CancellationToken cancellationToken)
    {
        var tasks = _connections
            .Where(kvp => !packet.VisitedNodes.Contains(kvp.Key))
            .Select(kvp => SendPacketToClientAsync(kvp.Value, packet, cancellationToken));
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

    public async Task SendToAllAsync(NetworkPacket packet, CancellationToken cancellationToken = default)
    {
        packet.SenderId = LocalId;
        packet.SenderName = LocalName;
        MarkPacketSeen(packet.Id);

        var tasks = _connections.Values.Select(c => SendPacketToClientAsync(c, packet, cancellationToken));
        await Task.WhenAll(tasks);
    }

    public async Task SendToPeerAsync(string peerId, NetworkPacket packet, CancellationToken cancellationToken = default)
    {
        packet.SenderId = LocalId;
        packet.SenderName = LocalName;
        MarkPacketSeen(packet.Id);

        if (_connections.TryGetValue(peerId, out var client))
            await SendPacketToClientAsync(client, packet, cancellationToken);
    }

    private async Task SendPacketToClientAsync(BluetoothClient client, NetworkPacket packet, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonConvert.SerializeObject(packet) + "\n";
            var bytes = Encoding.UTF8.GetBytes(json);
            await client.GetStream().WriteAsync(bytes, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogWarning(ex, "BT send error: {Message}", ex.Message);
        }
    }

    public Task ConnectToPeerAsync(string address, int? port = null, CancellationToken cancellationToken = default)
    {
        // Note: Direct Bluetooth connections require pairing first
        // The device will be discovered automatically via DiscoveryLoop
        // This method triggers an immediate discovery to find the device
        Log($"Bluetooth connect requested for {address} - device should appear via discovery");
        return Task.CompletedTask;
    }

    private async Task DiscoveryLoop(CancellationToken cancellationToken)
    {
        while (_running && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Use async device discovery with timeout to avoid hanging
                var discoveryTask = Task.Run(() =>
                {
                    using var client = new BluetoothClient();
                    return client.DiscoverDevices();
                }, cancellationToken);

                var timeoutTask = Task.Delay(8000, cancellationToken);
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
                LogWarning(ex, "BT discovery error: {Message}", ex.Message);
            }

            await Task.Delay(10000, cancellationToken);
        }
    }

    private void Log(string msg)
    {
        _logger.LogInformation("{Transport} {Message}", TransportName, msg);
        LogMessage?.Invoke($"[{TransportName}] {msg}");
    }

    private void LogWarning(Exception exception, string message, params object?[] args)
    {
        _logger.LogWarning(exception, message, args);
        LogMessage?.Invoke($"[{TransportName}] {FormatLogMessage(message, args)}");
    }

    private static string FormatLogMessage(string template, object?[] args)
    {
        var result = new StringBuilder();
        var argIndex = 0;

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '{')
            {
                var end = template.IndexOf('}', i + 1);
                if (end > i)
                {
                    result.Append(argIndex < args.Length ? args[argIndex++] : template[i..(end + 1)]);
                    i = end;
                    continue;
                }
            }

            result.Append(template[i]);
        }

        return result.ToString();
    }

    public void Dispose() => Stop();
}
