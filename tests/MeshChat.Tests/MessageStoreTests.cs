using MeshChat.Models;
using MeshChat.Services;

namespace MeshChat.Tests;

public sealed class MessageStoreTests : IDisposable
{
    private readonly string _filePath = Path.Combine(
        Path.GetTempPath(),
        "MeshChatMessageStoreTests",
        Guid.NewGuid().ToString("N"),
        "messages.json");

    [Fact]
    public void Save_ProtectsMessageContentAtRest_AndLoadsRoundTrip()
    {
        var store = new MessageStore(_filePath);
        var messages = new[]
        {
            new ChatMessage
            {
                Id = "message-1",
                SenderId = "alice",
                SenderName = "Alice",
                Content = "secret body",
                Type = MessageType.Text
            }
        };

        store.Save(messages);

        var persisted = File.ReadAllText(_filePath);
        Assert.StartsWith("DPAPI:", persisted);
        Assert.DoesNotContain("secret body", persisted);

        var loaded = Assert.Single(store.Load());
        Assert.Equal("secret body", loaded.Content);
    }

    [Fact]
    public void Load_PlaintextStore_MigratesToProtectedStorage()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, """
        [
          {
            "Id": "message-1",
            "SenderId": "alice",
            "SenderName": "Alice",
            "Content": "legacy plaintext",
            "Type": 0
          }
        ]
        """);

        var store = new MessageStore(_filePath);

        var loaded = Assert.Single(store.Load());

        Assert.Equal("legacy plaintext", loaded.Content);
        var migrated = File.ReadAllText(_filePath);
        Assert.StartsWith("DPAPI:", migrated);
        Assert.DoesNotContain("legacy plaintext", migrated);
    }

    [Fact]
    public void Load_CorruptedProtectedStore_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, "DPAPI:not-base64");
        var store = new MessageStore(_filePath);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void Load_TamperedProtectedStore_ReturnsEmpty()
    {
        var store = new MessageStore(_filePath);
        store.Save([
            new ChatMessage
            {
                Id = "message-1",
                SenderId = "alice",
                SenderName = "Alice",
                Content = "tamper target",
                Type = MessageType.Text
            }
        ]);
        var persisted = File.ReadAllText(_filePath);
        var chars = persisted.ToCharArray();
        chars[^2] = chars[^2] == 'A' ? 'B' : 'A';
        File.WriteAllText(_filePath, new string(chars));

        Assert.Empty(store.Load());
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
