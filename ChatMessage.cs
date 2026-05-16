using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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

public class ChatMessage : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SenderId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public MessageType Type { get; set; } = MessageType.Text;

    private MessageStatus _status = MessageStatus.Sending;
    public MessageStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public DateTime Timestamp { get; set; } = DateTime.Now;

    // Date separator for grouping messages by date
    public bool IsDateSeparator { get; set; }
    public string? DateSeparatorText { get; set; }

    // File transfer fields
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    private string? _filePath;
    public string? FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    private double _fileProgress;
    public double FileProgress
    {
        get => _fileProgress;
        set => SetProperty(ref _fileProgress, value);
    }

    // Mesh routing fields
    public string? TargetPeerId { get; set; }   // null = broadcast
    public int HopCount { get; set; } = 0;
    public string[] VisitedNodes { get; set; } = [];

    // Transport info
    public string Transport { get; set; } = "WiFi";  // "WiFi" or "Bluetooth"

    // Message reactions (emoji -> list of user IDs)
    private Dictionary<string, List<string>> _reactions = new();
    public Dictionary<string, List<string>> Reactions
    {
        get => _reactions;
        set => SetProperty(ref _reactions, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyReactionsChanged()
        => OnPropertyChanged(nameof(Reactions));

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
