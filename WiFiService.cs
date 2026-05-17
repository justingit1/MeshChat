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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace MeshChat.Services;

public interface INetworkService : IDisposable
{
    string LocalId { get; set; }
    string LocalName { get; set; }
    int ListenPort { get; }
    bool IsAvailable { get; }
    bool IsRunning { get; }

    // Transport implementations raise these events so callers can share one
    // receive/discovery pipeline without knowing whether packets came over WiFi or Bluetooth.
    event Action<Peer>? PeerDiscovered;
    event Action<string>? PeerLost;
    event Action<NetworkPacket>? PacketReceived;
    event Action<string>? LogMessage;

    // Send/connect methods accept cancellation tokens for UI shutdown and future
    // operations that need to cancel pending network work through the interface.
    Task StartAsync(CancellationToken cancellationToken = default);
    void Stop();
    Task StopAsync();
    Task SendToAllAsync(NetworkPacket packet, CancellationToken cancellationToken = default);
    Task SendToPeerAsync(string peerId, NetworkPacket packet, CancellationToken cancellationToken = default);
    Task ConnectToPeerAsync(string address, int? port = null, CancellationToken cancellationToken = default);
}

public class WiFiService : INetworkService
{
    private const int DefaultPort = 45678;
    private const string ServiceType = "_meshchat._tcp";
    private const string TransportName = "WiFi";
    private const int MaxPacketIdCacheSize = 10000; // Prevent unbounded growth

    private readonly ILogger<WiFiService> _logger;
    private TcpListener? _listener;
    private MulticastService? _multicast;
    private ServiceDiscovery? _serviceDiscovery;
    private readonly ConcurrentDictionary<string, TcpClient> _connections = new();
    private readonly ConcurrentDictionary<string, byte> _seenPacketIds = new();
    private readonly ConcurrentQueue<string> _seenPacketOrder = new();
    private readonly ConcurrentDictionary<Task, byte> _clientTasks = new();
    private CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private Task? _mdnsTask;

    public string LocalId { get; set; } = Guid.NewGuid().ToString();
    public string LocalName { get; set; } = Environment.MachineName;
    public int ListenPort { get; private set; } = DefaultPort;
    public bool IsAvailable => IsRunning;
    public bool IsRunning { get; private set; }

    public event Action<Peer>? PeerDiscovered;
    public event Action<string>? PeerLost;
    public event Action<NetworkPacket>? PacketReceived;
    public event Action<string>? LogMessage;

    public WiFiService(ILogger<WiFiService>? logger = null)
    {
        _logger = logger ?? NullLogger<WiFiService>.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Find port in background to avoid blocking UI
        ListenPort = await Task.Run(() => FindAvailablePort(DefaultPort), cancellationToken);

        _listener = new TcpListener(IPAddress.Any, ListenPort);
        _listener.Start();
        IsRunning = true;
        Log("TCP server listening on port {ListenPort}", ListenPort);
        _acceptTask = ObserveBackgroundTask(AcceptConnectionsAsync(_cts.Token), "WiFi accept loop");

        // Run mDNS in background to avoid blocking UI
        _mdnsTask = ObserveBackgroundTask(Task.Run(() => StartMdns(_cts.Token), _cts.Token), "WiFi mDNS");
    }

