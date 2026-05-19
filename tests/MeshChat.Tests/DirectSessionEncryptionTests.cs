using System.Collections.Generic;
using System.Reflection;
using MeshChat.Models;
using MeshChat.Services;
using MeshChat.Services.Crypto;
using MeshChat.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace MeshChat.Tests;

public sealed class DirectSessionEncryptionTests
{
    [Fact]
    public async Task DirectTextMessage_WithConfirmedSession_UsesEcdhAesGcm2()
    {
        using var harness = DirectSessionHarness.Create(establishSession: true);

        await harness.AliceVm.SendMessageAsync();

        var send = Assert.Single(harness.AliceWifi.Sends, send => send.Packet.Type == PacketType.Message);
        var packet = send.Packet;
        Assert.True(packet.IsEncrypted);
        Assert.Equal(SessionKeyManager.CryptoVersion, packet.CryptoVersion);
        Assert.Equal(harness.AliceManager.GetActiveSession("bob")!.SessionId, packet.CryptoSessionId);
        Assert.Equal((ulong)1, packet.CryptoMessageCounter);
        Assert.False(string.IsNullOrWhiteSpace(packet.CryptoNonce));
        Assert.False(string.IsNullOrWhiteSpace(packet.CryptoTag));

        var plainText = DecryptWithSessionReceiveKey(packet, harness.BobManager.GetActiveSession("alice")!);
        var message = JsonConvert.DeserializeObject<ChatMessage>(plainText);
        Assert.NotNull(message);
        Assert.Equal("hello", message.Content);
    }

    [Fact]
    public async Task IncomingEcdhAesGcm2Message_WithConfirmedSession_DecryptsWithReceiveKey()
    {
        using var harness = DirectSessionHarness.Create(establishSession: true);

        await harness.AliceVm.SendMessageAsync();
        var packet = Assert.Single(harness.AliceWifi.Sends, send => send.Packet.Type == PacketType.Message).Packet;
        await InvokeIncomingMessageAsync(harness.BobVm, packet);

        var message = Assert.Single(harness.BobVm.Messages);
        Assert.Equal("hello", message.Content);
        Assert.Equal(MessageStatus.Delivered, message.Status);
    }

    [Fact]
    public async Task IncomingEcdhAesGcm2Message_FirstCounterAccepted()
    {
        using var harness = DirectSessionHarness.Create(establishSession: true);

        var packet = await SendAliceMessageAsync(harness, "first");

        Assert.Equal((ulong)1, packet.CryptoMessageCounter);
        await InvokeIncomingMessageAsync(harness.BobVm, packet);

        var message = Assert.Single(harness.BobVm.Messages);
        Assert.Equal("first", message.Content);
    }

    [Fact]
    public async Task IncomingEcdhAesGcm2Message_LargerCounterAccepted()
    {
        using var harness = DirectSessionHarness.Create(establishSession: true);

        var first = await SendAliceMessageAsync(harness, "first");
        var second = await SendAliceMessageAsync(harness, "second");

        await InvokeIncomingMessageAsync(harness.BobVm, first);
        await InvokeIncomingMessageAsync(harness.BobVm, second);

        Assert.Equal((ulong)1, first.CryptoMessageCounter);
        Assert.Equal((ulong)2, second.CryptoMessageCounter);
        Assert.Equal(["first", "second"], harness.BobVm.Messages.Select(message => message.Content).ToArray());
    }

    [Fact]
    public async Task IncomingEcdhAesGcm2Message_RepeatedCounterRejected()
    {
        using var harness = DirectSessionHarness.Create(establishSession: true);

        var packet = await SendAliceMessageAsync(harness, "first");

        await InvokeIncomingMessageAsync(harness.BobVm, packet);
        await InvokeIncomingMessageAsync(harness.BobVm, packet);

        var message = Assert.Single(harness.BobVm.Messages);
        Assert.Equal("first", message.Content);
    }

