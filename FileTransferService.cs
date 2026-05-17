using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshChat.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace MeshChat.Services;

public class FileTransferInfo
{
    public string MessageId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public int TotalChunks { get; set; }
    public int ReceivedChunks { get; set; }
    public long ReceivedBytes { get; set; }
    public HashSet<int> ReceivedChunkIndexes { get; } = [];
    public MemoryStream Buffer { get; set; } = new();
}

public class FileChunkPayload
{
    public string MessageId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public string? Data { get; set; }
}

public interface IFileTransferService
{
    event Action<string, double>? ProgressUpdated;   // messageId, 0-1
    event Action<string, string>? FileReceived;       // messageId, saved path
    event Action<string>? LogMessage;

    IAsyncEnumerable<NetworkPacket> ChunkFileAsync(
        string filePath,
        string targetId,
        string senderId,
        string senderName,
        CancellationToken cancellationToken = default,
        string? messageId = null);

    void HandleChunk(NetworkPacket packet);
}

public class FileTransferService : IFileTransferService
{
    private const int ChunkSize = 32 * 1024; // 32KB per chunk
    private const string ServiceName = "FileTransfer";

    private readonly ILogger<FileTransferService> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileTransferInfo> _incoming = new();

    public event Action<string, double>? ProgressUpdated;   // messageId, 0-1
    public event Action<string, string>? FileReceived;       // messageId, saved path
    public event Action<string>? LogMessage;

    public FileTransferService(ILogger<FileTransferService>? logger = null)
    {
        _logger = logger ?? NullLogger<FileTransferService>.Instance;
    }

