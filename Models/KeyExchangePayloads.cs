using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MeshChat.Models;

public abstract class KeyExchangePayload
{
    public string ProtocolVersion { get; set; } = string.Empty;
    public string SenderPeerId { get; set; } = string.Empty;
    public string TargetPeerId { get; set; } = string.Empty;
    public byte[] IdentityPublicKey { get; set; } = [];
    public string IdentityKeyId { get; set; } = string.Empty;
    public byte[] EphemeralPublicKey { get; set; } = [];
    public byte[] HandshakeNonce { get; set; } = [];
    public string[] SupportedCryptoVersions { get; set; } = [];
    public byte[] Signature { get; set; } = [];

    protected byte[] GetTranscriptBytes(PacketType packetType)
        => KeyExchangeTranscript.GetBytes(packetType, this);
}

public sealed class KeyExchangeInitPayload : KeyExchangePayload
{
    public byte[] GetTranscriptBytes()
        => GetTranscriptBytes(PacketType.KeyExchangeInit);
}

public sealed class KeyExchangeResponsePayload : KeyExchangePayload
{
    public byte[] GetTranscriptBytes()
        => GetTranscriptBytes(PacketType.KeyExchangeResponse);
}

public sealed class KeyExchangeConfirmPayload : KeyExchangePayload
{
    public byte[] GetTranscriptBytes()
        => GetTranscriptBytes(PacketType.KeyExchangeConfirm);
}

public static class KeyExchangeTranscript
{
    private const string Domain = "MeshChat.KeyExchangeTranscript.v1";

    public static byte[] GetBytes(PacketType packetType, KeyExchangePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var stream = new MemoryStream();
        WriteField(stream, Domain);
        WriteField(stream, packetType.ToString());
        WriteField(stream, payload.ProtocolVersion);
        WriteField(stream, payload.SenderPeerId);
        WriteField(stream, payload.TargetPeerId);
        WriteField(stream, payload.IdentityPublicKey);
        WriteField(stream, payload.IdentityKeyId);
        WriteField(stream, payload.EphemeralPublicKey);
        WriteField(stream, payload.HandshakeNonce);
        WriteFields(stream, payload.SupportedCryptoVersions);
        return stream.ToArray();
    }

    private static void WriteFields(Stream stream, string[] values)
    {
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(count, values.Length);
        stream.Write(count);

        foreach (var value in values)
        {
            WriteField(stream, value);
        }
    }

    private static void WriteField(Stream stream, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static void WriteField(Stream stream, string value)
    {
        WriteField(stream, Encoding.UTF8.GetBytes(value));
    }
}
