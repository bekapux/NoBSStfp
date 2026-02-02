using System;
using System.Diagnostics;
using System.Threading;

namespace NoBSSftp.Services;

public static class LoggingService
{
    private static readonly Lock _gate = new();

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Warn(string message)
    {
        Write("WARN", message);
    }

    public static void Error(string message, Exception? ex = null)
    {
        var payload = ex is null ? message : $"{message}{Environment.NewLine}{ex}";
        Write("ERROR", payload);
    }

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:O}] {level} {message}";
        lock (_gate)
        {
            Console.WriteLine(line);
            Debug.WriteLine(line);
        }
    }
}
