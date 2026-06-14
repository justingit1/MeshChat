using MeshChat.Models;
using MeshChat.Services;
using MeshChat.Services.Crypto;
using MeshChat.ViewModels;
using Newtonsoft.Json;

namespace MeshChat.Tests;

public sealed class MainViewModelKeyExchangeTests
{
    [Fact]
    public async Task PeerDiscovery_DirectPeer_SendsKeyExchangeInit()
    {
        using var harness = new KeyExchangeHarness();

        harness.Wifi.RaisePeerDiscovered(CreateDirectPeer("bob", "Bob"));

        await WaitUntilAsync(() => harness.Wifi.Sends.Any(send => send.Packet.Type == PacketType.KeyExchangeInit));
        var send = Assert.Single(harness.Wifi.Sends, send => send.Packet.Type == PacketType.KeyExchangeInit);
        var payload = Deserialize<KeyExchangeInitPayload>(send.Packet);

        Assert.Equal("bob", send.PeerId);
        Assert.Equal("alice", payload.SenderPeerId);
        Assert.Equal("bob", payload.TargetPeerId);
    }

    [Fact]
    public async Task InboundKeyExchangeInit_SendsResponse()
    {
        using var harness = new KeyExchangeHarness();
        var init = harness.BobManager.CreateOutboundKeyExchangeInit("alice");

        harness.Wifi.RaisePacketReceived(CreatePacket(PacketType.KeyExchangeInit, "bob", "Bob", init));

        await WaitUntilAsync(() => harness.Wifi.Sends.Any(send => send.Packet.Type == PacketType.KeyExchangeResponse));
        var send = Assert.Single(harness.Wifi.Sends, send => send.Packet.Type == PacketType.KeyExchangeResponse);
        var response = Deserialize<KeyExchangeResponsePayload>(send.Packet);

        Assert.Equal("bob", send.PeerId);
        Assert.Equal("alice", response.SenderPeerId);
        Assert.Equal("bob", response.TargetPeerId);
    }

    [Fact]
    public async Task InboundKeyExchangeResponse_SendsConfirm()
    {
        using var harness = new KeyExchangeHarness();
        harness.Wifi.RaisePeerDiscovered(CreateDirectPeer("bob", "Bob"));
        await WaitUntilAsync(() => harness.Wifi.Sends.Any(send => send.Packet.Type == PacketType.KeyExchangeInit));

        var initPacket = harness.Wifi.Sends.Single(send => send.Packet.Type == PacketType.KeyExchangeInit).Packet;
        harness.BobTrustStore.UpsertPeerIdentity(
            "alice",
            "Alice",
            harness.AliceIdentity.ExportPublicKey(),
            harness.AliceIdentity.Fingerprint);
        var response = harness.BobManager.ProcessInboundKeyExchangeInit(Deserialize<KeyExchangeInitPayload>(initPacket));

        harness.Wifi.RaisePacketReceived(CreatePacket(PacketType.KeyExchangeResponse, "bob", "Bob", response));

        await WaitUntilAsync(() => harness.Wifi.Sends.Any(send => send.Packet.Type == PacketType.KeyExchangeConfirm));
        var send = Assert.Single(harness.Wifi.Sends, send => send.Packet.Type == PacketType.KeyExchangeConfirm);
        var confirm = Deserialize<KeyExchangeConfirmPayload>(send.Packet);

        Assert.Equal("bob", send.PeerId);
        Assert.Equal("alice", confirm.SenderPeerId);
        Assert.Equal("bob", confirm.TargetPeerId);
    }

    [Fact]
    public async Task InboundKeyExchangeConfirm_EstablishesSession()
    {
        using var harness = new KeyExchangeHarness();
        var init = harness.BobManager.CreateOutboundKeyExchangeInit("alice");
        harness.Wifi.RaisePacketReceived(CreatePacket(PacketType.KeyExchangeInit, "bob", "Bob", init));
        await WaitUntilAsync(() => harness.Wifi.Sends.Any(send => send.Packet.Type == PacketType.KeyExchangeResponse));

        var responsePacket = harness.Wifi.Sends.Single(send => send.Packet.Type == PacketType.KeyExchangeResponse).Packet;
        harness.BobTrustStore.UpsertPeerIdentity(
            "alice",
            "Alice",
            harness.AliceIdentity.ExportPublicKey(),
            harness.AliceIdentity.Fingerprint);
        var confirm = harness.BobManager.ProcessInboundKeyExchangeResponse(Deserialize<KeyExchangeResponsePayload>(responsePacket));

        harness.Wifi.RaisePacketReceived(CreatePacket(PacketType.KeyExchangeConfirm, "bob", "Bob", confirm));

        await WaitUntilAsync(() => harness.AliceManager.GetActiveSession("bob")?.IsConfirmed == true);
        var session = harness.AliceManager.GetActiveSession("bob");

        Assert.NotNull(session);
        Assert.True(session.IsConfirmed);
    }

