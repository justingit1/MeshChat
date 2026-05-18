using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace MeshChat.Models;

public static class PeerTopology
{
    public static PeerInfo[] CreateDirectPeerList(
        IEnumerable<Peer> peers,
        string localId,
        string targetPeerId)
    {
        return peers
            .Where(peer => peer.IsDirectlyConnected)
            .Where(peer => peer.Status != PeerStatus.Offline)
            .Where(peer => !IsSamePeer(peer.Id, localId))
            .Where(peer => !IsSamePeer(peer.Id, targetPeerId))
            .Select(peer => new PeerInfo
            {
                Id = peer.Id,
                Name = peer.DisplayName,
                IpAddress = peer.IpAddress?.ToString(),
                Port = peer.TcpPort,
                HopsAway = peer.HopsAway
            })
            .ToArray();
    }

    public static bool TryCreateIndirectPeer(
        PeerInfo peerInfo,
        Peer relayPeer,
        string localId,
        out Peer peer)
    {
        peer = default!;

        if (string.IsNullOrWhiteSpace(peerInfo.Id) ||
            IsSamePeer(peerInfo.Id, localId) ||
            IsSamePeer(peerInfo.Id, relayPeer.Id))
        {
            return false;
        }

        var advertisedHops = Math.Max(1, peerInfo.HopsAway);
        var relayHops = Math.Max(1, relayPeer.HopsAway);
        var hopsAway = relayHops + advertisedHops;

        peer = new Peer
        {
            Id = peerInfo.Id,
            DisplayName = string.IsNullOrWhiteSpace(peerInfo.Name) ? "Unknown peer" : peerInfo.Name,
            Status = PeerStatus.Online,
            Transport = relayPeer.Transport,
            SignalStrength = 0,
            IpAddress = TryParseIpAddress(peerInfo.IpAddress),
            TcpPort = peerInfo.Port,
            HopsAway = hopsAway,
            RelayPeerId = relayPeer.Id,
            LastSeen = DateTime.Now
        };

        return true;
    }

    private static bool IsSamePeer(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static IPAddress? TryParseIpAddress(string? value)
        => IPAddress.TryParse(value, out var address) ? address : null;
}
