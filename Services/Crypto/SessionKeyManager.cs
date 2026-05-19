using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using MeshChat.Models;

namespace MeshChat.Services.Crypto;

public sealed class SessionKeyManager : IDisposable
{
    public const string ProtocolVersion = "MESHCHAT-ECDH-P256-HKDF-SHA256-AESGCM-V1";
    public const string CryptoVersion = "AESGCM1";

    private static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromMinutes(10);

    private readonly string _localPeerId;
    private readonly LocalIdentity _localIdentity;
    private readonly PeerTrustStore _peerTrustStore;
    private readonly TimeSpan _sessionLifetime;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private readonly Dictionary<string, PendingOutboundHandshake> _pendingOutbound = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PeerCryptoSession> _sessionsByPeerId = new(StringComparer.Ordinal);
    private bool _disposed;

    public SessionKeyManager(
        string localPeerId,
        LocalIdentity localIdentity,
        PeerTrustStore peerTrustStore,
        TimeSpan? sessionLifetime = null,
        Func<DateTime>? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(localPeerId))
            throw new ArgumentException("Local peer ID is required.", nameof(localPeerId));

        _localPeerId = localPeerId;
        _localIdentity = localIdentity ?? throw new ArgumentNullException(nameof(localIdentity));
        _peerTrustStore = peerTrustStore ?? throw new ArgumentNullException(nameof(peerTrustStore));
        _sessionLifetime = sessionLifetime ?? DefaultSessionLifetime;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public KeyExchangeInitPayload CreateOutboundKeyExchangeInit(string peerId)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(peerId))
            throw new ArgumentException("Peer ID is required.", nameof(peerId));

        var now = _utcNow();
        var ephemeral = EphemeralKeyPair.Generate();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var payload = CreatePayload<KeyExchangeInitPayload>(
            peerId,
            ephemeral.ExportPublicKey(),
            nonce);
        Sign(payload);

        lock (_lock)
        {
            ReplacePendingOutbound(peerId, new PendingOutboundHandshake(
                GetSessionId(nonce),
                ephemeral,
                ephemeral.ExportPublicKey(),
                nonce,
                now,
                now.Add(_sessionLifetime)));
        }

        return payload;
    }

    public KeyExchangeResponsePayload ProcessInboundKeyExchangeInit(KeyExchangeInitPayload payload)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(payload);

        VerifyInboundPayload(payload, PacketType.KeyExchangeInit);

        var now = _utcNow();
        var ephemeral = EphemeralKeyPair.Generate();
        var response = CreatePayload<KeyExchangeResponsePayload>(
            payload.SenderPeerId,
            ephemeral.ExportPublicKey(),
            payload.HandshakeNonce);
        Sign(response);

        var sharedSecret = SessionCryptoService.DeriveSharedSecret(ephemeral, payload.EphemeralPublicKey);
        var salt = CreateSessionSalt(payload.HandshakeNonce, payload.EphemeralPublicKey, response.EphemeralPublicKey);
        var keyMaterial = SessionCryptoService.DeriveSessionKeys(sharedSecret, CryptoSessionRole.Responder, salt);
        CryptographicOperations.ZeroMemory(sharedSecret);

        var session = CreateSession(
            payload.SenderPeerId,
            payload.HandshakeNonce,
            response.EphemeralPublicKey,
            payload.EphemeralPublicKey,
            keyMaterial,
            now,
            now.Add(_sessionLifetime),
            isConfirmed: false);

        ephemeral.Dispose();

        lock (_lock)
        {
            _sessionsByPeerId[payload.SenderPeerId] = session;
        }

        return response;
    }

    public KeyExchangeConfirmPayload ProcessInboundKeyExchangeResponse(KeyExchangeResponsePayload payload)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(payload);

        VerifyInboundPayload(payload, PacketType.KeyExchangeResponse);

        PendingOutboundHandshake pending;
        lock (_lock)
        {
            if (!_pendingOutbound.TryGetValue(payload.SenderPeerId, out pending!))
                throw new InvalidOperationException("No pending outbound key exchange exists for this peer.");

            if (!pending.HandshakeNonce.SequenceEqual(payload.HandshakeNonce))
                throw new CryptographicException("Key exchange response nonce does not match the pending session.");

            if (_utcNow() >= pending.ExpiresAt)
                throw new InvalidOperationException("The pending key exchange has expired.");
        }

        var now = _utcNow();
        var sharedSecret = SessionCryptoService.DeriveSharedSecret(pending.LocalEphemeral, payload.EphemeralPublicKey);
        var salt = CreateSessionSalt(payload.HandshakeNonce, pending.LocalEphemeralPublicKey, payload.EphemeralPublicKey);
        var keyMaterial = SessionCryptoService.DeriveSessionKeys(sharedSecret, CryptoSessionRole.Initiator, salt);
        CryptographicOperations.ZeroMemory(sharedSecret);

        var session = CreateSession(
            payload.SenderPeerId,
            payload.HandshakeNonce,
            pending.LocalEphemeralPublicKey,
            payload.EphemeralPublicKey,
            keyMaterial,
            now,
            now.Add(_sessionLifetime),
            isConfirmed: true);

        var confirm = CreatePayload<KeyExchangeConfirmPayload>(
            payload.SenderPeerId,
            pending.LocalEphemeralPublicKey,
            payload.HandshakeNonce);
        Sign(confirm);

        lock (_lock)
        {
            pending.LocalEphemeral.Dispose();
            _pendingOutbound.Remove(payload.SenderPeerId);
            _sessionsByPeerId[payload.SenderPeerId] = session;
        }

        return confirm;
    }

    public void ProcessInboundKeyExchangeConfirm(KeyExchangeConfirmPayload payload)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(payload);

        VerifyInboundPayload(payload, PacketType.KeyExchangeConfirm);

        lock (_lock)
        {
            if (!_sessionsByPeerId.TryGetValue(payload.SenderPeerId, out var session))
                throw new InvalidOperationException("No active key exchange session exists for this peer.");

            if (!GetSessionId(payload.HandshakeNonce).Equals(session.SessionId, StringComparison.Ordinal))
                throw new CryptographicException("Key exchange confirm nonce does not match the active session.");

            if (!payload.EphemeralPublicKey.SequenceEqual(session.PeerEphemeralPublicKey))
                throw new CryptographicException("Key exchange confirm ephemeral key does not match the active session.");

            _sessionsByPeerId[payload.SenderPeerId] = session with { IsConfirmed = true };
        }
    }

    public PeerCryptoSession? GetActiveSession(string peerId)
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            return _sessionsByPeerId.TryGetValue(peerId, out var session) && _utcNow() < session.ExpiresAt
                ? CloneSession(session)
                : null;
        }
    }

    public int ExpireOldSessions()
    {
        ThrowIfDisposed();

        var now = _utcNow();
        lock (_lock)
        {
            var expiredPeers = _sessionsByPeerId
                .Where(pair => now >= pair.Value.ExpiresAt)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var peerId in expiredPeers)
                _sessionsByPeerId.Remove(peerId);

            var expiredPending = _pendingOutbound
                .Where(pair => now >= pair.Value.ExpiresAt)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var peerId in expiredPending)
            {
                _pendingOutbound[peerId].LocalEphemeral.Dispose();
                _pendingOutbound.Remove(peerId);
            }

            return expiredPeers.Count + expiredPending.Count;
        }
    }

    public void RemoveSession(string peerId)
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            _sessionsByPeerId.Remove(peerId);

            if (_pendingOutbound.Remove(peerId, out var pending))
                pending.LocalEphemeral.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_lock)
        {
            foreach (var pending in _pendingOutbound.Values)
                pending.LocalEphemeral.Dispose();

            _pendingOutbound.Clear();
            _sessionsByPeerId.Clear();
        }

        _disposed = true;
    }

    private void VerifyInboundPayload(KeyExchangePayload payload, PacketType packetType)
    {
        if (!payload.TargetPeerId.Equals(_localPeerId, StringComparison.Ordinal))
            throw new CryptographicException("Key exchange payload is not targeted to this peer.");

        if (!payload.ProtocolVersion.Equals(ProtocolVersion, StringComparison.Ordinal))
            throw new CryptographicException("Unsupported key exchange protocol version.");

        var peer = _peerTrustStore.GetByPeerId(payload.SenderPeerId)
            ?? throw new CryptographicException("Peer identity is not trusted.");

        if (peer.TrustState == TrustState.Blocked)
            throw new CryptographicException("Peer is blocked.");

        if (!peer.IdentityPublicKey.SequenceEqual(payload.IdentityPublicKey))
            throw new CryptographicException("Peer identity public key does not match the trust store.");

        using var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(peer.IdentityPublicKey, out _);

        if (!publicKey.VerifyData(
            KeyExchangeTranscript.GetBytes(packetType, payload),
            payload.Signature,
            HashAlgorithmName.SHA256))
        {
            throw new CryptographicException("Key exchange signature verification failed.");
        }
    }

    private TPayload CreatePayload<TPayload>(
        string targetPeerId,
        byte[] ephemeralPublicKey,
        byte[] handshakeNonce)
        where TPayload : KeyExchangePayload, new()
        => new()
        {
            ProtocolVersion = ProtocolVersion,
            SenderPeerId = _localPeerId,
            TargetPeerId = targetPeerId,
            IdentityPublicKey = _localIdentity.ExportPublicKey(),
            IdentityKeyId = $"fp:{_localIdentity.Fingerprint}",
            EphemeralPublicKey = (byte[])ephemeralPublicKey.Clone(),
            HandshakeNonce = (byte[])handshakeNonce.Clone(),
            SupportedCryptoVersions = [CryptoVersion]
        };

    private void Sign(KeyExchangeInitPayload payload)
        => payload.Signature = _localIdentity.Sign(payload.GetTranscriptBytes());

    private void Sign(KeyExchangeResponsePayload payload)
        => payload.Signature = _localIdentity.Sign(payload.GetTranscriptBytes());

    private void Sign(KeyExchangeConfirmPayload payload)
        => payload.Signature = _localIdentity.Sign(payload.GetTranscriptBytes());

    private static PeerCryptoSession CreateSession(
        string peerId,
        byte[] handshakeNonce,
        byte[] localEphemeralPublicKey,
        byte[] peerEphemeralPublicKey,
        SessionKeyMaterial keyMaterial,
        DateTime createdAt,
        DateTime expiresAt,
        bool isConfirmed)
        => new()
        {
            PeerId = peerId,
            SessionId = GetSessionId(handshakeNonce),
            LocalEphemeralPublicKey = (byte[])localEphemeralPublicKey.Clone(),
            PeerEphemeralPublicKey = (byte[])peerEphemeralPublicKey.Clone(),
            SendKey = (byte[])keyMaterial.SendKey.Clone(),
            ReceiveKey = (byte[])keyMaterial.ReceiveKey.Clone(),
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            SendMessageCounter = 0,
            ReceiveMessageCounter = 0,
            IsConfirmed = isConfirmed
        };

    private static string GetSessionId(byte[] handshakeNonce)
        => Convert.ToHexString(handshakeNonce).ToLowerInvariant();

    private static byte[] CreateSessionSalt(
        byte[] handshakeNonce,
        byte[] initiatorEphemeralPublicKey,
        byte[] responderEphemeralPublicKey)
    {
        var salt = new byte[handshakeNonce.Length + initiatorEphemeralPublicKey.Length + responderEphemeralPublicKey.Length];
        Buffer.BlockCopy(handshakeNonce, 0, salt, 0, handshakeNonce.Length);
        Buffer.BlockCopy(initiatorEphemeralPublicKey, 0, salt, handshakeNonce.Length, initiatorEphemeralPublicKey.Length);
        Buffer.BlockCopy(
            responderEphemeralPublicKey,
            0,
            salt,
            handshakeNonce.Length + initiatorEphemeralPublicKey.Length,
            responderEphemeralPublicKey.Length);
        return salt;
    }

    private void ReplacePendingOutbound(string peerId, PendingOutboundHandshake pending)
    {
        if (_pendingOutbound.Remove(peerId, out var existing))
            existing.LocalEphemeral.Dispose();

        _pendingOutbound[peerId] = pending;
    }

    private static PeerCryptoSession CloneSession(PeerCryptoSession session)
        => session with
        {
            LocalEphemeralPublicKey = (byte[])session.LocalEphemeralPublicKey.Clone(),
            PeerEphemeralPublicKey = (byte[])session.PeerEphemeralPublicKey.Clone(),
            SendKey = (byte[])session.SendKey.Clone(),
            ReceiveKey = (byte[])session.ReceiveKey.Clone()
        };

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record PendingOutboundHandshake(
        string SessionId,
        EphemeralKeyPair LocalEphemeral,
        byte[] LocalEphemeralPublicKey,
        byte[] HandshakeNonce,
        DateTime CreatedAt,
        DateTime ExpiresAt);
}
