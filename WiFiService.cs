using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshChat.Models;
using Makaretu.Dns;
using Newtonsoft.Json;

namespace MeshChat.Services;

public class WiFiService : IDisposable
{
    private const int DefaultPort = 45678;
    private const string ServiceType = "_meshchat._tcp";
    private const int MaxPacketIdCacheSize = 10000; // Prevent unbounded growth

    private TcpListener? _listener;
    private MulticastService? _multicast;
    private ServiceDiscovery? _serviceDiscovery;
    private readonly ConcurrentDictionary<string, TcpClient> _connections = new();
    private readonly ConcurrentDictionary<string, string> _seenPacketIds = new();
    private CancellationTokenSource _cts = new();

    public string LocalId { get; private set; } = Guid.NewGuid().ToString();
    public string LocalName { get; set; } = Environment.MachineName;
    public int ListenPort { get; private set; } = DefaultPort;
    public bool IsRunning { get; private set; }

    public event Action<Peer>? PeerDiscovered;
    public event Action<string>? PeerLost;
    public event Action<NetworkPacket>? PacketReceived;
    public event Action<string>? LogMessage;

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();

        // Find port in background to avoid blocking UI
        ListenPort = await Task.Run(() => FindAvailablePort(DefaultPort));

        _listener = new TcpListener(IPAddress.Any, ListenPort);
        _listener.Start();
        IsRunning = true;
        Log($"TCP server listening on port {ListenPort}");
        _ = AcceptConnectionsAsync(_cts.Token);