    [Fact]
    public async Task BlockedPeer_DoesNotStartOrRespondToKeyExchange()
    {
        using var harness = new KeyExchangeHarness();
        harness.AliceTrustStore.UpsertPeerIdentity(
            "bob",
            "Bob",
            harness.BobIdentity.ExportPublicKey(),
            harness.BobIdentity.Fingerprint);
        harness.AliceTrustStore.MarkBlocked("bob");

        await InvokeStartKeyExchangeIfNeededAsync(harness.ViewModel, CreateDirectPeer("bob", "Bob"));

        Assert.DoesNotContain(harness.Wifi.Sends, send => send.Packet.Type == PacketType.KeyExchangeInit);

        var init = new KeyExchangeInitPayload
        {
            SenderPeerId = "bob",
            TargetPeerId = "alice",
            IdentityPublicKey = harness.BobIdentity.ExportPublicKey()
        };
        await InvokeHandleKeyExchangeInitAsync(
            harness.ViewModel,
            CreatePacket(PacketType.KeyExchangeInit, "bob", "Bob", init));

        Assert.DoesNotContain(harness.Wifi.Sends, send => send.Packet.Type == PacketType.KeyExchangeResponse);
    }

    [Fact]
    public async Task InboundKeyExchangeInit_IdentityChangeForKnownPeer_IsRejectedBeforeTrustMutation()
    {
        using var harness = new KeyExchangeHarness();
        var verifiedPeer = CreateDirectPeer("bob", "Verified Bob") with
        {
            TrustState = TrustState.Verified,
            SecurityStatus = "Verified",
            FingerprintShort = "trusted"
        };
        harness.ViewModel.Peers.Add(verifiedPeer);
        AddPeerById(harness.ViewModel, verifiedPeer);
        harness.ViewModel.SelectedPeer = verifiedPeer;
        harness.AliceTrustStore.UpsertPeerIdentity(
            "bob",
            "Verified Bob",
            harness.BobIdentity.ExportPublicKey(),
            harness.BobIdentity.Fingerprint);
        harness.AliceTrustStore.MarkVerified("bob");

        using var attackerIdentity = LocalIdentity.Generate();
        using var attackerManager = new SessionKeyManager(
            "bob",
            attackerIdentity,
            new PeerTrustStore(Path.Combine(Path.GetTempPath(), "MeshChatKeyExchangeAttack", Guid.NewGuid().ToString("N"), "trust.json")));
        var init = attackerManager.CreateOutboundKeyExchangeInit("alice");

        await InvokeHandleKeyExchangeInitAsync(
            harness.ViewModel,
            CreatePacket(PacketType.KeyExchangeInit, "bob", "Evil Bob", init));

        Assert.DoesNotContain(harness.Wifi.Sends, send => send.Packet.Type == PacketType.KeyExchangeResponse);
        var stored = Assert.IsType<TrustedPeer>(harness.AliceTrustStore.GetByPeerId("bob"));
        Assert.Equal(TrustState.Verified, stored.TrustState);
        Assert.Equal("Verified Bob", stored.DisplayName);
        Assert.Equal(harness.BobIdentity.ExportPublicKey(), stored.IdentityPublicKey);
        Assert.Equal(harness.BobIdentity.Fingerprint, stored.Fingerprint);
        Assert.Null(harness.AliceManager.GetActiveSession("bob"));
        var visiblePeer = Assert.Single(harness.ViewModel.Peers, peer => peer.Id == "bob");
        Assert.Equal("Verified Bob", visiblePeer.DisplayName);
        Assert.Equal(TrustState.Verified, visiblePeer.TrustState);
        Assert.Equal("Verified", visiblePeer.SecurityStatus);
        Assert.Equal("trusted", visiblePeer.FingerprintShort);
    }

    [Fact]
    public async Task InboundKeyExchangeInit_InvalidSignatureForUnknownPeer_DoesNotCreateTrustRecord()
    {
        using var harness = new KeyExchangeHarness();
        var init = harness.BobManager.CreateOutboundKeyExchangeInit("alice");
        init.Signature[0] ^= 0x01;

        await InvokeHandleKeyExchangeInitAsync(
            harness.ViewModel,
            CreatePacket(PacketType.KeyExchangeInit, "bob", "Bob", init));

        Assert.DoesNotContain(harness.Wifi.Sends, send => send.Packet.Type == PacketType.KeyExchangeResponse);
        Assert.Null(harness.AliceTrustStore.GetByPeerId("bob"));
        Assert.Null(harness.AliceManager.GetActiveSession("bob"));
    }

