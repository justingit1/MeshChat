using System.Reflection;
using System.Runtime.Serialization;
using MeshChat.Models;
using MeshChat.Services;
using MeshChat.ViewModels;

namespace MeshChat.Tests;

public sealed class MessageRoutingTests
{
    [Fact]
    public async Task SendToPeerViaTransportAsync_DirectBluetoothPeer_UsesSelectedPeerId()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(
            wifi,
            bluetooth,
            new Peer
            {
                Id = "direct",
                DisplayName = "Direct",
                Transport = TransportType.Bluetooth,
                Status = PeerStatus.Online,
                HopsAway = 1
            });
        var packet = new NetworkPacket { Type = PacketType.Message, TargetId = "direct" };

        var sent = await InvokeSendAsync(vm, "direct", packet);

        Assert.True(sent);
        Assert.Empty(wifi.Sends);
        var send = Assert.Single(bluetooth.Sends);
        Assert.Equal("direct", send.PeerId);
        Assert.Equal("direct", packet.TargetId);
    }

    [Fact]
    public async Task SendToPeerViaTransportAsync_IndirectMessage_SendsToRelayAndKeepsFinalTarget()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(
            wifi,
            bluetooth,
            new Peer
            {
                Id = "relay",
                DisplayName = "Relay",
                Transport = TransportType.WiFi,
                Status = PeerStatus.Online,
                HopsAway = 1
            },
            new Peer
            {
                Id = "target",
                DisplayName = "Target",
                Transport = TransportType.WiFi,
                Status = PeerStatus.Online,
                HopsAway = 2,
                RelayPeerId = "relay"
            });
        var packet = new NetworkPacket { Type = PacketType.Message, TargetId = "target", Ttl = 4 };

        var sent = await InvokeSendAsync(vm, "target", packet);

        Assert.True(sent);
        var send = Assert.Single(wifi.Sends);
        Assert.Equal("relay", send.PeerId);
        Assert.Same(packet, send.Packet);
        Assert.Equal("target", packet.TargetId);
        Assert.Equal(4, packet.Ttl);
        Assert.Empty(packet.VisitedNodes);
        Assert.Empty(bluetooth.Sends);
    }

    [Fact]
    public async Task SendToPeerViaTransportAsync_IndirectMessageWithoutRelay_ReturnsFalse()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(
            wifi,
            bluetooth,
            new Peer
            {
                Id = "target",
                DisplayName = "Target",
                Transport = TransportType.WiFi,
                Status = PeerStatus.Online,
                HopsAway = 2
            });
        var packet = new NetworkPacket { Type = PacketType.Message, TargetId = "target" };

        var sent = await InvokeSendAsync(vm, "target", packet);

        Assert.False(sent);
        Assert.Empty(wifi.Sends);
        Assert.Empty(bluetooth.Sends);
    }

    [Fact]
    public async Task SendToPeerViaTransportAsync_IndirectFileChunk_DoesNotRoute()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(
            wifi,
            bluetooth,
            new Peer
            {
                Id = "relay",
                DisplayName = "Relay",
                Transport = TransportType.WiFi,
                Status = PeerStatus.Online,
                HopsAway = 1
            },
            new Peer
            {
                Id = "target",
                DisplayName = "Target",
                Transport = TransportType.WiFi,
                Status = PeerStatus.Online,
                HopsAway = 2,
                RelayPeerId = "relay"
            });
        var packet = new NetworkPacket { Type = PacketType.FileChunk, TargetId = "target" };

        var sent = await InvokeSendAsync(vm, "target", packet);

        Assert.False(sent);
        Assert.Empty(wifi.Sends);
        Assert.Empty(bluetooth.Sends);
    }

    private static MainViewModel CreateViewModel(
        FakeNetworkService wifi,
        FakeNetworkService bluetooth,
        params Peer[] peers)
    {
#pragma warning disable SYSLIB0050
        var vm = (MainViewModel)FormatterServices.GetUninitializedObject(typeof(MainViewModel));
#pragma warning restore SYSLIB0050
        var peerById = peers.ToDictionary(peer => peer.Id);

        SetField(vm, "_wifi", wifi);
        SetField(vm, "_bluetooth", bluetooth);
        SetField(vm, "_peerById", peerById);

        return vm;
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        var field = typeof(MainViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(instance, value);
    }

    private static async Task<bool> InvokeSendAsync(MainViewModel vm, string peerId, NetworkPacket packet)
    {
        var method = typeof(MainViewModel).GetMethod(
            "SendToPeerViaTransportAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task<bool>)method.Invoke(vm, [peerId, packet, CancellationToken.None])!;
        return await task;
    }

    private sealed class FakeNetworkService : INetworkService
    {
        public List<(string PeerId, NetworkPacket Packet)> Sends { get; } = [];

        public string LocalId { get; set; } = string.Empty;
        public string LocalName { get; set; } = "Local";
        public int ListenPort => 0;
        public bool IsAvailable => true;
        public bool IsRunning => true;

        public event Action<Peer>? PeerDiscovered { add { } remove { } }
        public event Action<string>? PeerLost { add { } remove { } }
        public event Action<NetworkPacket>? PacketReceived { add { } remove { } }
        public event Action<string>? LogMessage { add { } remove { } }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Stop() { }
        public Task StopAsync() => Task.CompletedTask;

        public Task SendToAllAsync(NetworkPacket packet, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SendToPeerAsync(string peerId, NetworkPacket packet, CancellationToken cancellationToken = default)
        {
            Sends.Add((peerId, packet));
            return Task.CompletedTask;
        }

        public Task ConnectToPeerAsync(string address, int? port = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Dispose() { }
    }
}
