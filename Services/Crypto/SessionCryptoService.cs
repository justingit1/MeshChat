using System;
using System.Security.Cryptography;
using System.Text;

namespace MeshChat.Services.Crypto;

public enum CryptoSessionRole
{
    Initiator,
    Responder
}

public sealed record SessionKeyMaterial(byte[] SendKey, byte[] ReceiveKey);

public sealed record AesGcmEncryptedPayload(byte[] Nonce, byte[] Ciphertext, byte[] Tag);

public static class SessionCryptoService
{
    private const int Aes256KeySize = 32;
    private const int AesGcmNonceSize = 12;
    private const int AesGcmTagSize = 16;

    private static readonly byte[] InitiatorToResponderInfo =
        Encoding.UTF8.GetBytes("MeshChat phase1 AES-256-GCM initiator-to-responder");

    private static readonly byte[] ResponderToInitiatorInfo =
        Encoding.UTF8.GetBytes("MeshChat phase1 AES-256-GCM responder-to-initiator");

    public static byte[] DeriveSharedSecret(EphemeralKeyPair localEphemeral, ReadOnlySpan<byte> remotePublicKey)
    {
        using var remote = ECDiffieHellman.Create();
        remote.ImportSubjectPublicKeyInfo(remotePublicKey, out _);
        return localEphemeral.KeyAgreement.DeriveRawSecretAgreement(remote.PublicKey);
    }

    public static SessionKeyMaterial DeriveSessionKeys(
        ReadOnlySpan<byte> sharedSecret,
        CryptoSessionRole localRole,
        ReadOnlySpan<byte> salt = default)
    {
        var initiatorToResponder = HkdfSha256(sharedSecret, salt, InitiatorToResponderInfo, Aes256KeySize);
        var responderToInitiator = HkdfSha256(sharedSecret, salt, ResponderToInitiatorInfo, Aes256KeySize);

        return localRole == CryptoSessionRole.Initiator
            ? new SessionKeyMaterial(initiatorToResponder, responderToInitiator)
            : new SessionKeyMaterial(responderToInitiator, initiatorToResponder);
    }

    public static AesGcmEncryptedPayload Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData = default)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcmNonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcmTagSize];

        using var aes = new AesGcm(key, AesGcmTagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        return new AesGcmEncryptedPayload(nonce, ciphertext, tag);
    }

    public static byte[] Decrypt(
        ReadOnlySpan<byte> key,
        AesGcmEncryptedPayload payload,
        ReadOnlySpan<byte> associatedData = default)
    {
        var plaintext = new byte[payload.Ciphertext.Length];

        using var aes = new AesGcm(key, AesGcmTagSize);
        aes.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plaintext, associatedData);

        return plaintext;
    }

    private static byte[] HkdfSha256(
        ReadOnlySpan<byte> inputKeyMaterial,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> info,
        int outputLength)
    {
        Span<byte> defaultSalt = stackalloc byte[32];
        ReadOnlySpan<byte> effectiveSalt = salt.IsEmpty ? defaultSalt : salt;

        byte[] pseudoRandomKey;
        using (var hmac = new HMACSHA256(effectiveSalt.ToArray()))
        {
            pseudoRandomKey = hmac.ComputeHash(inputKeyMaterial.ToArray());
        }

        var output = new byte[outputLength];
        var previous = Array.Empty<byte>();
        var offset = 0;
        byte counter = 1;

        using var expandHmac = new HMACSHA256(pseudoRandomKey);
        while (offset < outputLength)
        {
            var blockInput = new byte[previous.Length + info.Length + 1];
            Buffer.BlockCopy(previous, 0, blockInput, 0, previous.Length);
            info.CopyTo(blockInput.AsSpan(previous.Length));
            blockInput[^1] = counter++;

            previous = expandHmac.ComputeHash(blockInput);
            var bytesToCopy = Math.Min(previous.Length, outputLength - offset);
            Buffer.BlockCopy(previous, 0, output, offset, bytesToCopy);
            offset += bytesToCopy;
        }

        CryptographicOperations.ZeroMemory(pseudoRandomKey);
        CryptographicOperations.ZeroMemory(previous);
        return output;
    }
}