    private static Peer CreateDirectPeer(string peerId, string displayName)
        => new()
        {
            Id = peerId,
            DisplayName = displayName,
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        };

    private static NetworkPacket CreatePacket(PacketType type, string senderId, string senderName, KeyExchangePayload payload)
        => new()
        {
            Type = type,
            SenderId = senderId,
            SenderName = senderName,
            TargetId = payload.TargetPeerId,
            Payload = JsonConvert.SerializeObject(payload)
        };

    private static TPayload Deserialize<TPayload>(NetworkPacket packet)
        where TPayload : KeyExchangePayload
        => JsonConvert.DeserializeObject<TPayload>(packet.Payload!)!;

    private static async Task InvokeStartKeyExchangeIfNeededAsync(MainViewModel vm, Peer peer)
    {
        var method = typeof(MainViewModel).GetMethod(
            "StartKeyExchangeIfNeededAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        await (Task)method.Invoke(vm, [peer, CancellationToken.None])!;
    }

    private static async Task InvokeHandleKeyExchangeInitAsync(MainViewModel vm, NetworkPacket packet)
    {
        var method = typeof(MainViewModel).GetMethod(
            "HandleKeyExchangeInitAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        await (Task)method.Invoke(vm, [packet, CancellationToken.None])!;
    }

    private static void AddPeerById(MainViewModel vm, Peer peer)
    {
        var field = typeof(MainViewModel).GetField(
            "_peerById",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var peers = (Dictionary<string, Peer>)field.GetValue(vm)!;
        peers[peer.Id] = peer;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(3))
        {
            if (condition())
                return;

            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private sealed class KeyExchangeHarness : IDisposable
    {
        public KeyExchangeHarness()
        {
            AliceIdentity = LocalIdentity.Generate();
            BobIdentity = LocalIdentity.Generate();
            AliceTrustStore = new PeerTrustStore(CreateTempFilePath());
            BobTrustStore = new PeerTrustStore(CreateTempFilePath());
            AliceManager = new SessionKeyManager("alice", AliceIdentity, AliceTrustStore);
            BobManager = new SessionKeyManager("bob", BobIdentity, BobTrustStore);
            Wifi = new FakeNetworkService { LocalId = "alice", LocalName = "Alice" };
            Bluetooth = new FakeNetworkService { LocalId = "alice", LocalName = "Alice" };
            ViewModel = new MainViewModel(
                Wifi,
                Bluetooth,
                new FileTransferService(),
                new MessageStore(),
                localIdentity: AliceIdentity,
                peerTrustStore: AliceTrustStore,
                sessionKeyManager: AliceManager);
            ViewModel.DisplayName = "Alice";
        }

        public LocalIdentity AliceIdentity { get; }
        public LocalIdentity BobIdentity { get; }
        public PeerTrustStore AliceTrustStore { get; }
        public PeerTrustStore BobTrustStore { get; }
        public SessionKeyManager AliceManager { get; }
        public SessionKeyManager BobManager { get; }
        public FakeNetworkService Wifi { get; }
        public FakeNetworkService Bluetooth { get; }
        public MainViewModel ViewModel { get; }

        public void Dispose()
        {
            AliceManager.Dispose();
            BobManager.Dispose();
            AliceIdentity.Dispose();
            BobIdentity.Dispose();
        }

        private static string CreateTempFilePath()
            => Path.Combine(
                Path.GetTempPath(),
                "MeshChatMainViewModelKeyExchangeTests",
                Guid.NewGuid().ToString("N"),
                "trusted-peers.json");
    }

    private sealed class FakeNetworkService : INetworkService
    {
        public List<(string PeerId, NetworkPacket Packet)> Sends { get; } = [];

        public string LocalId { get; set; } = string.Empty;
        public string LocalName { get; set; } = "Local";
        public int ListenPort => 0;
        public bool IsAvailable => true;
        public bool IsRunning => true;

        public event Action<Peer>? PeerDiscovered;
        public event Action<NetworkPacket>? PacketReceived;
        public event Action<string>? PeerLost { add { } remove { } }
        public event Action<string>? LogMessage { add { } remove { } }

        public void RaisePeerDiscovered(Peer peer) => PeerDiscovered?.Invoke(peer);

        public void RaisePacketReceived(NetworkPacket packet) => PacketReceived?.Invoke(packet);

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
