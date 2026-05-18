using System.Security.Cryptography;
using System.Text;
using MeshChat.Services.Crypto;

namespace MeshChat.Tests;

public sealed class CryptoPhase1Tests
{
    [Fact]
    public void IdentityFingerprint_IsStableForCanonicalPublicKeyBytes()
    {
        using var identity = LocalIdentity.Generate();

        var publicKey = identity.ExportPublicKey();
        var expected = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();

        Assert.Equal(expected, identity.Fingerprint);
        Assert.Equal(identity.Fingerprint, Convert.ToHexString(SHA256.HashData(identity.ExportPublicKey())).ToLowerInvariant());
    }

    [Fact]
    public void TwoPeers_DeriveMatchingSharedSecretAndSessionMaterial()
    {
        using var aliceEphemeral = EphemeralKeyPair.Generate();
        using var bobEphemeral = EphemeralKeyPair.Generate();

        var aliceSecret = SessionCryptoService.DeriveSharedSecret(aliceEphemeral, bobEphemeral.ExportPublicKey());
        var bobSecret = SessionCryptoService.DeriveSharedSecret(bobEphemeral, aliceEphemeral.ExportPublicKey());
        var salt = Encoding.UTF8.GetBytes("phase-1-test-session");

        var aliceKeys = SessionCryptoService.DeriveSessionKeys(aliceSecret, CryptoSessionRole.Initiator, salt);
        var bobKeys = SessionCryptoService.DeriveSessionKeys(bobSecret, CryptoSessionRole.Responder, salt);

        Assert.Equal(aliceSecret, bobSecret);
        Assert.Equal(aliceKeys.SendKey, bobKeys.ReceiveKey);
        Assert.Equal(aliceKeys.ReceiveKey, bobKeys.SendKey);
    }

    [Fact]
    public void SessionKeys_AreDirectionallyDistinct()
    {
        using var aliceEphemeral = EphemeralKeyPair.Generate();
        using var bobEphemeral = EphemeralKeyPair.Generate();

        var sharedSecret = SessionCryptoService.DeriveSharedSecret(aliceEphemeral, bobEphemeral.ExportPublicKey());
        var keys = SessionCryptoService.DeriveSessionKeys(sharedSecret, CryptoSessionRole.Initiator);

        Assert.NotEqual(keys.SendKey, keys.ReceiveKey);
        Assert.Equal(32, keys.SendKey.Length);
        Assert.Equal(32, keys.ReceiveKey.Length);
    }

    [Fact]
    public void AesGcmDecrypt_SucceedsWithCorrectKeyAndAssociatedData()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("hello encrypted mesh");
        var aad = Encoding.UTF8.GetBytes("packet-id:123");

        var encrypted = SessionCryptoService.Encrypt(key, plaintext, aad);
        var decrypted = SessionCryptoService.Decrypt(key, encrypted, aad);

        Assert.Equal(12, encrypted.Nonce.Length);
        Assert.Equal(16, encrypted.Tag.Length);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void AesGcmDecrypt_FailsWithWrongKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("hello encrypted mesh");
        var aad = Encoding.UTF8.GetBytes("packet-id:123");
        var encrypted = SessionCryptoService.Encrypt(key, plaintext, aad);

        Assert.ThrowsAny<CryptographicException>(() =>
            SessionCryptoService.Decrypt(wrongKey, encrypted, aad));
    }

    [Fact]
    public void AesGcmDecrypt_FailsWithTamperedData()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("hello encrypted mesh");
        var aad = Encoding.UTF8.GetBytes("packet-id:123");
        var encrypted = SessionCryptoService.Encrypt(key, plaintext, aad);
        var tamperedCiphertext = (byte[])encrypted.Ciphertext.Clone();
        tamperedCiphertext[0] ^= 0x01;
        var tampered = encrypted with { Ciphertext = tamperedCiphertext };

        Assert.ThrowsAny<CryptographicException>(() =>
            SessionCryptoService.Decrypt(key, tampered, aad));
    }

    [Fact]
    public void HandshakeSignature_Verifies()
    {
        using var identity = LocalIdentity.Generate();
        using var ephemeral = EphemeralKeyPair.Generate();
        var transcript = CreateTranscript(ephemeral.ExportPublicKey());

        var signed = HandshakeCryptoService.Sign(identity, transcript);

        Assert.True(HandshakeCryptoService.Verify(identity.ExportPublicKey(), signed));
    }

    [Fact]
    public void HandshakeSignature_FailsWhenSignedFieldIsTampered()
    {
        using var identity = LocalIdentity.Generate();
        using var ephemeral = EphemeralKeyPair.Generate();
        var transcript = CreateTranscript(ephemeral.ExportPublicKey());
        var signed = HandshakeCryptoService.Sign(identity, transcript);
        var tampered = signed with
        {
            Transcript = transcript with { ReceiverPeerId = "mallory" }
        };

        Assert.False(HandshakeCryptoService.Verify(identity.ExportPublicKey(), tampered));
    }

    private static HandshakeTranscript CreateTranscript(byte[] ephemeralPublicKey)
        => new(
            "MESHCHAT-ECDH-P256-HKDF-SHA256-AESGCM-V1",
            "alice",
            "bob",
            ephemeralPublicKey,
            Encoding.UTF8.GetBytes("created-at:2026-05-18T00:00:00Z"));
}
