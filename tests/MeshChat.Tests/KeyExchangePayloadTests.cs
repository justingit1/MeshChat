using MeshChat.Models;
using Newtonsoft.Json;

namespace MeshChat.Tests;

public sealed class KeyExchangePayloadTests
{
    [Fact]
    public void KeyExchangePayload_SerializesAndDeserializes()
    {
        var payload = CreateInitPayload();

        var json = JsonConvert.SerializeObject(payload);
        var deserialized = JsonConvert.DeserializeObject<KeyExchangeInitPayload>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(payload.ProtocolVersion, deserialized.ProtocolVersion);
        Assert.Equal(payload.SenderPeerId, deserialized.SenderPeerId);
        Assert.Equal(payload.TargetPeerId, deserialized.TargetPeerId);
        Assert.Equal(payload.IdentityPublicKey, deserialized.IdentityPublicKey);
        Assert.Equal(payload.IdentityKeyId, deserialized.IdentityKeyId);
        Assert.Equal(payload.EphemeralPublicKey, deserialized.EphemeralPublicKey);
        Assert.Equal(payload.HandshakeNonce, deserialized.HandshakeNonce);
        Assert.Equal(payload.SupportedCryptoVersions, deserialized.SupportedCryptoVersions);
        Assert.Equal(payload.Signature, deserialized.Signature);
    }

    [Fact]
    public void CanonicalTranscriptBytes_AreStable()
    {
        var payload = CreateInitPayload();

        var first = payload.GetTranscriptBytes();
        var second = payload.GetTranscriptBytes();

        Assert.Equal(first, second);
        Assert.Equal(
            "000000214d657368436861742e4b657945786368616e67655472616e7363726970742e76310000000f4b657945786368616e6765496e6974000000284d455348434841542d454344482d503235362d484b44462d5348413235362d41455347434d2d563100000005616c69636500000003626f620000000501020304050000000866703a616c69636500000005060708090a0000000410111213000000020000000741455347434d310000000741455347434d32",
            Convert.ToHexString(first).ToLowerInvariant());
    }

    [Fact]
    public void ChangingSignedFields_ChangesTranscriptBytes()
    {
        var original = CreateInitPayload();
        var originalBytes = original.GetTranscriptBytes();

        AssertTranscriptChanges(originalBytes, original, payload => payload.ProtocolVersion = "v2");
        AssertTranscriptChanges(originalBytes, original, payload => payload.SenderPeerId = "mallory");
        AssertTranscriptChanges(originalBytes, original, payload => payload.TargetPeerId = "carol");
        AssertTranscriptChanges(originalBytes, original, payload => payload.IdentityPublicKey = [9, 2, 3, 4, 5]);
        AssertTranscriptChanges(originalBytes, original, payload => payload.IdentityKeyId = "fp:mallory");
        AssertTranscriptChanges(originalBytes, original, payload => payload.EphemeralPublicKey = [6, 7, 8, 9, 11]);
        AssertTranscriptChanges(originalBytes, original, payload => payload.HandshakeNonce = [0x10, 0x11, 0x12, 0x14]);
        AssertTranscriptChanges(originalBytes, original, payload => payload.SupportedCryptoVersions = ["AESGCM1", "AESGCM3"]);

        var responseBytes = KeyExchangeTranscript.GetBytes(PacketType.KeyExchangeResponse, original);
        Assert.NotEqual(originalBytes, responseBytes);
    }

    [Fact]
    public void Signature_IsNotPartOfTranscriptBytes()
    {
        var original = CreateInitPayload();
        var changedSignature = Clone(original);
        changedSignature.Signature = [0xaa, 0xbb, 0xcc];

        Assert.Equal(original.GetTranscriptBytes(), changedSignature.GetTranscriptBytes());
    }

