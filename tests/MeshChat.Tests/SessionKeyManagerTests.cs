using System.Security.Cryptography;
using MeshChat.Models;
using MeshChat.Services;
using MeshChat.Services.Crypto;

namespace MeshChat.Tests;

public sealed class SessionKeyManagerTests
{
    [Fact]
    public void TwoPeers_CompleteHandshakeAndDeriveMatchingDirectionalKeys()
    {
        using var fixture = SessionFixture.Create();

        var init = fixture.Alice.Manager.CreateOutboundKeyExchangeInit(fixture.Bob.PeerId);
        var response = fixture.Bob.Manager.ProcessInboundKeyExchangeInit(init);
        var confirm = fixture.Alice.Manager.ProcessInboundKeyExchangeResponse(response);
        fixture.Bob.Manager.ProcessInboundKeyExchangeConfirm(confirm);

        var aliceSession = fixture.Alice.Manager.GetActiveSession(fixture.Bob.PeerId);
        var bobSession = fixture.Bob.Manager.GetActiveSession(fixture.Alice.PeerId);

        Assert.NotNull(aliceSession);
        Assert.NotNull(bobSession);
        Assert.Equal(aliceSession.SendKey, bobSession.ReceiveKey);
        Assert.Equal(aliceSession.ReceiveKey, bobSession.SendKey);
        Assert.NotEqual(aliceSession.SendKey, aliceSession.ReceiveKey);
        Assert.True(aliceSession.IsConfirmed);
        Assert.True(bobSession.IsConfirmed);
    }

    [Fact]
    public void ProcessInboundKeyExchangeInit_TamperedSignatureFails()
    {
        using var fixture = SessionFixture.Create();
        var init = fixture.Alice.Manager.CreateOutboundKeyExchangeInit(fixture.Bob.PeerId);
        init.Signature[0] ^= 0x01;

        Assert.Throws<CryptographicException>(() =>
            fixture.Bob.Manager.ProcessInboundKeyExchangeInit(init));
    }

    [Fact]
    public void ProcessInboundKeyExchangeResponse_TamperedSignatureFails()
    {
        using var fixture = SessionFixture.Create();
        var init = fixture.Alice.Manager.CreateOutboundKeyExchangeInit(fixture.Bob.PeerId);
        var response = fixture.Bob.Manager.ProcessInboundKeyExchangeInit(init);
        response.Signature[0] ^= 0x01;

        Assert.Throws<CryptographicException>(() =>
            fixture.Alice.Manager.ProcessInboundKeyExchangeResponse(response));
    }

    [Fact]
    public void GetActiveSession_ReturnsEstablishedSessionByPeerId()
    {
        using var fixture = SessionFixture.Create();

        var init = fixture.Alice.Manager.CreateOutboundKeyExchangeInit(fixture.Bob.PeerId);
        var response = fixture.Bob.Manager.ProcessInboundKeyExchangeInit(init);
        fixture.Alice.Manager.ProcessInboundKeyExchangeResponse(response);

        var session = fixture.Alice.Manager.GetActiveSession(fixture.Bob.PeerId);

        Assert.NotNull(session);
        Assert.Equal(fixture.Bob.PeerId, session.PeerId);
        Assert.Null(fixture.Alice.Manager.GetActiveSession("unknown-peer"));
    }

    [Fact]
    public void ExpireOldSessions_RemovesExpiredSession()
    {
        var now = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);
        using var fixture = SessionFixture.Create(() => now, TimeSpan.FromMinutes(1));
        var init = fixture.Alice.Manager.CreateOutboundKeyExchangeInit(fixture.Bob.PeerId);
        var response = fixture.Bob.Manager.ProcessInboundKeyExchangeInit(init);
        fixture.Alice.Manager.ProcessInboundKeyExchangeResponse(response);

        now = now.AddMinutes(2);

