using MeshChat.Models;
using MeshChat.Services;

namespace MeshChat.Tests;

public sealed class WiFiServiceTests
{
    [Fact]
    public async Task ConnectToPeerAsync_HelloAckDiscovery_UsesRemoteAdvertisedTcpPort()
    {
        using var alice = new WiFiService
        {
            LocalId = "alice",
            LocalName = "Alice"
        };
        using var bob = new WiFiService
        {
            LocalId = "bob",
            LocalName = "Bob"
        };

        var aliceSawBob = new TaskCompletionSource<Peer>(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.PeerDiscovered += peer =>
        {
            if (peer.Id == "bob")
                aliceSawBob.TrySetResult(peer);
        };

        try
        {
            await alice.StartAsync();
            await bob.StartAsync();

            await alice.ConnectToPeerAsync("127.0.0.1", bob.ListenPort);

            var discovered = await aliceSawBob.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(bob.ListenPort, discovered.TcpPort);
        }
        finally
        {
            await alice.StopAsync();
            await bob.StopAsync();
        }
    }
}
