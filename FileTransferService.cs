using System;
using System.IO;
using System.Threading.Tasks;
using MeshChat.Models;
using Newtonsoft.Json;

namespace MeshChat.Services;

public class FileTransferInfo
{
    public string MessageId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public int TotalChunks { get; set; }
    public int ReceivedChunks { get; set; }
    public MemoryStream Buffer { get; set; } = new();
}

public class FileChunkPayload
{
    public string MessageId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public byte[]? Data { get; set; }
}

public class FileTransferService
{
    private const int ChunkSize = 32 * 1024; // 32KB per chunk

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileTransferInfo> _incoming = new();

    public event Action<string, double>? ProgressUpdated;   // messageId, 0-1
    public event Action<string, string>? FileReceived;       // messageId, saved path
    public event Action<string>? LogMessage;

    public async IAsyncEnumerable<NetworkPacket> ChunkFileAsync(
        string filePath, string targetId, string senderId, string senderName)
    {
        var fileInfo = new FileInfo(filePath);
        var messageId = Guid.NewGuid().ToString();
        var totalChunks = (int)Math.Ceiling((double)fileInfo.Length / ChunkSize);

        using var fs = File.OpenRead(filePath);
        var buffer = new byte[ChunkSize];
        int chunkIndex = 0;

        while (true)
        {
            var bytesRead = await fs.ReadAsync(buffer);
            if (bytesRead == 0) break;

            var chunk = new byte[bytesRead];
            Array.Copy(buffer, chunk, bytesRead);

            var payload = new FileChunkPayload
            {
                MessageId = messageId,
                FileName = fileInfo.Name,
                TotalSize = fileInfo.Length,
                ChunkIndex = chunkIndex,
                TotalChunks = totalChunks,
                Data = chunk
            };

            yield return new NetworkPacket
            {
                Type = PacketType.FileChunk,
                SenderId = senderId,
                SenderName = senderName,
                TargetId = targetId,
                Payload = JsonConvert.SerializeObject(payload)
            };

            ProgressUpdated?.Invoke(messageId, (double)(chunkIndex + 1) / totalChunks);
            chunkIndex++;

            // Small delay to avoid flooding the connection
            await Task.Delay(5);
        }

        Log($"Sent {chunkIndex} chunks for {fileInfo.Name}");
    }

    public void HandleChunk(NetworkPacket packet)
    {
        if (packet.Payload == null) return;

        var chunk = JsonConvert.DeserializeObject<FileChunkPayload>(packet.Payload);
        if (chunk == null) return;

        var transfer = _incoming.GetOrAdd(chunk.MessageId, _ => new FileTransferInfo
        {
            MessageId = chunk.MessageId,
            FileName = chunk.FileName,
            TotalSize = chunk.TotalSize,
            TotalChunks = chunk.TotalChunks
        });

        if (chunk.Data != null)
            transfer.Buffer.Write(chunk.Data);

        transfer.ReceivedChunks++;
        var progress = (double)transfer.ReceivedChunks / transfer.TotalChunks;
        ProgressUpdated?.Invoke(chunk.MessageId, progress);

        if (transfer.ReceivedChunks >= transfer.TotalChunks)
        {
            SaveFile(transfer);
            _incoming.TryRemove(chunk.MessageId, out _);
        }
    }

    private void SaveFile(FileTransferInfo transfer)
    {
        try
        {
            var downloadsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "MeshChat");
            Directory.CreateDirectory(downloadsPath);

            var savePath = Path.Combine(downloadsPath, transfer.FileName);
            // Avoid overwriting existing files
            if (File.Exists(savePath))
            {
                var name = Path.GetFileNameWithoutExtension(transfer.FileName);
                var ext = Path.GetExtension(transfer.FileName);
                savePath = Path.Combine(downloadsPath, $"{name}_{DateTime.Now:HHmmss}{ext}");
            }

            transfer.Buffer.Position = 0;
            using var fs = File.Create(savePath);
            transfer.Buffer.CopyTo(fs);

            Log($"File saved: {savePath}");
            FileReceived?.Invoke(transfer.MessageId, savePath);
        }
        catch (Exception ex)
        {
            Log($"Failed to save file: {ex.Message}");
        }
    }

    private void Log(string msg) => LogMessage?.Invoke($"[FileTransfer] {msg}");
}
