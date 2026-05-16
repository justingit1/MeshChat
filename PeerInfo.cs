namespace MeshChat.Models;

public record PeerInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? IpAddress { get; init; }
    public int Port { get; init; }
    public int HopsAway { get; init; }
}