    public async IAsyncEnumerable<NetworkPacket> ChunkFileAsync(
        string filePath,
        string targetId,
        string senderId,
        string senderName,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        string? messageId = null)
    {
        var fileInfo = new FileInfo(filePath);
        messageId ??= Guid.NewGuid().ToString();
        var totalChunks = Math.Max(1, (int)Math.Ceiling((double)fileInfo.Length / ChunkSize));

        using var fs = File.OpenRead(filePath);
        var buffer = new byte[ChunkSize];
        var chunkBuffer = buffer.AsMemory();
        int chunkIndex = 0;

        while (true)
        {
            // Reuse one 32KB Memory<byte> buffer for all reads. The payload stores
            // base64 text directly, matching the previous byte[] JSON shape while
            // avoiding a separate per-chunk byte[] allocation before serialization.
            var bytesRead = await fs.ReadAsync(chunkBuffer, cancellationToken);
            if (bytesRead == 0 && fileInfo.Length > 0) break;

            var payload = new FileChunkPayload
            {
                MessageId = messageId,
                FileName = fileInfo.Name,
                TotalSize = fileInfo.Length,
                ChunkIndex = chunkIndex,
                TotalChunks = totalChunks,
                Data = Convert.ToBase64String(chunkBuffer.Span[..bytesRead])
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
            if (fileInfo.Length == 0) break;

            // Small delay to avoid flooding the connection
            await Task.Delay(5, cancellationToken);
        }

        _logger.LogInformation(
            "Sent {ChunkCount} chunks for {FileName}",
            chunkIndex,
            fileInfo.Name);
        Log($"Sent {chunkIndex} chunks for {fileInfo.Name}");
    }

    public void HandleChunk(NetworkPacket packet)
    {
        if (packet.Payload == null) return;

        var chunk = JsonConvert.DeserializeObject<FileChunkPayload>(packet.Payload);
        if (chunk == null) return;
        if (!IsValidChunkMetadata(chunk)) return;

        var transfer = _incoming.GetOrAdd(chunk.MessageId, _ => new FileTransferInfo
        {
            MessageId = chunk.MessageId,
            FileName = chunk.FileName,
            TotalSize = chunk.TotalSize,
            TotalChunks = chunk.TotalChunks,
            // Pre-size the receive buffer when practical so MemoryStream does not
            // repeatedly allocate and copy as chunks arrive.
            Buffer = CreateReceiveBuffer(chunk.TotalSize)
        });
        if (transfer.FileName != chunk.FileName ||
            transfer.TotalSize != chunk.TotalSize ||
            transfer.TotalChunks != chunk.TotalChunks)
        {
            Log($"Rejected inconsistent chunk metadata for {chunk.MessageId}");
            return;
        }

        var decodedBytes = 0;
        if (!string.IsNullOrEmpty(chunk.Data))
        {
            // Decode into a pooled byte[] and write through Span<byte>; this avoids
            // allocating a fresh receive buffer for every chunk.
            var maxDecodedBytes = (chunk.Data.Length / 4) * 3;
            var rented = ArrayPool<byte>.Shared.Rent(maxDecodedBytes);
            try
            {
                if (!Convert.TryFromBase64String(chunk.Data, rented, out decodedBytes))
                    return;

                if (!IsExpectedChunkSize(chunk, decodedBytes))
                    return;

                transfer.Buffer.Position = (long)chunk.ChunkIndex * ChunkSize;
                transfer.Buffer.Write(rented.AsSpan(0, decodedBytes));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        else if (!IsExpectedChunkSize(chunk, decodedBytes))
        {
            return;
        }

        if (!transfer.ReceivedChunkIndexes.Add(chunk.ChunkIndex))
            return;

        transfer.ReceivedChunks++;
        transfer.ReceivedBytes += decodedBytes;
        var progress = (double)transfer.ReceivedChunks / transfer.TotalChunks;
        ProgressUpdated?.Invoke(chunk.MessageId, progress);

        if (transfer.ReceivedChunks >= transfer.TotalChunks)
        {
            if (transfer.ReceivedBytes == transfer.TotalSize)
                SaveFile(transfer);
            else
                Log($"Rejected incomplete file {transfer.FileName}: expected {transfer.TotalSize} bytes, received {transfer.ReceivedBytes}");

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

            var safeFileName = SanitizeFileName(transfer.FileName);
            var savePath = Path.Combine(downloadsPath, safeFileName);
            // Avoid overwriting existing files
            if (File.Exists(savePath))
            {
                var name = Path.GetFileNameWithoutExtension(safeFileName);
                var ext = Path.GetExtension(safeFileName);
                savePath = Path.Combine(downloadsPath, $"{name}_{DateTime.Now:HHmmss}{ext}");
            }

            transfer.Buffer.Position = 0;
            using var fs = File.Create(savePath);
            transfer.Buffer.CopyTo(fs);

            _logger.LogInformation("File saved to {SavePath}", savePath);
            Log($"File saved: {savePath}");
            FileReceived?.Invoke(transfer.MessageId, savePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save file {FileName}", transfer.FileName);
            Log($"Failed to save file: {ex.Message}");
        }
    }

    private static MemoryStream CreateReceiveBuffer(long totalSize)
    {
        return totalSize is > 0 and <= int.MaxValue
            ? new MemoryStream((int)totalSize)
            : new MemoryStream();
    }

    private static bool IsValidChunkMetadata(FileChunkPayload chunk)
    {
        return !string.IsNullOrWhiteSpace(chunk.MessageId) &&
               chunk.TotalSize >= 0 &&
               chunk.TotalChunks > 0 &&
               chunk.ChunkIndex >= 0 &&
               chunk.ChunkIndex < chunk.TotalChunks;
    }

    private static bool IsExpectedChunkSize(FileChunkPayload chunk, int decodedBytes)
    {
        var expectedBytes = chunk.TotalSize == 0
            ? 0
            : (int)Math.Min(ChunkSize, chunk.TotalSize - (long)chunk.ChunkIndex * ChunkSize);

        return expectedBytes >= 0 && decodedBytes == expectedBytes;
    }

    private static string SanitizeFileName(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "received_file";

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            safeName = safeName.Replace(invalidChar, '_');

        return safeName;
    }

    private void Log(string msg) => LogMessage?.Invoke($"[{ServiceName}] {msg}");
}
