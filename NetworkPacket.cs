using System;

namespace MeshChat.Models;

public enum PacketType
{
    Hello,          // peer announces itself
    HelloAck,       // response to hello with peer list
    Message,        // chat message
    MessageAck,     // delivery receipt
    ReadReceipt,    // read receipt
    FileChunk,      // file transfer chunk
    FileOffer,      // file transfer authorization offer
    FileAccept,     // file transfer authorization accept
    FileDecline,    // file transfer authorization decline
    FileComplete,   // file transfer done
    PeerList,       // share known peers (mesh relay info)
    Goodbye,        // peer is leaving
    Typing,         // typing indicator
    Reaction,       // message reaction (emoji)
    KeyExchangeInit,
    KeyExchangeResponse,
    KeyExchangeConfirm
}

public class NetworkPacket
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public PacketType Type { get; set; }
    public string SenderId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string? TargetId { get; set; }  // null = broadcast

    // Routing
    public int Ttl { get; set; } = 5;           // time-to-live hops
    public string[] VisitedNodes { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Payload (type-dependent)
    public string? Payload { get; set; }         // JSON string of actual data
    public bool IsEncrypted { get; set; }
    public string? CryptoVersion { get; set; }
    public string? CryptoSessionId { get; set; }
    public string? CryptoKeyId { get; set; }
    public string? CryptoNonce { get; set; }
    public string? CryptoTag { get; set; }
    public ulong? CryptoMessageCounter { get; set; }
    public int TcpPort { get; set; }             // sender's listening port (for Hello)

    // Peer list for mesh discovery
    public PeerInfo[]? KnownPeers { get; set; }
}

// Reaction payload for message reactions
public class ReactionPayload
{
    public string MessageId { get; set; } = string.Empty;
    public string Emoji { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public bool IsAdded { get; set; } = true; // true = added, false = removed
}
