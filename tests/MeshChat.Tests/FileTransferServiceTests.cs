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

        var first = new FileChunkPayload
        {
            MessageId = "inconsistent-metadata",
            FileName = "first.txt",
            TotalSize = 32769,
            TotalChunks = 2,
            ChunkIndex = 0,
            Data = Convert.ToBase64String(new byte[32768])
        };
        Accept(service, first);

        service.HandleChunk(CreatePacket(first));

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

        var payload = new FileChunkPayload
        {
            MessageId = "sha-mismatch",
            FileName = UniqueFileName("mismatch.txt"),
            TotalSize = 3,
            TotalChunks = 1,
            ChunkIndex = 0,
            FileSha256 = new string('0', 64),
            Data = Convert.ToBase64String("abc"u8.ToArray())
        };
        Accept(service, payload);

        service.HandleChunk(CreatePacket(payload));

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
        var payload = new FileChunkPayload
        {
            MessageId = "legacy-no-hash",
            FileName = UniqueFileName("legacy.txt"),
            TotalSize = bytes.Length,
            TotalChunks = 1,
            ChunkIndex = 0,
            FileSha256 = null,
            Data = Convert.ToBase64String(bytes)
        };
        Accept(service, payload);

        service.HandleChunk(CreatePacket(payload));

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

        var first = new FileChunkPayload
        {
            MessageId = "started-metadata",
            FileName = "started.txt",
            TotalSize = 32769,
            TotalChunks = 2,
            ChunkIndex = 0,
            Data = Convert.ToBase64String(new byte[32768])
        };
        Accept(service, first);

        service.HandleChunk(CreatePacket(first));
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

    [Fact]
    public void HandleChunk_RejectsOversizedFileMetadata()
    {
        var service = new FileTransferService();
        var started = false;
        service.FileStarted += _ => started = true;

        service.HandleChunk(CreatePacket(new FileChunkPayload
        {
            MessageId = "oversized-file",
            FileName = "large.bin",
            TotalSize = 100L * 1024 * 1024 + 1,
            TotalChunks = 3201,
            ChunkIndex = 0,
            Data = Convert.ToBase64String(new byte[32768])
        }));

        Assert.False(started);
    }

    [Fact]
    public void HandleChunk_RejectsOversizedEncodedChunk()
    {
        var service = new FileTransferService();
        var started = false;
        service.FileStarted += _ => started = true;

        service.HandleChunk(CreatePacket(new FileChunkPayload
        {
            MessageId = "oversized-chunk",
            FileName = "chunk.bin",
            TotalSize = 32769,
            TotalChunks = 2,
            ChunkIndex = 0,
            Data = Convert.ToBase64String(new byte[32769])
        }));

        Assert.False(started);
    }

    [Fact]
    public void HandleChunk_RejectsExtremeTotalChunksBeforeTransferAllocation()
    {
        var service = new FileTransferService();
        var started = false;
        service.FileStarted += _ => started = true;

        service.HandleChunk(CreatePacket(new FileChunkPayload
        {
            MessageId = "extreme-total-chunks",
            FileName = "extreme.bin",
            TotalSize = 1,
            TotalChunks = int.MaxValue,
            ChunkIndex = 0,
            Data = Convert.ToBase64String([1])
        }));

        Assert.False(started);
    }

    [Fact]
    public void HandleChunk_RejectsOversizedJsonPayloadBeforeParsing()
    {
        var service = new FileTransferService();
        var started = false;
        service.FileStarted += _ => started = true;

        service.HandleChunk(new NetworkPacket
        {
            Type = PacketType.FileChunk,
            SenderId = "sender",
            SenderName = "Sender",
            Payload = new string('{', 50000)
        });

        Assert.False(started);
    }

    [Fact]
    public void HandleChunk_BeforeOffer_IsRejected()
    {
        var service = new FileTransferService();
        var started = false;
        service.FileStarted += _ => started = true;

        service.HandleChunk(CreatePacket(CreateOneChunkPayload("before-offer")));

        Assert.False(started);
    }

    [Fact]
    public void HandleChunk_AfterOfferBeforeAccept_IsRejected()
    {
        var service = new FileTransferService();
        var started = false;
        service.FileStarted += _ => started = true;
        var payload = CreateOneChunkPayload("before-accept");

        RegisterOffer(service, payload);
        service.HandleChunk(CreatePacket(payload));

        Assert.False(started);
    }

    [Fact]
    public void HandleChunk_AfterDecline_IsRejected()
    {
        var service = new FileTransferService();
        var started = false;
        service.FileStarted += _ => started = true;
        var payload = CreateOneChunkPayload("declined");

        RegisterOffer(service, payload);
        Assert.True(service.DeclineIncomingTransfer("sender", payload.MessageId));
        service.HandleChunk(CreatePacket(payload));

        Assert.False(started);
    }

    [Fact]
    public void HandleChunk_AfterAccept_IsAccepted()
    {
        var service = new FileTransferService();
        var received = new List<(string MessageId, string Path)>();
        service.FileReceived += (messageId, path) =>
        {
            received.Add((messageId, path));
            _pathsToDelete.Add(path);
        };
        var payload = CreateOneChunkPayload("accepted");

        Accept(service, payload);
        service.HandleChunk(CreatePacket(payload));

        var item = Assert.Single(received);
        Assert.Equal(payload.MessageId, item.MessageId);
    }

    [Fact]
    public void HandleChunk_AfterCompletionReplay_IsRejected()
    {
        var service = new FileTransferService();
        var received = new List<(string MessageId, string Path)>();
        service.FileReceived += (messageId, path) =>
        {
            received.Add((messageId, path));
            _pathsToDelete.Add(path);
        };
        var payload = CreateOneChunkPayload("replay-complete");

        Accept(service, payload);
        service.HandleChunk(CreatePacket(payload));
        service.HandleChunk(CreatePacket(payload));

        Assert.Single(received);
    }

    [Fact]
    public void HandleChunk_AfterCancel_IsRejected()
    {
        var service = new FileTransferService();
        var started = false;
        service.FileStarted += _ => started = true;
        var payload = CreateOneChunkPayload("canceled");

        RegisterOffer(service, payload);
        Assert.True(service.CancelIncomingTransfer("sender", payload.MessageId));
        service.HandleChunk(CreatePacket(payload));

        Assert.False(started);
    }

    [Fact]
    public void HandleChunk_TooManyActiveOffers_RejectsExcess()
    {
        var service = new FileTransferService();

        for (var i = 0; i < 32; i++)
            Assert.True(RegisterOffer(service, CreateOneChunkPayload($"active-{i}")));

        Assert.False(RegisterOffer(service, CreateOneChunkPayload("active-overflow")));
    }

    [Fact]
    public void CleanupExpiredIncomingTransfers_RemovesExpiredOffer()
    {
        var service = new FileTransferService();
        var payload = CreateOneChunkPayload("expired");
        RegisterOffer(service, payload);

        SetAuthorizationExpiry(service, "sender", payload.MessageId, DateTime.UtcNow.AddSeconds(-1));

        Assert.Equal(1, service.CleanupExpiredIncomingTransfers());
        Assert.False(service.AcceptIncomingTransfer("sender", payload.MessageId));
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

    private static FileChunkPayload CreateOneChunkPayload(string messageId)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(messageId);
        return new FileChunkPayload
        {
            MessageId = messageId,
            FileName = $"{messageId}.txt",
            TotalSize = bytes.Length,
            TotalChunks = 1,
            ChunkIndex = 0,
            Data = Convert.ToBase64String(bytes)
        };
    }

    private static bool RegisterOffer(FileTransferService service, FileChunkPayload payload)
        => service.RegisterIncomingOffer(new FileTransferOfferInfo(
            payload.MessageId,
            payload.FileName,
            payload.TotalSize,
            payload.TotalChunks,
            "sender",
            "Sender",
            "target",
            payload.FileSha256));

    private static void Accept(FileTransferService service, FileChunkPayload payload)
    {
        Assert.True(RegisterOffer(service, payload));
        Assert.True(service.AcceptIncomingTransfer("sender", payload.MessageId));
    }

    private static void SetAuthorizationExpiry(
        FileTransferService service,
        string senderId,
        string messageId,
        DateTime expiresAt)
    {
        var field = typeof(FileTransferService).GetField(
            "_incomingAuthorizations",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var authorizations = (System.Collections.IDictionary)field.GetValue(service)!;
        var key = $"{senderId}\n{messageId}";
        var current = authorizations[key]!;
        var updated = current.GetType().GetMethod("<Clone>$")!.Invoke(current, null)!;
        current.GetType().GetProperty("ExpiresAt")!.SetValue(updated, expiresAt);
        authorizations[key] = updated;
    }

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
