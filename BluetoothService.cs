using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InTheHand.Net;
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
    private readonly ConcurrentDictionary<Task, byte> _clientTasks = new();
    private Task? _acceptTask;
    private Task? _discoveryTask;

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

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Check whether a Bluetooth radio is present before doing anything
            // Use Task.Run with timeout to prevent hanging on systems without BT.
            var radioTask = Task.Run(() => BluetoothRadio.Default, cancellationToken);
            _ = ObserveBackgroundTask(radioTask, "Bluetooth radio check");
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(3));
            if (await Task.WhenAny(radioTask, timeoutTask) != radioTask)
            {
                Log("Bluetooth check timed out - disabling BT");
                IsAvailable = false;
                return;
            }

            var radio = await radioTask;
            if (radio == null)
            {
                Log("No Bluetooth radio found - Bluetooth disabled.");
                IsAvailable = false;
                return;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listener = new BluetoothListener(_serviceGuid);
            _listener.Start();
            _running = true;
            IsAvailable = true;
            IsRunning = true;
            Log("Bluetooth listener started");

            // Start accepting incoming connections
            _acceptTask = ObserveBackgroundTask(AcceptConnectionsAsync(_cts.Token), "Bluetooth accept loop");

            // Start device discovery after a short delay to not block startup
            _discoveryTask = ObserveBackgroundTask(Task.Run(async () =>
            {
                await Task.Delay(2000, _cts.Token); // Wait for app to fully start
                await DiscoveryLoop(_cts.Token);
            }, _cts.Token), "Bluetooth discovery loop");
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
    }

    public void Stop()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    public async Task StopAsync()
    {
        _running = false;
        _cts.Cancel();
        IsRunning = false;
        try { _listener?.Stop(); } catch (Exception ex) { _logger.LogError(ex, "Bluetooth stop error"); }

        foreach (var conn in _connections.Values)
        {
            try { conn.Close(); } catch (Exception ex) { _logger.LogError(ex, "Bluetooth conn close error"); }
        }
        _connections.Clear();
        await WaitForBackgroundTasksAsync();
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _running)
        {
            try
            {
                var client = await Task.Run(() => _listener!.AcceptBluetoothClient(), ct);
                TrackClientTask(HandleClientAsync(client, ct));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (IsExpectedShutdown(ex, ct)) { break; }
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
                    if (!IsRunning || ct.IsCancellationRequested) continue;
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
                    if (!IsRunning || ct.IsCancellationRequested) continue;
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
                if (ShouldRelayPacket(packet))
                {
                    await RelayPacketAsync(CreateRelayPacket(packet), ct);
                }

                if (ShouldDeliverToApplication(packet) && IsRunning && !ct.IsCancellationRequested)
                    PacketReceived?.Invoke(packet);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsExpectedShutdown(ex, ct)) { }
        catch (Exception ex)
        {
            LogWarning(ex, "BT client error: {Message}", ex.Message);
        }
        finally
        {
            if (peerId != null)
            {
                var removedCurrentConnection =
                    ((ICollection<KeyValuePair<string, BluetoothClient>>)_connections)
                    .Remove(new KeyValuePair<string, BluetoothClient>(peerId, client));
                if (removedCurrentConnection && IsRunning && !ct.IsCancellationRequested)
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
        var visited = packet.VisitedNodes.ToList();
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
            IsEncrypted = packet.IsEncrypted,
            CryptoVersion = packet.CryptoVersion,
            CryptoSessionId = packet.CryptoSessionId,
            CryptoKeyId = packet.CryptoKeyId,
            CryptoNonce = packet.CryptoNonce,
            CryptoTag = packet.CryptoTag,
            CryptoMessageCounter = packet.CryptoMessageCounter,
            TcpPort = packet.TcpPort,
            KnownPeers = packet.KnownPeers
        };
    }

    private bool ShouldDeliverToApplication(NetworkPacket packet)
        => packet.TargetId == null || packet.TargetId == LocalId;

    private bool ShouldRelayPacket(NetworkPacket packet)
        => packet.Ttl > 1
           && packet.TargetId != LocalId
           && (packet.TargetId != null || packet.Type == PacketType.Message);

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

    public async Task ConnectToPeerAsync(string address, int? port = null, CancellationToken cancellationToken = default)
    {
        BluetoothClient? client = null;

        try
        {
            if (!IsAvailable || !IsRunning)
            {
                Log($"Bluetooth connect skipped for {address}: Bluetooth is not available");
                return;
            }

            if (!BluetoothAddress.TryParse(address, out var bluetoothAddress))
            {
                Log($"Bluetooth connect failed: invalid address {address}");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            client = new BluetoothClient();
            await client.ConnectAsync(bluetoothAddress, _serviceGuid).WaitAsync(cancellationToken);
            Log($"Connected to Bluetooth peer {address}");

            TrackClientTask(HandleClientAsync(client, _cts.Token));

            await SendPacketToClientAsync(client, new NetworkPacket
            {
                Type = PacketType.Hello,
                SenderId = LocalId,
                SenderName = LocalName
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            client = null;
        }
        catch (OperationCanceledException)
        {
            Log($"Bluetooth connect cancelled for {address}");
        }
        catch (Exception ex)
        {
            LogWarning(ex, "BT connect failed for {Address}: {Message}", address, ex.Message);
        }
        finally
        {
            client?.Dispose();
        }
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

            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (IsExpectedShutdown(ex, cancellationToken)) { break; }
            catch (Exception ex)
            {
                LogWarning(ex, "BT discovery error: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(10000, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
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
        _ = ObserveBackgroundTask(task, "Bluetooth client handler").ContinueWith(
            t => _clientTasks.TryRemove(t, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WaitForBackgroundTasksAsync()
    {
        var tasks = new[] { _acceptTask, _discoveryTask }
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
            _logger.LogWarning(ex, "Timed out waiting for Bluetooth background tasks to stop");
        }
        catch (Exception ex) when (IsExpectedShutdown(ex, _cts.Token)) { }
    }

    private static bool IsExpectedShutdown(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return exception is OperationCanceledException
                or ObjectDisposedException
                or IOException
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
