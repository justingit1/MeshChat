using System;
using System.Security.Cryptography;

namespace MeshChat.Services.Crypto;

public sealed class LocalIdentity : IDisposable
{
    private readonly ECDsa _signingKey;
    private readonly byte[] _publicKey;

    private LocalIdentity(ECDsa signingKey)
    {
        _signingKey = signingKey;
        _publicKey = _signingKey.ExportSubjectPublicKeyInfo();
        Fingerprint = CryptoEncoding.ToHex(SHA256.HashData(_publicKey));
    }

    public byte[] PublicKey => ExportPublicKey();

    public string Fingerprint { get; }

    public static LocalIdentity Generate()
        => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static LocalIdentity FromPkcs8PrivateKey(ReadOnlySpan<byte> privateKey)
    {
        var signingKey = ECDsa.Create();
        signingKey.ImportPkcs8PrivateKey(privateKey, out _);
        return new LocalIdentity(signingKey);
    }

    public byte[] ExportPrivateKey()
        => _signingKey.ExportPkcs8PrivateKey();

    public byte[] ExportPublicKey()
        => (byte[])_publicKey.Clone();

    public byte[] Sign(ReadOnlySpan<byte> data)
        => _signingKey.SignData(data, HashAlgorithmName.SHA256);

    public void Dispose()
    {
        _signingKey.Dispose();
    }
}
