using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using MySqlConnector;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.BpyApi.Tests;

internal sealed class FakeConfig : IPluginConfig
{
    private readonly Dictionary<string, string> _values;

    public FakeConfig(Dictionary<string, string>? values = null) => _values = values ?? new Dictionary<string, string>();

    public T? Get<T>(string key, T? defaultValue = default)
    {
        if (!_values.TryGetValue(key, out var raw))
            return defaultValue;
        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
    }

    public TOptions Bind<TOptions>()
        where TOptions : new() => new();
}

internal sealed class FakeLogger : IPluginLogger
{
    public List<string> Lines { get; } = new();

    public void Info(string message) => Lines.Add("info: " + message);

    public void Warn(string message) => Lines.Add("warn: " + message);

    public void Error(string message, Exception? exception = null) => Lines.Add("error: " + message + " " + exception);
}

internal sealed class FakeEvents : IEventBus
{
    private readonly List<(Type Type, Func<object, CancellationToken, Task> Handler)> _handlers = new();

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : IPluginEvent
    {
        var entry = (typeof(TEvent), (Func<object, CancellationToken, Task>)((e, ct) => handler((TEvent)e, ct)));
        _handlers.Add(entry);
        return new Disposer(() => _handlers.Remove(entry));
    }

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IPluginEvent
    {
        foreach (var (type, handler) in _handlers.ToList())
        {
            if (type == typeof(TEvent))
                await handler(@event, cancellationToken).ConfigureAwait(false);
        }
    }

    public int Count => _handlers.Count;
}

internal sealed class FakeCommands : ICommandRegistry
{
    public IDisposable Register(string name, string description, Func<CommandInvocation, CancellationToken, Task<string?>> handler) =>
        new Disposer(() => { });
}

/// <summary>The host's route table, as PluginRouteTable/PluginHttpMiddleware match it (first match, subdomain = first host label).</summary>
internal sealed class FakeHttp : IHttpRouteRegistry
{
    public List<(string Method, string Pattern, Regex Regex, List<string> Names, string? Subdomain, Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>> Handler)> Routes { get; } = new();

    public IDisposable Map(
        string method,
        string pattern,
        Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>> handler,
        bool requireAuth = false,
        string? subdomain = null)
    {
        var names = new List<string>();
        var builder = new StringBuilder("^");
        var segments = pattern.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
                builder.Append('/');
            var segment = segments[i];
            if (segment.Length >= 2 && segment[0] == '{' && segment[^1] == '}')
            {
                names.Add(segment[1..^1]);
                builder.Append("(?<").Append(segment[1..^1]).Append(">[^/]+)");
            }
            else
            {
                builder.Append(Regex.Escape(segment));
            }
        }

        builder.Append('$');
        var entry = (method, pattern, new Regex(builder.ToString(), RegexOptions.CultureInvariant), names, subdomain, handler);
        Routes.Add(entry);
        return new Disposer(() => Routes.Remove(entry));
    }

    /// <summary>Sends a request the way the host would; null when no plugin route matches (the host would carry on).</summary>
    public async Task<PluginHttpResponse?> SendAsync(string method, string host, string pathAndQuery)
    {
        var question = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = question < 0 ? pathAndQuery : pathAndQuery[..question];
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (question >= 0)
        {
            foreach (var pair in pathAndQuery[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
                var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                query[key] = query.TryGetValue(key, out var existing) ? existing + "," + value : value;
            }
        }

        var dot = host.IndexOf('.', StringComparison.Ordinal);
        var label = dot > 0 ? host[..dot] : null;
        foreach (var route in Routes.ToList())
        {
            if (!string.Equals(route.Method, method, StringComparison.OrdinalIgnoreCase))
                continue;
            if (route.Subdomain is not null && !string.Equals(route.Subdomain, label, StringComparison.OrdinalIgnoreCase))
                continue;
            var match = route.Regex.Match(path);
            if (!match.Success)
                continue;

            var values = route.Names.ToDictionary(n => n, n => match.Groups[n].Value, StringComparer.OrdinalIgnoreCase);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = host };
            var request = new PluginHttpRequest(method, path, values, query, headers, null, null);
            return await route.Handler(request, CancellationToken.None).ConfigureAwait(false);
        }

        return null;
    }
}

internal sealed class FakePlayers : IHostPlayerView
{
    public List<PluginPlayerInfo> Players { get; } = new();

    public int OnlineCount => Players.Count;

    public IReadOnlyList<PluginPlayerInfo> OnlinePlayers => Players;

    public PluginPlayerInfo? FindByUserId(long userId) => Players.FirstOrDefault(p => p.UserId == userId);

    public PluginPlayerInfo? FindByUsername(string username) =>
        Players.FirstOrDefault(p => string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));
}

internal sealed class FakeOnline : IHostOnlineCounts
{
    public HostOnlineCounts Counts { get; set; } = new(3, 1, 2, 0);

    public Task<HostOnlineCounts> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Counts);
}

/// <summary>A live MySQL connection, switched to read-only before the plugin gets it: nothing can be written.</summary>
internal sealed class ReadOnlyData : IPluginDataAccess
{
    private readonly string _connectionString;

    public ReadOnlyData(string connectionString) => _connectionString = connectionString;

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SET SESSION TRANSACTION READ ONLY";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }
}

internal sealed class FakeContext : IPluginContext
{
    public FakeContext(IPluginDataAccess? data = null, Dictionary<string, string>? config = null)
    {
        Data = data;
        Config = new FakeConfig(config);
    }

    public IPluginConfig Config { get; }

    public FakeLogger Log { get; } = new();

    public IPluginLogger Logger => Log;

    public FakeEvents Bus { get; } = new();

    public IEventBus Events => Bus;

    public ICommandRegistry Commands { get; } = new FakeCommands();

    public FakeHttp Routes { get; } = new();

    public IHttpRouteRegistry Http => Routes;

    public IPluginDataAccess? Data { get; }

    public FakePlayers Players { get; } = new();

    public FakeOnline Online { get; } = new();

    public TService? GetHostService<TService>()
        where TService : class =>
        typeof(TService) == typeof(IHostPlayerView) ? (TService)(object)Players
        : typeof(TService) == typeof(IHostOnlineCounts) ? (TService)(object)Online
        : null;
}

internal sealed class Disposer : IDisposable
{
    private Action? _action;

    public Disposer(Action action) => _action = action;

    public void Dispose()
    {
        _action?.Invoke();
        _action = null;
    }
}
