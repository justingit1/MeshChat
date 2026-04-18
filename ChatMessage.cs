using System;
using System.Collections.Generic;

namespace MeshChat.Models;

public enum MessageStatus
{
    Sending,
    Sent,
    Delivered,
    Read,
    Failed
}

public enum MessageType
{
    Text,
    File,
    System,
    DateSeparator
}

public class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SenderId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public MessageType Type { get; set; } = MessageType.Text;
    public MessageStatus Status { get; set; } = MessageStatus.Sending;
    public DateTime Timestamp { get; set; } = DateTime.Now;

    // Date separator for grouping messages by date
    public bool IsDateSeparator { get; set; }
    public string? DateSeparatorText { get; set; }

    // File transfer fields
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public string? FilePath { get; set; }
    public double FileProgress { get; set; }

    // Mesh routing fields
    public string? TargetPeerId { get; set; }   // null = broadcast
    public int HopCount { get; set; } = 0;
    public string[] VisitedNodes { get; set; } = [];

    // Transport info
    public string Transport { get; set; } = "WiFi";  // "WiFi" or "Bluetooth"

    // Message reactions (emoji -> list of user IDs)
    public Dictionary<string, List<string>> Reactions { get; set; } = new();
}
