// LogBuffer.cs – Thread-safe ring buffer + logger wrapper for the monitoring dashboard
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Connector
{
    /// <summary>
    /// A single log entry captured from the application.
    /// </summary>
    public sealed class LogEntry
    {
        public DateTime Timestamp { get; }
        public LogSeverity Severity { get; }
        public string Message { get; }
        public string Source { get; }

        public LogEntry(DateTime timestamp, LogSeverity severity, string message, string source = "")
        {
            Timestamp = timestamp;
            Severity = severity;
            Message = message;
            Source = source;
        }
    }

    /// <summary>Log severity levels.</summary>
    public enum LogSeverity
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Thread-safe circular ring buffer that captures console log entries.
    /// Bounded to a maximum size; oldest entries are evicted when full.
    /// </summary>
    public sealed class LogBuffer
    {
        private readonly LogEntry[] _buffer;
        private readonly int _capacity;
        private int _head = 0;  // next write position
        private int _count = 0; // current number of entries

        /// <summary>Thread-safety lock for all public operations.</summary>
        private readonly object _lock = new object();

        public LogBuffer(int capacity = 5000)
        {
            _capacity = capacity > 0 ? capacity : 5000;
            _buffer = new LogEntry[_capacity];
        }

        /// <summary>
        /// Adds a log entry to the buffer. Thread-safe.
        /// If the buffer is full, the oldest entry is overwritten.
        /// </summary>
        public void Add(LogEntry entry)
        {
            if (entry == null) return;

            lock (_lock)
            {
                _buffer[_head] = entry;
                _head = (_head + 1) % _capacity;
                if (_count < _capacity)
                    _count++;
            }
        }

        /// <summary>
        /// Returns the last <paramref name="count"/> log entries in chronological order (oldest first).
        /// </summary>
        public List<LogEntry> GetLatest(int count = 200)
        {
            lock (_lock)
            {
                if (count <= 0) count = 200;
                if (count > _count) count = _count;

                var result = new List<LogEntry>(count);
                int start = _count > _capacity
                    ? (_head - _count + _capacity) % _capacity
                    : (_head - _count + _capacity) % _capacity;

                for (int i = 0; i < count; i++)
                {
                    int idx = (start + i) % _capacity;
                    result.Add(_buffer[idx]);
                }
                return result;
            }
        }

        /// <summary>
        /// Returns all entries currently in the buffer in chronological order.
        /// </summary>
        public List<LogEntry> GetAll() => GetLatest(int.MaxValue);

        /// <summary>Total capacity of the buffer.</summary>
        public int Capacity => _capacity;

        /// <summary>Number of entries currently stored.</summary>
        public int Count
        {
            get
            {
                lock (_lock) return _count;
            }
        }
    }

    /// <summary>
    /// A lightweight logger that writes to both the Console and the LogBuffer.
    /// Replace all Console.WriteLine / Console.Error.WriteLine calls with this.
    /// </summary>
    public sealed class ConsoleLogger
    {
        private readonly LogBuffer _buffer;

        public ConsoleLogger(LogBuffer buffer)
        {
            _buffer = buffer ?? new LogBuffer(5000);
        }

        /// <summary>Log an informational message.</summary>
        public void Info(string message, string source = "")
        {
            var entry = new LogEntry(DateTime.UtcNow, LogSeverity.Info, message, source);
            _buffer.Add(entry);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        /// <summary>Log a warning message.</summary>
        public void Warning(string message, string source = "")
        {
            var entry = new LogEntry(DateTime.UtcNow, LogSeverity.Warning, message, source);
            _buffer.Add(entry);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WARNING: {message}");
            Console.ResetColor();
        }

        /// <summary>Log an error message.</summary>
        public void Error(string message, string source = "")
        {
            var entry = new LogEntry(DateTime.UtcNow, LogSeverity.Error, message, source);
            _buffer.Add(entry);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR: {message}");
            Console.ResetColor();
        }

        /// <summary>Access the underlying log buffer for the dashboard.</summary>
        public LogBuffer Buffer => _buffer;
    }
}
