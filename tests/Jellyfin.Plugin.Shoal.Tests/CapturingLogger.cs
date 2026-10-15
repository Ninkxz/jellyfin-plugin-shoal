using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Collects every log line (at every level, including Trace and Debug) so a test can search them for secrets.
/// </summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly object _lock = new();
    private readonly List<string> _lines = [];

    /// <summary>Gets the lines logged so far: level, message and the exception's type and message.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lock)
            {
                return _lines.ToList();
            }
        }
    }

    /// <summary>Gets all lines as one text.</summary>
    public string All => string.Join('\n', Lines);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var text = logLevel + " " + formatter(state, exception);
        if (exception is not null)
        {
            text += " | " + exception.GetType().FullName + ": " + exception.Message;
        }

        lock (_lock)
        {
            _lines.Add(text);
        }
    }
}