        // Run mDNS in background to avoid blocking UI
        _ = Task.Run(StartMdns);
    }

    public void Stop()
    {
        _cts.Cancel();
        _listener?.Stop();
        _serviceDiscovery?.Dispose();
        _multicast?.Dispose();
        foreach (var conn in _connections.Values) conn.Close();
        _connections.Clear();
        IsRunning = false;
    }

    private void StartMdns()
    {
        try
        {
            _multicast = new MulticastService();
            _serviceDiscovery = new ServiceDiscovery(_multicast);
            var profile = new ServiceProfile(
                $"{LocalName}-{LocalId.Substring(0, 8)}",
                ServiceType,
                (ushort)ListenPort);
            profile.AddProperty("id", LocalId);
            profile.AddProperty("name", LocalName);
            _serviceDiscovery.Advertise(profile);
            _serviceDiscovery.ServiceInstanceDiscovered += OnServiceDiscovered;
            _multicast.Start();
            _serviceDiscovery.QueryAllServices();
        }
        catch (Exception ex) { Log($"mDNS warning: {ex.Message}"); }
    }

    private void OnServiceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        if (e.ServiceInstanceName.ToString().Contains(ServiceType))
            _multicast?.SendQuery(e.ServiceInstanceName);
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Error("WiFi listener error", ex); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        string? peerId = null;
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!ct.IsCancellationRequested && client.Connected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                var packet = JsonConvert.DeserializeObject<NetworkPacket>(line);
                if (packet == null) continue;

                if (packet.Type == PacketType.Hello)
                {
                    peerId = packet.SenderId;
                    _connections[peerId] = client;
                    var ep = (IPEndPoint)client.Client.RemoteEndPoint!;
                    Log($"Incoming connection from {packet.SenderName} ({ep.Address})");
                    PeerDiscovered?.Invoke(new Peer
                    {
                        Id = packet.SenderId,
                        DisplayName = packet.SenderName,
                        Status = PeerStatus.Online,
                        Transport = MeshChat.Models.TransportType.WiFi,
                        IpAddress = ep.Address,
                        TcpPort = packet.TcpPort,
                        HopsAway = 1
                    });
                    await SendHelloAckAsync(client, packet.SenderId);
                }

                // When the remote peer acknowledges our Hello, register the connection
                // so we can send messages back to them (fixes one-way messaging)
                if (packet.Type == PacketType.HelloAck && peerId == null)
                {
                    peerId = packet.SenderId;
                    _connections[peerId] = client;
                    var ep = (IPEndPoint)client.Client.RemoteEndPoint!;
                    Log($"Connection established with {packet.SenderName}");
                    PeerDiscovered?.Invoke(new Peer
                    {
                        Id = packet.SenderId,
                        DisplayName = packet.SenderName,
                        Status = PeerStatus.Online,
                        Transport = MeshChat.Models.TransportType.WiFi,
                        IpAddress = ep.Address,
                        TcpPort = ListenPort,
                        HopsAway = 1
                    });
                }

                if (_seenPacketIds.ContainsKey(packet.Id)) continue;
                _seenPacketIds[packet.Id] = string.Empty;
                CleanupPacketIdCache();

                // FIXED: Removed the [...] syntax that caused the error on line 123
                if (packet.Ttl > 1 && packet.TargetId != LocalId && packet.TargetId != null)
                {
                    packet.Ttl--;
                    var visited = new System.Collections.Generic.List<string>(packet.VisitedNodes);
                    visited.Add(LocalId);
                    packet.VisitedNodes = visited.ToArray();
                    await RelayPacketAsync(packet);
                }

                PacketReceived?.Invoke(packet);
            }
        }
        catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Error("WiFi listener error", ex); }
        finally
        {
            if (peerId != null) { _connections.TryRemove(peerId, out _); PeerLost?.Invoke(peerId); }
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

    private void CleanupPacketIdCache()
    {
        if (_seenPacketIds.Count > MaxPacketIdCacheSize)
        {
            // Clear half the entries when exceeding limit
            var keysToRemove = _seenPacketIds.Keys.Take(_seenPacketIds.Count / 2).ToList();
            foreach (var key in keysToRemove)
            {
                _seenPacketIds.TryRemove(key, out _);
            }
        }
    }

    public async Task SendToAllAsync(NetworkPacket packet)
    {
        packet.SenderId = LocalId;
        packet.SenderName = LocalName;
        _seenPacketIds[packet.Id] = string.Empty;
        CleanupPacketIdCache();
        var tasks = _connections.Values.Select(c => SendPacketToClientAsync(c, packet));
        await Task.WhenAll(tasks);
    }

    private async Task SendPacketToClientAsync(TcpClient client, NetworkPacket packet)
    {
        try
        {
            var json = JsonConvert.SerializeObject(packet) + "\n";
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Error("WiFi listener error", ex); }
    }

    private async Task SendHelloAckAsync(TcpClient client, string targetId)
    {
        await SendPacketToClientAsync(client, new NetworkPacket
        {
            Type = PacketType.HelloAck,
            SenderId = LocalId,
            SenderName = LocalName,
            TcpPort = ListenPort,
            TargetId = targetId
        });
    }

    private static int FindAvailablePort(int preferred)
    {
        try { var l = new TcpListener(IPAddress.Any, preferred); l.Start(); l.Stop(); return preferred; }
        catch { Logger.Warning($"Port {preferred} in use, trying {preferred + 1}"); return preferred + 1; }
    }

    public async Task SendToPeerAsync(string peerId, NetworkPacket packet)
    {
        packet.SenderId = LocalId;
        packet.SenderName = LocalName;
        _seenPacketIds[packet.Id] = string.Empty;
        CleanupPacketIdCache();
        if (_connections.TryGetValue(peerId, out var client))
            await SendPacketToClientAsync(client, packet);
    }

    public async Task ConnectToPeerAsync(string ipAddress, int port)
    {
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(ipAddress, port);
            Log($"Connected to {ipAddress}:{port}");
            _ = HandleClientAsync(client, _cts.Token);

            // Send Hello so the remote peer registers us
            var hello = new NetworkPacket
            {
                Type = PacketType.Hello,
                SenderId = LocalId,
                SenderName = LocalName,
                TcpPort = ListenPort
            };
            await SendPacketToClientAsync(client, hello);
        }
        catch (Exception ex) { Log($"Connect failed: {ex.Message}"); }
    }

    private void Log(string msg) => LogMessage?.Invoke($"[WiFi] {msg}");
    public void Dispose() => Stop();
}