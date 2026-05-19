using System.Reflection;
using System.Runtime.Serialization;
using MeshChat.Models;
using MeshChat.Services;
using MeshChat.ViewModels;

namespace MeshChat.Tests;

public sealed class NetworkPacketBehaviorTests
{
    [Fact]
    public void TryDecrypt_InvalidAesGcm1Payload_ReturnsFalse()
    {
#pragma warning disable SYSLIB0050
        var viewModel = (MainViewModel)FormatterServices.GetUninitializedObject(typeof(MainViewModel));
#pragma warning restore SYSLIB0050
        var method = typeof(MainViewModel).GetMethod(
            "TryDecrypt",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        object?[] args = ["AESGCM1:not-valid-base64", "AESGCM1", null];
        var result = (bool)method.Invoke(viewModel, args)!;

        Assert.False(result);
        Assert.NotEqual("""{"Content":"plaintext"}""", args[2]);
    }

    [Fact]
    public void CreateRelayPacket_PreservesMetadataAndDoesNotMutateOriginal()
    {
        var service = new WiFiService { LocalId = "relay-node" };
        var method = typeof(WiFiService).GetMethod(
            "CreateRelayPacket",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var createdAt = DateTime.UtcNow.AddMinutes(-5);
        var knownPeers = new[] { new PeerInfo { Id = "peer-a", Name = "Peer A" } };
        var originalVisited = new[] { "origin-node" };
        var original = new NetworkPacket
        {
            Id = "packet-id",
            Type = PacketType.Message,
            SenderId = "sender",
            SenderName = "Sender",
            TargetId = "target",
            Ttl = 4,
            VisitedNodes = originalVisited,
            CreatedAt = createdAt,
            Payload = "AESGCM1:payload",
            IsEncrypted = true,
            CryptoVersion = "AESGCM1",
            CryptoSessionId = "session-1",
            CryptoKeyId = "key-1",
            CryptoNonce = "nonce",
            CryptoTag = "tag",
            CryptoMessageCounter = 7,
            TcpPort = 45678,
            KnownPeers = knownPeers
        };

        var clone = (NetworkPacket)method.Invoke(service, [original])!;

        Assert.Equal(original.Id, clone.Id);
        Assert.Equal(original.Type, clone.Type);
        Assert.Equal(original.SenderId, clone.SenderId);
        Assert.Equal(original.SenderName, clone.SenderName);
        Assert.Equal(original.TargetId, clone.TargetId);
        Assert.Equal(3, clone.Ttl);
        Assert.Equal(["origin-node", "relay-node"], clone.VisitedNodes);
        Assert.Equal(createdAt, clone.CreatedAt);
        Assert.Equal(original.Payload, clone.Payload);
        Assert.True(clone.IsEncrypted);
        Assert.Equal("AESGCM1", clone.CryptoVersion);
        Assert.Equal("session-1", clone.CryptoSessionId);
        Assert.Equal("key-1", clone.CryptoKeyId);
        Assert.Equal("nonce", clone.CryptoNonce);
        Assert.Equal("tag", clone.CryptoTag);
        Assert.Equal((ulong)7, clone.CryptoMessageCounter);
        Assert.Equal(original.TcpPort, clone.TcpPort);
        Assert.Same(knownPeers, clone.KnownPeers);

        Assert.Equal(4, original.Ttl);
        Assert.Equal(["origin-node"], original.VisitedNodes);
        Assert.Same(originalVisited, original.VisitedNodes);
    }
}
