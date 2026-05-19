using System;

namespace MeshChat.Models;

public sealed record PeerCryptoSession
{
    public string PeerId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public byte[] LocalEphemeralPublicKey { get; init; } = [];
    public byte[] PeerEphemeralPublicKey { get; init; } = [];
    public byte[] SendKey { get; init; } = [];
    public byte[] ReceiveKey { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public ulong SendMessageCounter { get; init; }
    public ulong ReceiveMessageCounter { get; init; }
    public bool IsConfirmed { get; init; }
}
