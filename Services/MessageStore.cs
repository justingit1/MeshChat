using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshChat.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace MeshChat.Services;

public class MessageStore
{
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

    public List<ChatMessage> Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    return JsonConvert.DeserializeObject<List<ChatMessage>>(json) ?? new List<ChatMessage>();
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
                File.WriteAllText(_filePath, json);
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
}
