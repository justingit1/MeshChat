using System.Security.Cryptography;
using MeshChat.Models;
using MeshChat.Services;
using Newtonsoft.Json;

namespace MeshChat.Tests;

public sealed class FileTransferServiceTests : IDisposable
{
    private readonly List<string> _pathsToDelete = [];

    [Fact]
    public async Task ChunkFileAsync_UsesCallerProvidedMessageId()
    {
        var filePath = CreateTempFile("hello");
        var service = new FileTransferService();
        var messageId = "caller-message-id";

        var packet = await SingleAsync(service.ChunkFileAsync(
                filePath,
                targetId: "target",
                senderId: "sender",
                senderName: "Sender",
                messageId: messageId));

        var payload = DeserializePayload(packet);
        Assert.Equal(messageId, payload.MessageId);
        Assert.Equal(PacketType.FileChunk, packet.Type);
    }

    [Fact]
    public async Task ChunkFileAsync_ZeroByteFile_ProducesSingleEmptyChunk()
    {
        var filePath = CreateTempFile("");
        var service = new FileTransferService();

        var packet = await SingleAsync(service.ChunkFileAsync(
                filePath,
                targetId: "target",
                senderId: "sender",
                senderName: "Sender",
                messageId: "zero-byte-message"));

        var payload = DeserializePayload(packet);
        Assert.Equal(0, payload.TotalSize);
        Assert.Equal(0, payload.ChunkIndex);
        Assert.Equal(1, payload.TotalChunks);
        Assert.Equal(string.Empty, payload.Data);
        Assert.False(string.IsNullOrWhiteSpace(payload.FileSha256));
    }

    [Fact]
    public void HandleChunk_RejectsInconsistentMetadata()
    {
        var service = new FileTransferService();
        var received = false;
        service.FileReceived += (_, path) =>
        {
            received = true;
            _pathsToDelete.Add(path);
        };

        service.HandleChunk(CreatePacket(new FileChunkPayload
        {
            MessageId = "inconsistent-metadata",
            FileName = "first.txt",
            TotalSize = 32769,
            TotalChunks = 2,
            ChunkIndex = 0,
            Data = Convert.ToBase64String(new byte[32768])
        }));

        service.HandleChunk(CreatePacket(new FileChunkPayload
        {
            MessageId = "inconsistent-metadata",
            FileName = "second.txt",
            TotalSize = 32769,
            TotalChunks = 2,
            ChunkIndex = 1,
            Data = Convert.ToBase64String([1])
        }));

        Assert.False(received);
    }

    [Fact]
    public void HandleChunk_Sha256Mismatch_DoesNotRaiseFileReceived()
    {
        var service = new FileTransferService();
        var received = false;
        service.FileReceived += (_, path) =>
        {
            received = true;
            _pathsToDelete.Add(path);
        };

        service.HandleChunk(CreatePacket(new FileChunkPayload
        {
            MessageId = "sha-mismatch",
            FileName = UniqueFileName("mismatch.txt"),
            TotalSize = 3,
            TotalChunks = 1,
            ChunkIndex = 0,
            FileSha256 = new string('0', 64),
            Data = Convert.ToBase64String("abc"u8.ToArray())
        }));

        Assert.False(received);
    }

    [Fact]
    public void HandleChunk_MissingSha256_AllowsLegacyByteCountValidatedTransfer()
    {
        var service = new FileTransferService();
        var received = new List<(string MessageId, string Path)>();
        service.FileReceived += (messageId, path) =>
        {
            received.Add((messageId, path));
            _pathsToDelete.Add(path);
        };

        var bytes = "legacy"u8.ToArray();
        service.HandleChunk(CreatePacket(new FileChunkPayload
        {
            MessageId = "legacy-no-hash",
            FileName = UniqueFileName("legacy.txt"),
            TotalSize = bytes.Length,
            TotalChunks = 1,
            ChunkIndex = 0,
            FileSha256 = null,
            Data = Convert.ToBase64String(bytes)
        }));

        var item = Assert.Single(received);
        Assert.Equal("legacy-no-hash", item.MessageId);
        Assert.Equal(bytes, File.ReadAllBytes(item.Path));
    }

    [Fact]
    public void HandleChunk_RaisesFileStartedOnceWithMetadata()
    {
        var service = new FileTransferService();
        var started = new List<FileTransferStartedInfo>();
        service.FileStarted += started.Add;
        service.FileReceived += (_, path) => _pathsToDelete.Add(path);

        service.HandleChunk(CreatePacket(new FileChunkPayload
        {
            MessageId = "started-metadata",
            FileName = "started.txt",
            TotalSize = 32769,
            TotalChunks = 2,
            ChunkIndex = 0,
            Data = Convert.ToBase64String(new byte[32768])
        }));
        service.HandleChunk(CreatePacket(new FileChunkPayload
        {
            MessageId = "started-metadata",
            FileName = "started.txt",
            TotalSize = 32769,
            TotalChunks = 2,
            ChunkIndex = 0,
            Data = Convert.ToBase64String(new byte[32768])
        }));
        service.HandleChunk(CreatePacket(new FileChunkPayload
        {
            MessageId = "started-metadata",
            FileName = "started.txt",
            TotalSize = 32769,
            TotalChunks = 2,
            ChunkIndex = 1,
            Data = Convert.ToBase64String([1])
        }));

        var item = Assert.Single(started);
        Assert.Equal("started-metadata", item.MessageId);
        Assert.Equal("started.txt", item.FileName);
        Assert.Equal(32769, item.TotalSize);
        Assert.Equal("sender", item.SenderId);
        Assert.Equal("Sender", item.SenderName);
    }

    public void Dispose()
    {
        foreach (var path in _pathsToDelete)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private string CreateTempFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), UniqueFileName("meshchat-test.txt"));
        File.WriteAllText(path, contents);
        _pathsToDelete.Add(path);
        return path;
    }

    private static string UniqueFileName(string suffix)
        => $"{Guid.NewGuid():N}-{suffix}";

    private static NetworkPacket CreatePacket(FileChunkPayload payload)
        => new()
        {
            Type = PacketType.FileChunk,
            SenderId = "sender",
            SenderName = "Sender",
            Payload = JsonConvert.SerializeObject(payload)
        };

    private static FileChunkPayload DeserializePayload(NetworkPacket packet)
        => JsonConvert.DeserializeObject<FileChunkPayload>(packet.Payload!)!;

    private static async Task<NetworkPacket> SingleAsync(IAsyncEnumerable<NetworkPacket> packets)
    {
        var collected = new List<NetworkPacket>();
        await foreach (var packet in packets)
            collected.Add(packet);

        return Assert.Single(collected);
    }
}
