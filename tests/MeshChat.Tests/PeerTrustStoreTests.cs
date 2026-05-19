using MeshChat.Models;
using MeshChat.Services;

namespace MeshChat.Tests;

public sealed class PeerTrustStoreTests
{
    [Fact]
    public void MissingFile_LoadsEmptyStore()
    {
        var filePath = CreateTempFilePath();
        var store = new PeerTrustStore(filePath);

        var peers = store.Load();

        Assert.Empty(peers);
        Assert.Empty(store.Peers);
    }

    [Fact]
    public void UpsertPeerIdentity_AddsUnknownPeer()
    {
        var store = new PeerTrustStore(CreateTempFilePath());
        var firstSeen = new DateTime(2026, 05, 18, 12, 0, 0, DateTimeKind.Utc);

        var peer = store.UpsertPeerIdentity("peer-1", "Alice", [1, 2, 3], "ABCDEF", firstSeen);

        Assert.Equal("peer-1", peer.PeerId);
        Assert.Equal("Alice", peer.DisplayName);
        Assert.Equal([1, 2, 3], peer.IdentityPublicKey);
        Assert.Equal("abcdef", peer.Fingerprint);
        Assert.Equal(TrustState.Unknown, peer.TrustState);
        Assert.Equal(firstSeen, peer.FirstSeen);
        Assert.Equal(firstSeen, peer.LastSeen);
        Assert.Null(peer.VerifiedAt);
    }

    [Fact]
    public void UpsertPeerIdentity_UpdatesExistingPeerAndPreservesFirstSeen()
    {
        var store = new PeerTrustStore(CreateTempFilePath());
        var firstSeen = new DateTime(2026, 05, 18, 12, 0, 0, DateTimeKind.Utc);
        var lastSeen = firstSeen.AddMinutes(5);
        store.UpsertPeerIdentity("peer-1", "Alice", [1, 2, 3], "abcdef", firstSeen);

        var peer = store.UpsertPeerIdentity("peer-1", "Alice Cooper", [1, 2, 3], "abcdef", lastSeen);

        Assert.Equal("Alice Cooper", peer.DisplayName);
        Assert.Equal(firstSeen, peer.FirstSeen);
        Assert.Equal(lastSeen, peer.LastSeen);
    }

    [Fact]
    public void MarkVerified_SetsVerifiedStateAndTimestamp()
    {
        var store = new PeerTrustStore(CreateTempFilePath());
        store.UpsertPeerIdentity("peer-1", "Alice", [1, 2, 3], "abcdef");
        var verifiedAt = new DateTime(2026, 05, 18, 13, 0, 0, DateTimeKind.Utc);

        var peer = store.MarkVerified("peer-1", verifiedAt);

        Assert.NotNull(peer);
        Assert.Equal(TrustState.Verified, peer.TrustState);
        Assert.Equal(verifiedAt, peer.VerifiedAt);
    }

    [Fact]
    public void UpsertPeerIdentity_ChangedVerifiedIdentityResetsTrust()
    {
        var store = new PeerTrustStore(CreateTempFilePath());
        store.UpsertPeerIdentity("peer-1", "Alice", [1, 2, 3], "abcdef");
        store.MarkVerified("peer-1", new DateTime(2026, 05, 18, 13, 0, 0, DateTimeKind.Utc));

        var peer = store.UpsertPeerIdentity("peer-1", "Alice", [4, 5, 6], "123456");

        Assert.Equal(TrustState.Unknown, peer.TrustState);
        Assert.Null(peer.VerifiedAt);
        Assert.Equal("123456", peer.Fingerprint);
        Assert.Equal([4, 5, 6], peer.IdentityPublicKey);
    }

    [Fact]
    public void MarkBlocked_SetsBlockedStateAndClearsVerifiedAt()
    {
        var store = new PeerTrustStore(CreateTempFilePath());
        store.UpsertPeerIdentity("peer-1", "Alice", [1, 2, 3], "abcdef");
        store.MarkVerified("peer-1", new DateTime(2026, 05, 18, 13, 0, 0, DateTimeKind.Utc));

        var peer = store.MarkBlocked("peer-1");

        Assert.NotNull(peer);
        Assert.Equal(TrustState.Blocked, peer.TrustState);
        Assert.Null(peer.VerifiedAt);
    }

    [Fact]
    public void Unblock_SetsUnknownState()
    {
        var store = new PeerTrustStore(CreateTempFilePath());
        store.UpsertPeerIdentity("peer-1", "Alice", [1, 2, 3], "abcdef");
        store.MarkBlocked("peer-1");

        var peer = store.Unblock("peer-1");

        Assert.NotNull(peer);
        Assert.Equal(TrustState.Unknown, peer.TrustState);
        Assert.Null(peer.VerifiedAt);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsTrustedPeers()
    {
        var filePath = CreateTempFilePath();
        var store = new PeerTrustStore(filePath);
        var firstSeen = new DateTime(2026, 05, 18, 12, 0, 0, DateTimeKind.Utc);
        var verifiedAt = firstSeen.AddMinutes(1);
        store.UpsertPeerIdentity("peer-1", "Alice", [1, 2, 3], "abcdef", firstSeen);
        store.MarkVerified("peer-1", verifiedAt);
        store.Save();

        var reloaded = new PeerTrustStore(filePath);
        reloaded.Load();
        var peer = reloaded.GetByPeerId("peer-1");

        Assert.NotNull(peer);
        Assert.Equal("Alice", peer.DisplayName);
        Assert.Equal([1, 2, 3], peer.IdentityPublicKey);
        Assert.Equal("abcdef", peer.Fingerprint);
        Assert.Equal(TrustState.Verified, peer.TrustState);
        Assert.Equal(firstSeen, peer.FirstSeen);
        Assert.Equal(verifiedAt, peer.VerifiedAt);
    }

    [Fact]
    public void GetByFingerprint_FindsPeerCaseInsensitively()
    {
        var store = new PeerTrustStore(CreateTempFilePath());
        store.UpsertPeerIdentity("peer-1", "Alice", [1, 2, 3], "abcdef");

        var peer = store.GetByFingerprint("ABCDEF");

        Assert.NotNull(peer);
        Assert.Equal("peer-1", peer.PeerId);
    }

    private static string CreateTempFilePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeshChat.Tests", Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "trusted-peers.json");
    }
}
