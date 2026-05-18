using System;
using System.Security.Cryptography;

namespace MeshChat.Services.Crypto;

public sealed class EphemeralKeyPair : IDisposable
{
    private readonly byte[] _publicKey;

    internal ECDiffieHellman KeyAgreement { get; }

    private EphemeralKeyPair(ECDiffieHellman keyAgreement)
    {
        KeyAgreement = keyAgreement;
        _publicKey = KeyAgreement.ExportSubjectPublicKeyInfo();
    }

    public byte[] PublicKey => ExportPublicKey();

    public static EphemeralKeyPair Generate()
        => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    public byte[] ExportPublicKey()
        => (byte[])_publicKey.Clone();

    public void Dispose()
    {
        KeyAgreement.Dispose();
    }
}