    [Fact]
    public async Task IncomingEcdhAesGcm2Message_LowerCounterRejected()
    {
        using var harness = DirectSessionHarness.Create(establishSession: true);

        var lower = await SendAliceMessageAsync(harness, "first");
        var higher = await SendAliceMessageAsync(harness, "second");

        await InvokeIncomingMessageAsync(harness.BobVm, higher);
        await InvokeIncomingMessageAsync(harness.BobVm, lower);

        var message = Assert.Single(harness.BobVm.Messages);
        Assert.Equal("second", message.Content);
    }

    [Fact]
    public async Task IncomingEcdhAesGcm2Message_TamperedCounterFailsAuthentication()
    {
        using var harness = DirectSessionHarness.Create(establishSession: true);

        var packet = await SendAliceMessageAsync(harness, "first");
        packet.CryptoMessageCounter++;

        await InvokeIncomingMessageAsync(harness.BobVm, packet);

        Assert.Empty(harness.BobVm.Messages);
        Assert.Empty(harness.BobWifi.Sends);
    }

    [Fact]
    public async Task IncomingEcdhAesGcm2Message_NewSessionResetsReceiveCounter()
    {
        using var harness = DirectSessionHarness.Create(establishSession: true);

        var firstSessionPacket = await SendAliceMessageAsync(harness, "first");
        await InvokeIncomingMessageAsync(harness.BobVm, firstSessionPacket);

        harness.EstablishNewSession();
        var secondSessionPacket = await SendAliceMessageAsync(harness, "second");
        await InvokeIncomingMessageAsync(harness.BobVm, secondSessionPacket);

        Assert.Equal((ulong)1, firstSessionPacket.CryptoMessageCounter);
        Assert.Equal((ulong)1, secondSessionPacket.CryptoMessageCounter);
        Assert.NotEqual(firstSessionPacket.CryptoSessionId, secondSessionPacket.CryptoSessionId);
        Assert.Equal(["first", "second"], harness.BobVm.Messages.Select(message => message.Content).ToArray());
    }

    [Fact]
    public async Task IncomingEcdhAesGcm2Message_MissingSession_DropsSafely()
    {
        using var sender = DirectSessionHarness.Create(establishSession: true);
        using var receiverWithoutSession = DirectSessionHarness.Create(establishSession: false);

        await sender.AliceVm.SendMessageAsync();
        var packet = Assert.Single(sender.AliceWifi.Sends, send => send.Packet.Type == PacketType.Message).Packet;
        await InvokeIncomingMessageAsync(receiverWithoutSession.BobVm, packet);

        Assert.Empty(receiverWithoutSession.BobVm.Messages);
        Assert.Empty(receiverWithoutSession.BobWifi.Sends);
    }

    [Fact]
    public async Task IncomingEcdhAesGcm2Message_TamperedTag_DropsSafely()
    {
        using var harness = DirectSessionHarness.Create(establishSession: true);

        await harness.AliceVm.SendMessageAsync();
        var packet = Assert.Single(harness.AliceWifi.Sends, send => send.Packet.Type == PacketType.Message).Packet;
        var tag = Convert.FromBase64String(packet.CryptoTag!);
        tag[0] ^= 0x01;
        packet.CryptoTag = Convert.ToBase64String(tag);

        await InvokeIncomingMessageAsync(harness.BobVm, packet);

        Assert.Empty(harness.BobVm.Messages);
        Assert.Empty(harness.BobWifi.Sends);
    }

    [Fact]
    public async Task IncomingEncryptedMessage_UnknownCryptoVersion_DropsSafely()
    {
        using var harness = DirectSessionHarness.Create(establishSession: false);
        var packet = new NetworkPacket
        {
            Type = PacketType.Message,
            SenderId = "alice",
            SenderName = "Alice",
            TargetId = "bob",
            Payload = "ciphertext",
            IsEncrypted = true,
            CryptoVersion = "UNKNOWN"
        };

        await InvokeIncomingMessageAsync(harness.BobVm, packet);

        Assert.Empty(harness.BobVm.Messages);
        Assert.Empty(harness.BobWifi.Sends);
    }

