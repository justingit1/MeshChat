using System.Collections.Generic;
using System.Windows.Media;

namespace MeshChat.Models;

public enum LogLevel
{
    Info,
    WiFi,
    Bluetooth,
    FileTransfer,
    Success,
    Warning,
    Error,
    Peer,
    Sent,       // Message sent - green with arrow
    Received    // Message received - blue with arrow
}

public class LogSegment
{
    public string Text { get; set; } = "";
    public Color Color { get; set; } = Colors.White;
    public bool IsBold { get; set; }
}

public class LogEntry
{
    public string Timestamp { get; set; } = "";
    public string Tag { get; set; } = "";
    public Color TagColor { get; set; } = Colors.White;
    public string Message { get; set; } = "";
    public List<LogSegment> Segments { get; set; } = [];
    public string FullText { get; set; } = "";
}