    public void Stop()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        IsRunning = false;
        _listener?.Stop();
        if (_serviceDiscovery != null)
            _serviceDiscovery.ServiceInstanceDiscovered -= OnServiceDiscovered;
        _serviceDiscovery?.Dispose();
        _multicast?.Dispose();
        foreach (var conn in _connections.Values) conn.Close();
        _connections.Clear();
        await WaitForBackgroundTasksAsync();
    }

    private void StartMdns(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        catch (Exception ex) when (IsExpectedShutdown(ex, cancellationToken)) { }
        catch (Exception ex) { LogWarning(ex, "mDNS warning: {Message}", ex.Message); }
    }

    private void OnServiceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        if (!IsRunning || _cts.IsCancellationRequested) return;

        try
        {
            if (e.ServiceInstanceName.ToString().Contains(ServiceType))
                _multicast?.SendQuery(e.ServiceInstanceName);
        }
        catch (ObjectDisposedException) when (_cts.IsCancellationRequested) { }
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                TrackClientTask(HandleClientAsync(client, ct));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (IsExpectedShutdown(ex, ct)) { }
            catch (Exception ex) { _logger.LogError(ex, "WiFi listener error"); }
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
                    if (!IsRunning || ct.IsCancellationRequested) continue;
                    peerId = packet.SenderId;
                    _connections[peerId] = client;
                    var ep = (IPEndPoint)client.Client.RemoteEndPoint!;
                    Log("Incoming connection from {SenderName} ({RemoteAddress})", packet.SenderName, ep.Address);
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
                    await SendHelloAckAsync(client, packet.SenderId, ct);
                }

                // When the remote peer acknowledges our Hello, register the connection
                // so we can send messages back to them (fixes one-way messaging)
                if (packet.Type == PacketType.HelloAck && peerId == null)
                {
                    if (!IsRunning || ct.IsCancellationRequested) continue;
                    peerId = packet.SenderId;
                    _connections[peerId] = client;
                    var ep = (IPEndPoint)client.Client.RemoteEndPoint!;
                    Log("Connection established with {SenderName}", packet.SenderName);
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

                if (!MarkPacketSeen(packet.Id)) continue;

                if (packet.Ttl > 1 && packet.TargetId != LocalId && packet.TargetId != null)
                {
                    await RelayPacketAsync(CreateRelayPacket(packet), ct);
                }

                if (ShouldDeliverToApplication(packet) && IsRunning && !ct.IsCancellationRequested)
                    PacketReceived?.Invoke(packet);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsExpectedShutdown(ex, ct)) { }
        catch (Exception ex) { _logger.LogError(ex, "WiFi listener error"); }
        finally
        {
            if (peerId != null)
            {
                _connections.TryRemove(peerId, out _);
                if (IsRunning && !ct.IsCancellationRequested)
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

    private NetworkPacket CreateRelayPacket(NetworkPacket packet)
    {
        var visited = new System.Collections.Generic.List<string>(packet.VisitedNodes);
        visited.Add(LocalId);

        return new NetworkPacket
        {
            Id = packet.Id,
            Type = packet.Type,
            SenderId = packet.SenderId,
            SenderName = packet.SenderName,
            TargetId = packet.TargetId,
            Ttl = packet.Ttl - 1,
            VisitedNodes = visited.ToArray(),
            CreatedAt = packet.CreatedAt,
            Payload = packet.Payload,
            TcpPort = packet.TcpPort,
            KnownPeers = packet.KnownPeers
        };
    }

    private bool ShouldDeliverToApplication(NetworkPacket packet)
        => packet.TargetId == null || packet.TargetId == LocalId;

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

    private async Task SendPacketToClientAsync(TcpClient client, NetworkPacket packet, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonConvert.SerializeObject(packet) + "\n";
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken);
        }
        catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogError(ex, "WiFi send error"); }
    }

    private async Task SendHelloAckAsync(TcpClient client, string targetId, CancellationToken cancellationToken)
    {
        await SendPacketToClientAsync(client, new NetworkPacket
        {
            Type = PacketType.HelloAck,
            SenderId = LocalId,
            SenderName = LocalName,
            TcpPort = ListenPort,
            TargetId = targetId
        }, cancellationToken);
    }

    private int FindAvailablePort(int preferred)
    {
        try { var l = new TcpListener(IPAddress.Any, preferred); l.Start(); l.Stop(); return preferred; }
        catch
        {
            _logger.LogWarning(
                "Port {PreferredPort} in use, trying {FallbackPort}",
                preferred,
                preferred + 1);
            return preferred + 1;
        }
    }

    public async Task SendToPeerAsync(string peerId, NetworkPacket packet, CancellationToken cancellationToken = default)
    {
        packet.SenderId = LocalId;
        packet.SenderName = LocalName;
        MarkPacketSeen(packet.Id);
        if (_connections.TryGetValue(peerId, out var client))
            await SendPacketToClientAsync(client, packet, cancellationToken);
    }

    public async Task ConnectToPeerAsync(string address, int? port = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = new TcpClient();
            var tcpPort = port ?? DefaultPort;
            await client.ConnectAsync(address, tcpPort, cancellationToken);
            Log("Connected to {Address}:{Port}", address, tcpPort);
            TrackClientTask(HandleClientAsync(client, _cts.Token));

            // Send Hello so the remote peer registers us
            var hello = new NetworkPacket
            {
                Type = PacketType.Hello,
                SenderId = LocalId,
                SenderName = LocalName,
                TcpPort = ListenPort
            };
            await SendPacketToClientAsync(client, hello, cancellationToken);
        }
        catch (Exception ex) { LogWarning(ex, "Connect failed: {Message}", ex.Message); }
    }

    private void Log(string message, params object?[] args)
    {
        _logger.LogInformation(message, args);
        LogMessage?.Invoke($"[{TransportName}] {FormatLogMessage(message, args)}");
    }

    private void LogWarning(Exception exception, string message, params object?[] args)
    {
        _logger.LogWarning(exception, message, args);
        LogMessage?.Invoke($"[{TransportName}] {FormatLogMessage(message, args)}");
    }

    private Task ObserveBackgroundTask(Task task, string operation)
    {
        _ = task.ContinueWith(
            t => _logger.LogError(t.Exception, "{Operation} failed", operation),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        return task;
    }

    private void TrackClientTask(Task task)
    {
        _clientTasks.TryAdd(task, 0);
        _ = ObserveBackgroundTask(task, "WiFi client handler").ContinueWith(
            t => _clientTasks.TryRemove(t, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WaitForBackgroundTasksAsync()
    {
        var tasks = new[] { _acceptTask, _mdnsTask }
            .Where(task => task != null)
            .Cast<Task>()
            .Concat(_clientTasks.Keys)
            .ToArray();

        if (tasks.Length == 0) return;

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Timed out waiting for WiFi background tasks to stop");
        }
        catch (Exception ex) when (IsExpectedShutdown(ex, _cts.Token)) { }
    }

    private static bool IsExpectedShutdown(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return exception is OperationCanceledException
                or ObjectDisposedException
                or SocketException
                or InvalidOperationException;

        return false;
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