    [Fact]
    public async Task DirectTextMessage_VerifiedPeerWithoutEncryption_DoesNotSendPlaintext()
    {
        using var harness = DirectSessionHarness.Create(establishSession: false);
        harness.AliceTrustStore.MarkVerified("bob");

        await harness.AliceVm.SendMessageAsync();

        Assert.DoesNotContain(harness.AliceWifi.Sends, send => send.Packet.Type == PacketType.Message);
        var message = Assert.Single(harness.AliceVm.Messages);
        Assert.Equal(MessageStatus.Failed, message.Status);
        Assert.NotNull(message.QueuedAt);
        Assert.Contains("Encrypted session unavailable", harness.AliceVm.ToastMessage);
    }

    [Fact]
    public async Task DirectTextMessage_LegacyAesGcm1_StillWorksWhenEncryptionEnabledAndNoSession()
    {
        using var harness = DirectSessionHarness.Create(establishSession: false);
        harness.AliceVm.EncryptionEnabled = true;

        await harness.AliceVm.SendMessageAsync();
        var packet = Assert.Single(harness.AliceWifi.Sends, send => send.Packet.Type == PacketType.Message).Packet;

        Assert.True(packet.IsEncrypted);
        Assert.Equal("AESGCM1", packet.CryptoVersion);
        Assert.Null(packet.CryptoSessionId);

        await InvokeIncomingMessageAsync(harness.BobVm, packet);
        var message = Assert.Single(harness.BobVm.Messages);
        Assert.Equal("hello", message.Content);
    }

    [Fact]
    public async Task DirectTextMessage_NoSessionAndEncryptionDisabled_SendsPlaintext()
    {
        using var harness = DirectSessionHarness.Create(establishSession: false);

        await harness.AliceVm.SendMessageAsync();
        var packet = Assert.Single(harness.AliceWifi.Sends, send => send.Packet.Type == PacketType.Message).Packet;

        Assert.False(packet.IsEncrypted);
        Assert.Null(packet.CryptoVersion);
        Assert.Null(packet.CryptoSessionId);

        var message = JsonConvert.DeserializeObject<ChatMessage>(packet.Payload!);
        Assert.NotNull(message);
        Assert.Equal("hello", message.Content);
    }

    [Fact]
    public void UnknownPeer_WithConfirmedSession_ShowsEncryptedUnverifiedStatus()
    {
        using var harness = DirectSessionHarness.Create(establishSession: true);

        InvokeRefreshPeerSecurityStatus(harness.AliceVm, "bob");

        Assert.Equal(TrustState.Unknown, harness.AliceVm.SelectedPeer!.TrustState);
        Assert.Equal("Encrypted · Unverified", harness.AliceVm.SelectedPeer.SecurityStatus);
        Assert.False(string.IsNullOrWhiteSpace(harness.AliceVm.SelectedPeer.FingerprintShort));
    }

    [Fact]
    public void VerifiedPeer_WithConfirmedSession_ShowsEncryptedVerifiedStatus()
    {
        using var harness = DirectSessionHarness.Create(establishSession: true);
        harness.AliceTrustStore.MarkVerified("bob");

        InvokeRefreshPeerSecurityStatus(harness.AliceVm, "bob");

        Assert.Equal(TrustState.Verified, harness.AliceVm.SelectedPeer!.TrustState);
        Assert.Equal("Encrypted · Verified", harness.AliceVm.SelectedPeer.SecurityStatus);
    }

