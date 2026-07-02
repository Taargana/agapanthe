namespace Agapanthe.Core;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>Minimal console logger. Structured logging can replace this later.</summary>
public static class Log
{
    public static LogLevel MinLevel { get; set; } =
#if DEBUG
        LogLevel.Debug;
#else
        LogLevel.Info;
#endif

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    private static void Write(LogLevel level, string message)
    {
        if (level < MinLevel)
        {
            return;
        }

        var output = level >= LogLevel.Warn ? Console.Error : Console.Out;
        output.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{level.ToString().ToUpperInvariant(),-5}] {message}");
    }
}
