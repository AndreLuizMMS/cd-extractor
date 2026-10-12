using System.Collections.ObjectModel;
using MediaRescue.Core;
using Serilog.Events;

namespace MediaRescue.App.Infrastructure;

public enum LogKind { Info, Warning, Error }

public sealed record LogLine(DateTime Time, string Text, LogKind Kind)
{
    public string Display => $"{Time:HH:mm:ss}  {Text}";
}

/// <summary>Feeds the live log panel and the Serilog file at the same time. Thread-safe.</summary>
public sealed class LogService
{
    private const int MaxLines = 300;
    private readonly IUiDispatcher _ui;
    private readonly IClock _clock;

    public LogService(IUiDispatcher ui, IClock clock) { _ui = ui; _clock = clock; }

    public ObservableCollection<LogLine> Lines { get; } = new();

    public void Info(string message) => Add(LogKind.Info, message, null);
    public void Warn(string message) => Add(LogKind.Warning, message, null);
    public void Error(string message, Exception? ex = null) => Add(LogKind.Error, message, ex);

    private void Add(LogKind kind, string message, Exception? ex)
    {
        var level = kind switch { LogKind.Error => LogEventLevel.Error, LogKind.Warning => LogEventLevel.Warning, _ => LogEventLevel.Information };
        try { Serilog.Log.Write(level, ex, "{Message}", message); } catch { /* logging must never break the app */ }
        var line = new LogLine(_clock.UtcNow.ToLocalTime(), message, kind);
        _ui.Post(() =>
        {
            Lines.Add(line);
            while (Lines.Count > MaxLines) Lines.RemoveAt(0);
        });
    }
}
