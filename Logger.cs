using System;
using System.IO;
using MeshChat.Models;

namespace MeshChat;

public static class Logger
{
    private static readonly string LogFilePath;
    private static readonly object LockObj = new();

    static Logger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logDir = Path.Combine(appData, "MeshChat", "Logs");
        Directory.CreateDirectory(logDir);
        LogFilePath = Path.Combine(logDir, $"meshchat_{DateTime.Now:yyyy-MM-dd}.log");
    }

    public static void Log(string message, LogLevel level = LogLevel.Info)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        lock (LockObj)
        {
            try
            {
                File.AppendAllText(LogFilePath, entry + Environment.NewLine);
            }
            catch { }
        }
    }

    public static void Error(string message, Exception? ex = null)
    {
        var msg = ex != null ? $"{message}: {ex.Message}\n{ex.StackTrace}" : message;
        Log(msg, Models.LogLevel.Error);
    }

    public static void Warning(string message) => Log(message, Models.LogLevel.Warning);
    public static void Info(string message) => Log(message, Models.LogLevel.Info);
}