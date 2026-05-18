using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MeshChat.Services.Crypto;

internal static class CryptoEncoding
{
    public static string ToHex(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static void WriteField(Stream stream, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    public static void WriteField(Stream stream, string value)
    {
        WriteField(stream, Encoding.UTF8.GetBytes(value));
    }
}
