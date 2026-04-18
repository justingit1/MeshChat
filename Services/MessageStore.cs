using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshChat.Models;
using Newtonsoft.Json;

namespace MeshChat.Services;

public class MessageStore
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public MessageStore()
    {
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
                Logger.Error("Failed to load messages", ex);
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
                Logger.Error("Failed to save messages", ex);
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
                Logger.Error("Failed to clear messages", ex);
            }
        }
    }
}