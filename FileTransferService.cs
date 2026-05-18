using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
    public string? ExpectedFileSha256 { get; set; }
    public bool HashValidationUnavailableLogged { get; set; }
    public HashSet<int> ReceivedChunkIndexes { get; } = [];
    public string PartialPath { get; set; } = string.Empty;
}

public class FileChunkPayload
{
    public string MessageId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public string? FileSha256 { get; set; }
    public string? Data { get; set; }
}

public record FileTransferStartedInfo(
    string MessageId,
    string FileName,
    long TotalSize,
    string SenderId,
    string SenderName,
    string? TargetId);

public interface IFileTransferService
{
    event Action<FileTransferStartedInfo>? FileStarted;
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

    public event Action<FileTransferStartedInfo>? FileStarted;
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
        var fileSha256 = await ComputeFileSha256Async(filePath, cancellationToken);

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
                FileSha256 = fileSha256,
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

        FileChunkPayload? chunk;
        try
        {
            chunk = JsonConvert.DeserializeObject<FileChunkPayload>(packet.Payload);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Rejected malformed file chunk payload");
            Log("Rejected malformed file chunk payload");
            return;
        }

        if (chunk == null) return;
        if (!IsValidChunkMetadata(chunk)) return;

        var transfer = GetOrCreateTransfer(chunk, out var isNewTransfer);
        if (transfer.FileName != chunk.FileName ||
            transfer.TotalSize != chunk.TotalSize ||
            transfer.TotalChunks != chunk.TotalChunks)
        {
            Log($"Rejected inconsistent chunk metadata for {chunk.MessageId}");
            CleanupTransfer(chunk.MessageId, transfer);
            return;
        }

        if (!IsExpectedFileHash(transfer, chunk))
        {
            Log($"Rejected inconsistent file hash metadata for {chunk.MessageId}");
            CleanupTransfer(chunk.MessageId, transfer);
            return;
        }

        if (transfer.ReceivedChunkIndexes.Contains(chunk.ChunkIndex))
            return;

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
                {
                    CleanupTransfer(chunk.MessageId, transfer);
                    return;
                }

                if (!IsExpectedChunkSize(chunk, decodedBytes))
                {
                    CleanupTransfer(chunk.MessageId, transfer);
                    return;
                }

                WriteChunkToPartialFile(transfer.PartialPath, chunk.ChunkIndex, rented.AsSpan(0, decodedBytes));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        else if (!IsExpectedChunkSize(chunk, decodedBytes))
        {
            CleanupTransfer(chunk.MessageId, transfer);
            return;
        }
        else
        {
            EnsurePartialFileExists(transfer.PartialPath);
        }

        if (!transfer.ReceivedChunkIndexes.Add(chunk.ChunkIndex))
            return;

        transfer.ReceivedChunks++;
        transfer.ReceivedBytes += decodedBytes;
        var progress = (double)transfer.ReceivedChunks / transfer.TotalChunks;
        if (isNewTransfer)
        {
            FileStarted?.Invoke(new FileTransferStartedInfo(
                chunk.MessageId,
                transfer.FileName,
                transfer.TotalSize,
                packet.SenderId,
                packet.SenderName,
                packet.TargetId));
        }

        ProgressUpdated?.Invoke(chunk.MessageId, progress);

