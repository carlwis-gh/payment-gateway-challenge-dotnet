using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace PaymentGateway.Api.Tests.TestDoubles;

/// <summary>One captured log call: its message and every property from its state and scopes.</summary>
public sealed record LogEntry(string Category, LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Properties)
{
    /// <summary>Everything a log sink could write for this entry, as one string.</summary>
    public string AllText => string.Join(" | ", Properties.Select(p => $"{p.Key}={p.Value}").Prepend(Message));

    public object? Property(string name) => Properties.LastOrDefault(p => p.Key == name).Value;
}

/// <summary>
/// Records every log call made by the gateway, with its scopes, so tests can assert what would
/// reach a log pipeline (DESIGN D11, D16).
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private static IEnumerable<KeyValuePair<string, object?>> PropertiesOf(object? state) => state switch
    {
        null => Array.Empty<KeyValuePair<string, object?>>(),
        // Message-template state and dictionary scopes (object and object? are the same type at runtime).
        IEnumerable<KeyValuePair<string, object?>> pairs => pairs,
        _ => new[] { new KeyValuePair<string, object?>("Scope", state.ToString()) }
    };

    private sealed class Logger : ILogger
    {
        private readonly CapturingLoggerProvider _provider;
        private readonly string _category;

        public Logger(CapturingLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = new List<KeyValuePair<string, object?>>();
            _provider._scopes.ForEachScope((scope, list) => list.AddRange(PropertiesOf(scope)), properties);
            properties.AddRange(PropertiesOf(state));
            if (exception is not null)
            {
                properties.Add(new("Exception", exception.ToString()));
            }

            _provider.Entries.Enqueue(new LogEntry(_category, logLevel, formatter(state, exception), properties));
        }
    }
}