    [Fact]
    public void ExistingNetworkPacketSerialization_StillWorks()
    {
        var packet = new NetworkPacket
        {
            Id = "packet-1",
            Type = PacketType.Message,
            SenderId = "alice",
            SenderName = "Alice",
            TargetId = "bob",
            Ttl = 4,
            VisitedNodes = ["alice"],
            CreatedAt = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc),
            Payload = """{"Content":"hello"}""",
            IsEncrypted = true,
            CryptoVersion = "AESGCM1",
            CryptoSessionId = "session-1",
            CryptoKeyId = "key-1",
            CryptoNonce = "nonce",
            CryptoTag = "tag",
            CryptoMessageCounter = 7,
            TcpPort = 45678,
            KnownPeers = [new PeerInfo { Id = "bob", Name = "Bob" }]
        };

        var json = JsonConvert.SerializeObject(packet);
        var deserialized = JsonConvert.DeserializeObject<NetworkPacket>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(packet.Id, deserialized.Id);
        Assert.Equal(PacketType.Message, deserialized.Type);
        Assert.Equal(packet.SenderId, deserialized.SenderId);
        Assert.Equal(packet.SenderName, deserialized.SenderName);
        Assert.Equal(packet.TargetId, deserialized.TargetId);
        Assert.Equal(packet.Ttl, deserialized.Ttl);
        Assert.Equal(packet.VisitedNodes, deserialized.VisitedNodes);
        Assert.Equal(packet.CreatedAt, deserialized.CreatedAt);
        Assert.Equal(packet.Payload, deserialized.Payload);
        Assert.Equal(packet.IsEncrypted, deserialized.IsEncrypted);
        Assert.Equal(packet.CryptoVersion, deserialized.CryptoVersion);
        Assert.Equal(packet.CryptoSessionId, deserialized.CryptoSessionId);
        Assert.Equal(packet.CryptoKeyId, deserialized.CryptoKeyId);
        Assert.Equal(packet.CryptoNonce, deserialized.CryptoNonce);
        Assert.Equal(packet.CryptoTag, deserialized.CryptoTag);
        Assert.Equal(packet.CryptoMessageCounter, deserialized.CryptoMessageCounter);
        Assert.Equal(packet.TcpPort, deserialized.TcpPort);
        Assert.NotNull(deserialized.KnownPeers);
        Assert.Single(deserialized.KnownPeers);
        Assert.Equal("bob", deserialized.KnownPeers[0].Id);
    }

    private static void AssertTranscriptChanges(
        byte[] originalBytes,
        KeyExchangeInitPayload original,
        Action<KeyExchangeInitPayload> mutate)
    {
        var changed = Clone(original);
        mutate(changed);

        Assert.NotEqual(originalBytes, changed.GetTranscriptBytes());
    }

    private static KeyExchangeInitPayload Clone(KeyExchangeInitPayload payload)
        => new()
        {
            ProtocolVersion = payload.ProtocolVersion,
            SenderPeerId = payload.SenderPeerId,
            TargetPeerId = payload.TargetPeerId,
            IdentityPublicKey = (byte[])payload.IdentityPublicKey.Clone(),
            IdentityKeyId = payload.IdentityKeyId,
            EphemeralPublicKey = (byte[])payload.EphemeralPublicKey.Clone(),
            HandshakeNonce = (byte[])payload.HandshakeNonce.Clone(),
            SupportedCryptoVersions = (string[])payload.SupportedCryptoVersions.Clone(),
            Signature = (byte[])payload.Signature.Clone()
        };

    private static KeyExchangeInitPayload CreateInitPayload()
        => new()
        {
            ProtocolVersion = "MESHCHAT-ECDH-P256-HKDF-SHA256-AESGCM-V1",
            SenderPeerId = "alice",
            TargetPeerId = "bob",
            IdentityPublicKey = [1, 2, 3, 4, 5],
            IdentityKeyId = "fp:alice",
            EphemeralPublicKey = [6, 7, 8, 9, 10],
            HandshakeNonce = [0x10, 0x11, 0x12, 0x13],
            SupportedCryptoVersions = ["AESGCM1", "AESGCM2"],
            Signature = [0x20, 0x21, 0x22]
        };
}
