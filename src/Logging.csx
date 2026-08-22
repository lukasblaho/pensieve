#nullable enable
// Logging.csx
// Minimal leveled logger: writes to console and to a daily rolling log file.

using System;
using System.IO;

public enum LogLevel
{
    Info,
    Warn,
    Error
}

public sealed class Logger
{
    private readonly string _logsDir;
    private readonly object _lock = new object();

    public Logger(string logsDir)
    {
        _logsDir = logsDir;
        Directory.CreateDirectory(_logsDir);
    }

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Error(string message) => Write(LogLevel.Error, message);

    public void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message} | Exception: {ex}");

    private void Write(LogLevel level, string message)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var line = $"[{timestamp}] [{level.ToString().ToUpperInvariant()}] {message}";

        lock (_lock)
        {
            var color = level switch
            {
                LogLevel.Warn => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                _ => Console.ForegroundColor
            };

            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(line);
            Console.ForegroundColor = previous;

            var logFile = Path.Combine(_logsDir, $"agent-{DateTime.UtcNow:yyyy-MM-dd}.log");
            File.AppendAllText(logFile, line + Environment.NewLine);
        }
    }
}
