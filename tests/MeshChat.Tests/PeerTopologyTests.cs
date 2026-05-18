using System.Net;
using MeshChat.Models;

namespace MeshChat.Tests;

public sealed class PeerTopologyTests
{
    [Fact]
    public void CreateDirectPeerList_OnlyIncludesOtherOnlineDirectPeers()
    {
        var peers = new[]
        {
            new Peer { Id = "local", DisplayName = "Local", HopsAway = 1 },
            new Peer { Id = "target", DisplayName = "Target", HopsAway = 1 },
            new Peer
            {
                Id = "direct",
                DisplayName = "Direct",
                Status = PeerStatus.Online,
                IpAddress = IPAddress.Parse("192.168.1.20"),
                TcpPort = 45678,
                HopsAway = 1
            },
            new Peer { Id = "indirect", DisplayName = "Indirect", Status = PeerStatus.Online, HopsAway = 2 },
            new Peer { Id = "offline", DisplayName = "Offline", Status = PeerStatus.Offline, HopsAway = 1 }
        };

        var result = PeerTopology.CreateDirectPeerList(peers, "local", "target");

        var peer = Assert.Single(result);
        Assert.Equal("direct", peer.Id);
        Assert.Equal("Direct", peer.Name);
        Assert.Equal("192.168.1.20", peer.IpAddress);
        Assert.Equal(45678, peer.Port);
        Assert.Equal(1, peer.HopsAway);
    }

    [Fact]
    public void TryCreateIndirectPeer_UsesSenderAsRelayAndAddsAdvertisedHops()
    {
        var relay = new Peer
        {
            Id = "relay",
            DisplayName = "Relay",
            Transport = MeshChat.Models.TransportType.Bluetooth,
            HopsAway = 1
        };

        var result = PeerTopology.TryCreateIndirectPeer(
            new PeerInfo
            {
                Id = "remote",
                Name = "Remote",
                IpAddress = "10.0.0.8",
                Port = 50000,
                HopsAway = 1
            },
            relay,
            "local",
            out var peer);

        Assert.True(result);
        Assert.Equal("remote", peer.Id);
        Assert.Equal("Remote", peer.DisplayName);
        Assert.Equal(MeshChat.Models.TransportType.Bluetooth, peer.Transport);
        Assert.Equal(2, peer.HopsAway);
        Assert.Equal("relay", peer.RelayPeerId);
        Assert.False(peer.IsDirectlyConnected);
        Assert.Equal(IPAddress.Parse("10.0.0.8"), peer.IpAddress);
        Assert.Equal(50000, peer.TcpPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("local")]
    [InlineData("relay")]
    public void TryCreateIndirectPeer_RejectsInvalidSelfAndRelayPeers(string peerId)
    {
        var relay = new Peer { Id = "relay", DisplayName = "Relay", HopsAway = 1 };

        var result = PeerTopology.TryCreateIndirectPeer(
            new PeerInfo { Id = peerId, Name = "Peer", HopsAway = 1 },
            relay,
            "local",
            out _);

        Assert.False(result);
    }

    [Fact]
    public void TryCreateIndirectPeer_NormalizesInvalidAdvertisedHopCount()
    {
        var relay = new Peer { Id = "relay", DisplayName = "Relay", HopsAway = 1 };

        var result = PeerTopology.TryCreateIndirectPeer(
            new PeerInfo { Id = "remote", Name = "Remote", HopsAway = 0 },
            relay,
            "local",
            out var peer);

        Assert.True(result);
        Assert.Equal(2, peer.HopsAway);
    }
}
