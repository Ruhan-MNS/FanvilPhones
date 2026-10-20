using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FancilPhones.Tests;

internal sealed class RecordingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;
    public void Dispose() { }

    internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception,
        KeyValuePair<string, object?>[] Properties, KeyValuePair<string, object?>[] Scope);

    private sealed class Recorder(RecordingLoggerProvider provider, string category) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider.scopes.Push(state);

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scopeValues = new List<KeyValuePair<string, object?>>();
            provider.scopes.ForEachScope((scope, values) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> properties) values.AddRange(properties);
            }, scopeValues);
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values ? values.ToArray() : [];
            provider.Entries.Enqueue(new(category, level, formatter(state, exception), exception, properties, scopeValues.ToArray()));
        }
    }
}
