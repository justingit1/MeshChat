using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MeshChat.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace MeshChat.Services;

public class MessageStore
{
    private const string ProtectedPayloadPrefix = "DPAPI:";

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly ILogger<MessageStore> _logger;

    public MessageStore(ILogger<MessageStore>? logger = null)
    {
        _logger = logger ?? NullLogger<MessageStore>.Instance;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDir = Path.Combine(appData, "MeshChat", "Data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "messages.json");
    }

    public MessageStore(string filePath, ILogger<MessageStore>? logger = null)
    {
        _logger = logger ?? NullLogger<MessageStore>.Instance;
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        _filePath = filePath;
    }

    public List<ChatMessage> Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = ReadProtectedJson(File.ReadAllText(_filePath), out var wasPlaintext);
                    var messages = JsonConvert.DeserializeObject<List<ChatMessage>>(json) ?? new List<ChatMessage>();
                    if (wasPlaintext)
                        Save(messages);

                    return messages;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load messages from {FilePath}", _filePath);
            }
            return new List<ChatMessage>();
        }
    }

    public void Save(IEnumerable<ChatMessage> messages)
    {
        lock (_lock)
        {
            try
            {
                var json = JsonConvert.SerializeObject(messages, Formatting.Indented);
                File.WriteAllText(_filePath, ProtectJson(json));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save messages to {FilePath}", _filePath);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath))
                    File.Delete(_filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear messages at {FilePath}", _filePath);
            }
        }
    }

    private static string ProtectJson(string json)
    {
        var protectedBytes = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(json),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        return ProtectedPayloadPrefix + Convert.ToBase64String(protectedBytes);
    }

    private static string ReadProtectedJson(string persisted, out bool wasPlaintext)
    {
        if (!persisted.StartsWith(ProtectedPayloadPrefix, StringComparison.Ordinal))
        {
            wasPlaintext = true;
            return persisted;
        }

        wasPlaintext = false;
        var protectedBytes = Convert.FromBase64String(persisted[ProtectedPayloadPrefix.Length..]);
        var jsonBytes = ProtectedData.Unprotect(
            protectedBytes,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        return System.Text.Encoding.UTF8.GetString(jsonBytes);
    }
}