    private static string DecryptWithSessionReceiveKey(NetworkPacket packet, PeerCryptoSession session)
    {
        var encrypted = new AesGcmEncryptedPayload(
            Convert.FromBase64String(packet.CryptoNonce!),
            Convert.FromBase64String(packet.Payload!),
            Convert.FromBase64String(packet.CryptoTag!));
        var plainBytes = SessionCryptoService.Decrypt(
            session.ReceiveKey,
            encrypted,
            CreateSessionMessageAssociatedData(packet));
        return System.Text.Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] CreateSessionMessageAssociatedData(NetworkPacket packet)
        => System.Text.Encoding.UTF8.GetBytes(string.Join('\n',
        [
            SessionKeyManager.CryptoVersion,
            PacketType.Message.ToString(),
            packet.CryptoSessionId!,
            packet.SenderId,
            packet.TargetId ?? string.Empty,
            packet.CryptoMessageCounter!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ]));

    private static async Task<NetworkPacket> SendAliceMessageAsync(DirectSessionHarness harness, string content)
    {
        harness.AliceVm.MessageInput = content;
        await harness.AliceVm.SendMessageAsync();
        return harness.AliceWifi.Sends
            .Where(send => send.Packet.Type == PacketType.Message)
            .Select(send => send.Packet)
            .Last();
    }

