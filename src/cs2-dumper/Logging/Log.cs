using System.Globalization;

namespace Cs2Dumper.Logging;

public enum LogLevel
{
    Error = 1,
    Warn = 2,
    Info = 3,
    Debug = 4,
    Trace = 5,
}

/// <summary>
/// Tiny leveled logger replacing <c>log</c> + <c>simplelog</c>: a console sink at the
/// verbosity-derived level (warn/error to stderr, the rest to stdout) and an optional
/// <c>cs2-dumper.log</c> file sink fixed at Info, matching the reference's two-logger setup.
/// </summary>
public static class Log
{
    private static LogLevel _consoleLevel = LogLevel.Error;
    private const LogLevel FileLevel = LogLevel.Info;
    private static TextWriter? _file;
    private static readonly object Gate = new();

    public static void Init(LogLevel consoleLevel, string? logFilePath)
    {
        _consoleLevel = consoleLevel;
        if (logFilePath is not null)
        {
            _file = new StreamWriter(File.Create(logFilePath)) { AutoFlush = true };
        }
    }

    public static LogLevel ConsoleLevelFromVerbosity(int verbose) => verbose switch
    {
        0 => LogLevel.Error,
        1 => LogLevel.Warn,
        2 => LogLevel.Info,
        3 => LogLevel.Debug,
        _ => LogLevel.Trace,
    };

    public static void Error(string message) => Emit(LogLevel.Error, message);

    public static void Warn(string message) => Emit(LogLevel.Warn, message);

    public static void Info(string message) => Emit(LogLevel.Info, message);

    public static void Debug(string message) => Emit(LogLevel.Debug, message);

    public static void Trace(string message) => Emit(LogLevel.Trace, message);

    private static void Emit(LogLevel level, string message)
    {
        lock (Gate)
        {
            if (level <= _consoleLevel)
            {
                TextWriter sink = level <= LogLevel.Warn ? Console.Error : Console.Out;
                sink.WriteLine($"[{Name(level)}] {message}");
            }

            if (_file is not null && level <= FileLevel)
            {
                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                _file.WriteLine($"{ts} [{Name(level)}] {message}");
            }
        }
    }

    private static string Name(LogLevel level) => level switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN",
        LogLevel.Info => "INFO",
        LogLevel.Debug => "DEBUG",
        _ => "TRACE",
    };
}
