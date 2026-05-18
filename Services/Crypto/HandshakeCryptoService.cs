using System;
using System.IO;
using System.Security.Cryptography;

namespace MeshChat.Services.Crypto;

public sealed record HandshakeTranscript(
    string ProtocolVersion,
    string SenderPeerId,
    string ReceiverPeerId,
    byte[] SenderEphemeralPublicKey,
    byte[] Context);

public sealed record SignedHandshake(HandshakeTranscript Transcript, byte[] Signature);

public static class HandshakeCryptoService
{
    public static SignedHandshake Sign(LocalIdentity identity, HandshakeTranscript transcript)
    {
        var bytes = Serialize(transcript);
        return new SignedHandshake(transcript, identity.Sign(bytes));
    }

    public static bool Verify(ReadOnlySpan<byte> identityPublicKey, SignedHandshake handshake)
    {
        using var identity = ECDsa.Create();
        identity.ImportSubjectPublicKeyInfo(identityPublicKey, out _);

        var bytes = Serialize(handshake.Transcript);
        return identity.VerifyData(bytes, handshake.Signature, HashAlgorithmName.SHA256);
    }

    private static byte[] Serialize(HandshakeTranscript transcript)
    {
        using var stream = new MemoryStream();
        CryptoEncoding.WriteField(stream, transcript.ProtocolVersion);
        CryptoEncoding.WriteField(stream, transcript.SenderPeerId);
        CryptoEncoding.WriteField(stream, transcript.ReceiverPeerId);
        CryptoEncoding.WriteField(stream, transcript.SenderEphemeralPublicKey);
        CryptoEncoding.WriteField(stream, transcript.Context);
        return stream.ToArray();
    }
}