        Assert.Equal(1, fixture.Alice.Manager.ExpireOldSessions());
        Assert.Equal(1, fixture.Bob.Manager.ExpireOldSessions());
        Assert.Null(fixture.Alice.Manager.GetActiveSession(fixture.Bob.PeerId));
        Assert.Null(fixture.Bob.Manager.GetActiveSession(fixture.Alice.PeerId));
    }

    [Fact]
    public void ProcessInboundKeyExchangeConfirm_MarksResponderSessionConfirmed()
    {
        using var fixture = SessionFixture.Create();
        var init = fixture.Alice.Manager.CreateOutboundKeyExchangeInit(fixture.Bob.PeerId);
        var response = fixture.Bob.Manager.ProcessInboundKeyExchangeInit(init);

        var beforeConfirm = fixture.Bob.Manager.GetActiveSession(fixture.Alice.PeerId);
        var confirm = fixture.Alice.Manager.ProcessInboundKeyExchangeResponse(response);
        fixture.Bob.Manager.ProcessInboundKeyExchangeConfirm(confirm);
        var afterConfirm = fixture.Bob.Manager.GetActiveSession(fixture.Alice.PeerId);

        Assert.NotNull(beforeConfirm);
        Assert.False(beforeConfirm.IsConfirmed);
        Assert.NotNull(afterConfirm);
        Assert.True(afterConfirm.IsConfirmed);
    }

    [Fact]
    public void SessionKeyManager_DoesNotPersistSessionKeys()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "MeshChatSessionKeyManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            using var fixture = SessionFixture.Create(
                trustStorePathFactory: peerId => Path.Combine(tempDirectory, $"{peerId}-trust.json"));

            var init = fixture.Alice.Manager.CreateOutboundKeyExchangeInit(fixture.Bob.PeerId);
            var response = fixture.Bob.Manager.ProcessInboundKeyExchangeInit(init);
            var confirm = fixture.Alice.Manager.ProcessInboundKeyExchangeResponse(response);
            fixture.Bob.Manager.ProcessInboundKeyExchangeConfirm(confirm);

            Assert.Empty(Directory.GetFiles(tempDirectory, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private sealed class SessionFixture : IDisposable
    {
        private SessionFixture(PeerHarness alice, PeerHarness bob)
        {
            Alice = alice;
            Bob = bob;
        }

        public PeerHarness Alice { get; }

        public PeerHarness Bob { get; }

        public static SessionFixture Create(
            Func<DateTime>? utcNow = null,
            TimeSpan? sessionLifetime = null,
            Func<string, string>? trustStorePathFactory = null)
        {
            var aliceIdentity = LocalIdentity.Generate();
            var bobIdentity = LocalIdentity.Generate();
            var aliceTrustStore = CreateTrustStore("alice", trustStorePathFactory);
            var bobTrustStore = CreateTrustStore("bob", trustStorePathFactory);

            aliceTrustStore.UpsertPeerIdentity(
                "bob",
                "Bob",
                bobIdentity.ExportPublicKey(),
                bobIdentity.Fingerprint);
            bobTrustStore.UpsertPeerIdentity(
                "alice",
                "Alice",
                aliceIdentity.ExportPublicKey(),
                aliceIdentity.Fingerprint);

            var alice = new PeerHarness(
                "alice",
                aliceIdentity,
                new SessionKeyManager("alice", aliceIdentity, aliceTrustStore, sessionLifetime, utcNow));
            var bob = new PeerHarness(
                "bob",
                bobIdentity,
                new SessionKeyManager("bob", bobIdentity, bobTrustStore, sessionLifetime, utcNow));

            return new SessionFixture(alice, bob);
        }

        public void Dispose()
        {
            Alice.Dispose();
            Bob.Dispose();
        }

        private static PeerTrustStore CreateTrustStore(
            string peerId,
            Func<string, string>? trustStorePathFactory)
        {
            var path = trustStorePathFactory?.Invoke(peerId)
                ?? Path.Combine(Path.GetTempPath(), "MeshChatSessionKeyManagerTests", Guid.NewGuid().ToString("N"), $"{peerId}.json");
            return new PeerTrustStore(path);
        }
    }

    private sealed class PeerHarness : IDisposable
    {
        public PeerHarness(string peerId, LocalIdentity identity, SessionKeyManager manager)
        {
            PeerId = peerId;
            Identity = identity;
            Manager = manager;
        }

        public string PeerId { get; }

        public LocalIdentity Identity { get; }

        public SessionKeyManager Manager { get; }

        public void Dispose()
        {
            Manager.Dispose();
            Identity.Dispose();
        }
    }
}
