using System;

namespace MeshChat.Models;

public enum TrustState
{
    Unknown,
    Verified,
    Blocked
}

public sealed record TrustedPeer
{
    public string PeerId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public byte[] IdentityPublicKey { get; init; } = [];
    public string Fingerprint { get; init; } = string.Empty;
    public TrustState TrustState { get; init; } = TrustState.Unknown;
    public DateTime FirstSeen { get; init; } = DateTime.UtcNow;
    public DateTime LastSeen { get; init; } = DateTime.UtcNow;
    public DateTime? VerifiedAt { get; init; }
}
