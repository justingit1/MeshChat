using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshChat.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace MeshChat.Services;

public sealed class PeerTrustStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly ILogger<PeerTrustStore> _logger;
    private readonly Dictionary<string, TrustedPeer> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _peerIdByFingerprint = new(StringComparer.OrdinalIgnoreCase);

    public PeerTrustStore(ILogger<PeerTrustStore>? logger = null)
        : this(CreateDefaultFilePath(), logger)
    {
    }

    public PeerTrustStore(string filePath, ILogger<PeerTrustStore>? logger = null)
    {
        _filePath = filePath;
        _logger = logger ?? NullLogger<PeerTrustStore>.Instance;
    }

    public IReadOnlyCollection<TrustedPeer> Peers
    {
        get
        {
            lock (_lock)
            {
                return _peers.Values.ToList();
            }
        }
    }

    public IReadOnlyCollection<TrustedPeer> Load()
    {
        lock (_lock)
        {
            _peers.Clear();
            _peerIdByFingerprint.Clear();

            try
            {
                if (!File.Exists(_filePath))
                    return [];

                var json = File.ReadAllText(_filePath);
                var peers = JsonConvert.DeserializeObject<List<TrustedPeer>>(json) ?? [];

                foreach (var peer in peers.Where(p => !string.IsNullOrWhiteSpace(p.PeerId)))
                    StorePeer(NormalizePeer(peer));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load trusted peers from {FilePath}", _filePath);
            }

            return _peers.Values.ToList();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var peers = _peers.Values.OrderBy(p => p.PeerId, StringComparer.Ordinal).ToList();
                var json = JsonConvert.SerializeObject(peers, Formatting.Indented);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save trusted peers to {FilePath}", _filePath);
            }
        }
    }

    public TrustedPeer UpsertPeerIdentity(
        string peerId,
        string displayName,
        byte[] identityPublicKey,
        string fingerprint,
        DateTime? seenAt = null)
    {
        if (string.IsNullOrWhiteSpace(peerId))
            throw new ArgumentException("Peer ID is required.", nameof(peerId));

        var now = seenAt ?? DateTime.UtcNow;
        var normalizedFingerprint = NormalizeFingerprint(fingerprint);
        var publicKey = (byte[])identityPublicKey.Clone();

        lock (_lock)
        {
            if (!_peers.TryGetValue(peerId, out var existing))
            {
                var created = new TrustedPeer
                {
                    PeerId = peerId,
                    DisplayName = displayName,
                    IdentityPublicKey = publicKey,
                    Fingerprint = normalizedFingerprint,
                    TrustState = TrustState.Unknown,
                    FirstSeen = now,
                    LastSeen = now
                };

                StorePeer(created);
                return created;
            }

            var identityChanged = !existing.Fingerprint.Equals(normalizedFingerprint, StringComparison.OrdinalIgnoreCase)
                || !existing.IdentityPublicKey.SequenceEqual(publicKey);

            var nextState = identityChanged && existing.TrustState == TrustState.Verified
                ? TrustState.Unknown
                : existing.TrustState;

            var updated = existing with
            {
                DisplayName = displayName,
                IdentityPublicKey = publicKey,
                Fingerprint = normalizedFingerprint,
                TrustState = nextState,
                LastSeen = now,
                VerifiedAt = nextState == TrustState.Verified ? existing.VerifiedAt : null
            };

            StorePeer(updated, existing.Fingerprint);
            return updated;
        }
    }

    public TrustedPeer? MarkVerified(string peerId, DateTime? verifiedAt = null)
        => UpdateTrustState(peerId, TrustState.Verified, verifiedAt ?? DateTime.UtcNow);

    public TrustedPeer MarkBlocked(string peerId, string displayName = "")
    {
        if (string.IsNullOrWhiteSpace(peerId))
            throw new ArgumentException("Peer ID is required.", nameof(peerId));

        lock (_lock)
        {
            if (_peers.TryGetValue(peerId, out var existing))
            {
                var updated = existing with
                {
                    TrustState = TrustState.Blocked,
                    VerifiedAt = null
                };

                StorePeer(updated, existing.Fingerprint);
                return updated;
            }

            var now = DateTime.UtcNow;
            var created = new TrustedPeer
            {
                PeerId = peerId,
                DisplayName = displayName,
                TrustState = TrustState.Blocked,
                FirstSeen = now,
                LastSeen = now
            };

            StorePeer(created);
            return created;
        }
    }

    public TrustedPeer? Unblock(string peerId)
        => UpdateTrustState(peerId, TrustState.Unknown, null);

    public TrustedPeer? GetByPeerId(string peerId)
    {
        lock (_lock)
        {
            return _peers.TryGetValue(peerId, out var peer) ? peer : null;
        }
    }

    public TrustedPeer? GetByFingerprint(string fingerprint)
    {
        var normalizedFingerprint = NormalizeFingerprint(fingerprint);

        lock (_lock)
        {
            return _peerIdByFingerprint.TryGetValue(normalizedFingerprint, out var peerId) &&
                   _peers.TryGetValue(peerId, out var peer)
                ? peer
                : null;
        }
    }

    private TrustedPeer? UpdateTrustState(string peerId, TrustState trustState, DateTime? verifiedAt)
    {
        lock (_lock)
        {
            if (!_peers.TryGetValue(peerId, out var existing))
                return null;

            var updated = existing with
            {
                TrustState = trustState,
                VerifiedAt = trustState == TrustState.Verified ? verifiedAt : null
            };

            StorePeer(updated, existing.Fingerprint);
            return updated;
        }
    }

    private void StorePeer(TrustedPeer peer, string? previousFingerprint = null)
    {
        if (!string.IsNullOrWhiteSpace(previousFingerprint) &&
            !previousFingerprint.Equals(peer.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            var normalizedPreviousFingerprint = NormalizeFingerprint(previousFingerprint);
            if (_peerIdByFingerprint.TryGetValue(normalizedPreviousFingerprint, out var mappedPeerId) &&
                mappedPeerId == peer.PeerId)
            {
                _peerIdByFingerprint.Remove(normalizedPreviousFingerprint);

                var replacement = _peers.Values.FirstOrDefault(existing =>
                    existing.PeerId != peer.PeerId &&
                    existing.Fingerprint.Equals(normalizedPreviousFingerprint, StringComparison.OrdinalIgnoreCase));

                if (replacement != null)
                    _peerIdByFingerprint[normalizedPreviousFingerprint] = replacement.PeerId;
            }
        }

        _peers[peer.PeerId] = peer;

        if (!string.IsNullOrWhiteSpace(peer.Fingerprint))
            _peerIdByFingerprint[peer.Fingerprint] = peer.PeerId;
    }

    private static TrustedPeer NormalizePeer(TrustedPeer peer)
        => peer with
        {
            IdentityPublicKey = peer.IdentityPublicKey ?? [],
            Fingerprint = NormalizeFingerprint(peer.Fingerprint)
        };

    private static string NormalizeFingerprint(string fingerprint)
        => fingerprint.Trim().ToLowerInvariant();

    private static string CreateDefaultFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDir = Path.Combine(appData, "MeshChat", "Data");
        return Path.Combine(dataDir, "trusted-peers.json");
    }
}
