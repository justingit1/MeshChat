using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MeshChat.Logging;

// MeshChat uses the Microsoft.Extensions.Logging abstractions throughout the app.
// App.xaml.cs creates one ILoggerFactory, registers this provider, and injects typed
// ILogger<T> instances into view models and services. Keep UI log coloring in
// MainViewModel/LogEntryToInlinesConverter; this provider is only the durable file sink.
public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly object _writeLock = new();
    private bool _disposed;

    public DailyFileLoggerProvider()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _logDirectory = Path.Combine(appData, "MeshChat", "Logs");
        Directory.CreateDirectory(_logDirectory);
    }

    public ILogger CreateLogger(string categoryName)
        => new DailyFileLogger(categoryName, _logDirectory, _writeLock, () => _disposed);

    public void Dispose()
    {
        _disposed = true;
    }

    private sealed class DailyFileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly string _logDirectory;
        private readonly object _writeLock;
        private readonly Func<bool> _isDisposed;

        public DailyFileLogger(
            string categoryName,
            string logDirectory,
            object writeLock,
            Func<bool> isDisposed)
        {
            _categoryName = categoryName;
            _logDirectory = logDirectory;
            _writeLock = writeLock;
            _isDisposed = isDisposed;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
            => logLevel != Microsoft.Extensions.Logging.LogLevel.None;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (_isDisposed() || !IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception == null)
                return;

            var timestamp = DateTime.Now;
            var entry = new StringBuilder()
                .Append('[').Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ")
                .Append('[').Append(logLevel).Append("] ")
                .Append('[').Append(_categoryName).Append("] ")
                .Append(message);

            var properties = GetStructuredProperties(state);
            if (properties.Count > 0)
            {
                entry.Append(" | ");
                entry.Append(string.Join(", ", properties.Select(p => $"{p.Key}={p.Value}")));
            }

            if (exception != null)
                entry.AppendLine().Append(exception);

            var filePath = Path.Combine(_logDirectory, $"meshchat_{timestamp:yyyy-MM-dd}.log");

            lock (_writeLock)
            {
                try
                {
                    // The target file is resolved per write, so long-running sessions
                    // automatically rotate when the calendar day changes.
                    File.AppendAllText(filePath, entry + Environment.NewLine);
                }
                catch
                {
                    // Logging must never crash the chat app; failures here are ignored.
                }
            }
        }

        private static List<KeyValuePair<string, object?>> GetStructuredProperties<TState>(TState state)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values)
                return [];

            return values
                .Where(value => value.Key != "{OriginalFormat}")
                .ToList();
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