        if (transfer.ReceivedChunks >= transfer.TotalChunks)
        {
            if (transfer.ReceivedBytes == transfer.TotalSize)
            {
                if (IsCompleteFileHashValid(transfer))
                    SaveFile(transfer);
                else
                    CleanupPartialFile(transfer.PartialPath);
            }
            else
            {
                Log($"Rejected incomplete file {transfer.FileName}: expected {transfer.TotalSize} bytes, received {transfer.ReceivedBytes}");
                CleanupPartialFile(transfer.PartialPath);
            }

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

            File.Move(transfer.PartialPath, savePath);

            _logger.LogInformation("File saved to {SavePath}", savePath);
            Log($"File saved: {savePath}");
            FileReceived?.Invoke(transfer.MessageId, savePath);
        }
        catch (Exception ex)
        {
            CleanupPartialFile(transfer.PartialPath);
            _logger.LogError(ex, "Failed to save file {FileName}", transfer.FileName);
            Log($"Failed to save file: {ex.Message}");
        }
    }

    private static void WriteChunkToPartialFile(string partialPath, int chunkIndex, ReadOnlySpan<byte> bytes)
    {
        EnsurePartialFileExists(partialPath);
        using var fs = new FileStream(partialPath, FileMode.Open, FileAccess.Write, FileShare.Read);
        fs.Position = (long)chunkIndex * ChunkSize;
        fs.Write(bytes);
    }

    private static void EnsurePartialFileExists(string partialPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        using var _ = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
    }

    private FileTransferInfo GetOrCreateTransfer(FileChunkPayload chunk, out bool isNewTransfer)
    {
        if (_incoming.TryGetValue(chunk.MessageId, out var existing))
        {
            isNewTransfer = false;
            return existing;
        }

        var transfer = new FileTransferInfo
        {
            MessageId = chunk.MessageId,
            FileName = chunk.FileName,
            TotalSize = chunk.TotalSize,
            TotalChunks = chunk.TotalChunks,
            ExpectedFileSha256 = NormalizeFileHash(chunk.FileSha256),
            PartialPath = GetPartialFilePath(chunk.MessageId)
        };

        if (_incoming.TryAdd(chunk.MessageId, transfer))
        {
            ResetPartialFile(transfer.PartialPath);
            isNewTransfer = true;
            return transfer;
        }

        isNewTransfer = false;
        return _incoming[chunk.MessageId];
    }

    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var fs = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(fs, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private bool IsExpectedFileHash(FileTransferInfo transfer, FileChunkPayload chunk)
    {
        var chunkHash = NormalizeFileHash(chunk.FileSha256);
        if (chunkHash == null)
        {
            if (transfer.ExpectedFileSha256 == null && !transfer.HashValidationUnavailableLogged)
            {
                Log($"Hash validation unavailable for {transfer.FileName}; using byte-count validation only");
                transfer.HashValidationUnavailableLogged = true;
            }

            return true;
        }

        transfer.ExpectedFileSha256 ??= chunkHash;
        return string.Equals(transfer.ExpectedFileSha256, chunkHash, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCompleteFileHashValid(FileTransferInfo transfer)
    {
        if (transfer.ExpectedFileSha256 == null)
            return true;

        var actualHash = ComputeFileSha256(transfer.PartialPath);
        if (string.Equals(actualHash, transfer.ExpectedFileSha256, StringComparison.OrdinalIgnoreCase))
            return true;

        Log($"Rejected corrupted file {transfer.FileName}: SHA-256 mismatch");
        return false;
    }

    private static string ComputeFileSha256(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        var hash = SHA256.HashData(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeFileHash(string? fileSha256)
    {
        return string.IsNullOrWhiteSpace(fileSha256)
            ? null
            : fileSha256.Trim().ToLowerInvariant();
    }

    private static string GetPartialFilePath(string messageId)
    {
        var partialDirectory = Path.Combine(GetReceiveDirectory(), ".partial");
        Directory.CreateDirectory(partialDirectory);
        return Path.Combine(partialDirectory, $"{SanitizeFileName(messageId)}.part");
    }

    private static void ResetPartialFile(string partialPath)
    {
        using var _ = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    private void CleanupTransfer(string messageId, FileTransferInfo transfer)
    {
        _incoming.TryRemove(messageId, out _);
        CleanupPartialFile(transfer.PartialPath);
    }

    private static void CleanupPartialFile(string partialPath)
    {
        if (string.IsNullOrWhiteSpace(partialPath))
            return;

        try
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);
        }
        catch
        {
            // Best-effort cleanup; the original transfer error is reported by the caller.
        }
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

    private static string GetReceiveDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "MeshChat");
    }

    private void Log(string msg) => LogMessage?.Invoke($"[{ServiceName}] {msg}");
}