    private static async Task InvokeIncomingMessageAsync(MainViewModel vm, NetworkPacket packet)
    {
        var method = typeof(MainViewModel).GetMethod(
            "HandleIncomingMessageAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task)method.Invoke(vm, [packet, CancellationToken.None])!;
        await task;
    }

    private static void InvokeRefreshPeerSecurityStatus(MainViewModel vm, string peerId)
    {
        var method = typeof(MainViewModel).GetMethod(
            "RefreshPeerSecurityStatus",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(vm, [peerId]);
    }

    private sealed class DirectSessionHarness : IDisposable
    {
        private DirectSessionHarness(
            LocalIdentity aliceIdentity,
            LocalIdentity bobIdentity,
            PeerTrustStore aliceTrustStore,
            PeerTrustStore bobTrustStore,
            SessionKeyManager aliceManager,
            SessionKeyManager bobManager,
            FakeNetworkService aliceWifi,
            FakeNetworkService bobWifi,
            MainViewModel aliceVm,
            MainViewModel bobVm)
        {
            AliceIdentity = aliceIdentity;
            BobIdentity = bobIdentity;
            AliceTrustStore = aliceTrustStore;
            BobTrustStore = bobTrustStore;
            AliceManager = aliceManager;
            BobManager = bobManager;
            AliceWifi = aliceWifi;
            BobWifi = bobWifi;
            AliceVm = aliceVm;
            BobVm = bobVm;
        }

        public LocalIdentity AliceIdentity { get; }
        public LocalIdentity BobIdentity { get; }
        public PeerTrustStore AliceTrustStore { get; }
        public PeerTrustStore BobTrustStore { get; }
        public SessionKeyManager AliceManager { get; }
        public SessionKeyManager BobManager { get; }
        public FakeNetworkService AliceWifi { get; }
        public FakeNetworkService BobWifi { get; }
        public MainViewModel AliceVm { get; }
        public MainViewModel BobVm { get; }

        public static DirectSessionHarness Create(bool establishSession)
        {
            var aliceIdentity = LocalIdentity.Generate();
            var bobIdentity = LocalIdentity.Generate();
            var aliceTrustStore = CreateTrustStore("alice");
            var bobTrustStore = CreateTrustStore("bob");

            aliceTrustStore.UpsertPeerIdentity("bob", "Bob", bobIdentity.ExportPublicKey(), bobIdentity.Fingerprint);
            bobTrustStore.UpsertPeerIdentity("alice", "Alice", aliceIdentity.ExportPublicKey(), aliceIdentity.Fingerprint);

            var aliceManager = new SessionKeyManager("alice", aliceIdentity, aliceTrustStore);
            var bobManager = new SessionKeyManager("bob", bobIdentity, bobTrustStore);

            if (establishSession)
            {
                var init = aliceManager.CreateOutboundKeyExchangeInit("bob");
                var response = bobManager.ProcessInboundKeyExchangeInit(init);
                var confirm = aliceManager.ProcessInboundKeyExchangeResponse(response);
                bobManager.ProcessInboundKeyExchangeConfirm(confirm);
            }

            var aliceWifi = new FakeNetworkService { LocalId = "alice", LocalName = "Alice" };
            var aliceBluetooth = new FakeNetworkService { LocalId = "alice", LocalName = "Alice" };
            var bobWifi = new FakeNetworkService { LocalId = "bob", LocalName = "Bob" };
            var bobBluetooth = new FakeNetworkService { LocalId = "bob", LocalName = "Bob" };

            var aliceVm = CreateViewModel(
                aliceWifi,
                aliceBluetooth,
                aliceIdentity,
                aliceTrustStore,
                aliceManager,
                "Alice",
                CreateDirectPeer("bob", "Bob"),
                messageInput: "hello");
            var bobVm = CreateViewModel(
                bobWifi,
                bobBluetooth,
                bobIdentity,
                bobTrustStore,
                bobManager,
                "Bob",
                CreateDirectPeer("alice", "Alice"));

            return new DirectSessionHarness(
                aliceIdentity,
                bobIdentity,
                aliceTrustStore,
                bobTrustStore,
                aliceManager,
                bobManager,
                aliceWifi,
                bobWifi,
                aliceVm,
                bobVm);
        }

        public void Dispose()
        {
            AliceManager.Dispose();
            BobManager.Dispose();
            AliceIdentity.Dispose();
            BobIdentity.Dispose();
        }

        public void EstablishNewSession()
        {
            var init = AliceManager.CreateOutboundKeyExchangeInit("bob");
            var response = BobManager.ProcessInboundKeyExchangeInit(init);
            var confirm = AliceManager.ProcessInboundKeyExchangeResponse(response);
            BobManager.ProcessInboundKeyExchangeConfirm(confirm);
        }

        private static MainViewModel CreateViewModel(
            FakeNetworkService wifi,
            FakeNetworkService bluetooth,
            LocalIdentity identity,
            PeerTrustStore trustStore,
            SessionKeyManager manager,
            string displayName,
            Peer peer,
            string? messageInput = null)
        {
            var vm = new MainViewModel(
                wifi,
                bluetooth,
                new FileTransferService(),
                new MessageStore(),
                NullLogger<MainViewModel>.Instance,
                localIdentity: identity,
                peerTrustStore: trustStore,
                sessionKeyManager: manager);

            vm.DisplayName = displayName;
            vm.Peers.Add(peer);
            AddPeerById(vm, peer);
            vm.SelectedPeer = peer;
            if (messageInput != null)
                vm.MessageInput = messageInput;
            return vm;
        }

        private static Peer CreateDirectPeer(string id, string displayName)
            => new()
            {
                Id = id,
                DisplayName = displayName,
                Status = PeerStatus.Online,
                Transport = TransportType.WiFi,
                HopsAway = 1
            };

        private static void AddPeerById(MainViewModel vm, Peer peer)
        {
            var field = typeof(MainViewModel).GetField("_peerById", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var peers = (Dictionary<string, Peer>)field.GetValue(vm)!;
            peers[peer.Id] = peer;
        }

        private static PeerTrustStore CreateTrustStore(string peerId)
            => new(Path.Combine(
                Path.GetTempPath(),
                "MeshChatDirectSessionEncryptionTests",
                Guid.NewGuid().ToString("N"),
                $"{peerId}.json"));
    }

    private sealed class FakeNetworkService : INetworkService
    {
        public List<(string PeerId, NetworkPacket Packet)> Sends { get; } = [];

        public string LocalId { get; set; } = string.Empty;
        public string LocalName { get; set; } = string.Empty;
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
