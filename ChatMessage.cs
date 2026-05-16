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

public record ChatMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string SenderId { get; init; } = string.Empty;
    public string SenderName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public MessageType Type { get; init; } = MessageType.Text;
    public MessageStatus Status { get; init; } = MessageStatus.Sending;
    public DateTime Timestamp { get; init; } = DateTime.Now;

    // Date separator for grouping messages by date
    public bool IsDateSeparator { get; init; }
    public string? DateSeparatorText { get; init; }

    // File transfer fields
    public string? FileName { get; init; }
    public long FileSize { get; init; }
    public string? FilePath { get; init; }
    public double FileProgress { get; init; }

    // Mesh routing fields
    public string? TargetPeerId { get; init; }   // null = broadcast
    public int HopCount { get; init; }
    public string[] VisitedNodes { get; init; } = [];

    // Transport info
    public string Transport { get; init; } = "WiFi";  // "WiFi" or "Bluetooth"

    // Message reactions (emoji -> list of user IDs)
    public Dictionary<string, List<string>> Reactions { get; init; } = [];

    public void NotifyReactionsChanged()
    {
    }
}
