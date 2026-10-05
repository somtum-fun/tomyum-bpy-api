// What every endpoint gets: the database, the SQL for this server's databases, the host's live view of
// players, and the plugin's settings.

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.BpyApi;

/// <summary>Settings, bound from <c>Plugins:tomyum.bpy-api</c>.</summary>
internal sealed class ApiOptions
{
    /// <summary>The website's database (accounts, vanilla scores and stats, beatmaps).</summary>
    public string OsuWebDatabase { get; set; } = "osu";

    /// <summary>TomYum's database (Relax/Autopilot scores and stats, ranked statuses, uploaded sets).</summary>
    public string TomyumDatabase { get; set; } = "tomyum";

    /// <summary>Added to TomYum's Relax/Autopilot score ids, so they never meet the website's (vanilla) ids.</summary>
    public long ModeScoreIdOffset { get; set; } = 10_000_000_000;

    /// <summary>The server's domain for links (uploaded sets' covers); empty: the request's host without its first label.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>TomYum's stable replays (<c>Replays:Path</c>), relative to the server's directory.</summary>
    public string ReplaysPath { get; set; } = "data/replays";

    /// <summary>Lazer replays as the spectator server saves them (<c>Circleguard:LazerReplaysPath</c>); empty: none.</summary>
    public string LazerReplaysPath { get; set; } = "/app/lazer-replays";

    /// <summary>What /statistics reports as the server version.</summary>
    public string Version { get; set; } = "1.0.0+tomyum-bpy-api";

    /// <summary>bancho.py's own host: <c>api.&lt;domain&gt;</c>. Empty: every host.</summary>
    public string ApiSubdomain { get; set; } = "api";

    /// <summary>The API's path there (bancho.py's).</summary>
    public string ApiPrefix { get; set; } = "/v1";

    /// <summary>The second home of the API, on the website's host. Empty subdomain: every host.</summary>
    public string WebsiteSubdomain { get; set; } = "osu";

    /// <summary>Its path there; empty turns it off.</summary>
    public string WebsitePrefix { get; set; } = "/bpy-api/v1";

    /// <summary>Most players /search_players lists (bancho.py has no limit).</summary>
    public int SearchLimit { get; set; } = 1000;
}

/// <summary>Raised by <see cref="Api.Db"/> when the plugin isn't trusted with the database.</summary>
#pragma warning disable CA1032 // only ever thrown here, with its one message
internal sealed class NoDatabaseException : Exception
#pragma warning restore CA1032
{
    public NoDatabaseException()
        : base("tomyum.bpy-api has no database access: add it to Plugins:Trusted.")
    {
    }
}

internal sealed class Api
{
    private readonly Db? _db;

    public Api(IPluginDataAccess? data, ApiOptions options, IHostPlayerView? players, IHostOnlineCounts? online, IPluginLogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _db = data is null ? null : new Db(data);
        Options = options;
        Sql = new Sql(options.OsuWebDatabase, options.TomyumDatabase, options.ModeScoreIdOffset);
        Players = players;
        Online = online;
        Logger = logger;
    }

    public ApiOptions Options { get; }

    public Sql Sql { get; }

    public IHostPlayerView? Players { get; }

    public IHostOnlineCounts? Online { get; }

    public IPluginLogger? Logger { get; }

    public bool HasDatabase => _db is not null;

    public Db Db => _db ?? throw new NoDatabaseException();

    /// <summary>When each player logged in (Unix seconds), from the host's login events.</summary>
    public ConcurrentDictionary<long, double> LoginTimes { get; } = new();

    /// <summary>
    /// Players online, as bancho.py counts its sessions: osu!stable and IRC, plus lazer (from the
    /// spectator server's presence) when the host can tell. Bots aren't counted.
    /// </summary>
    public async Task<long> OnlineCountAsync(CancellationToken cancellationToken)
    {
        if (Online is not null)
        {
            try
            {
                var counts = await Online.GetAsync(cancellationToken).ConfigureAwait(false);
                return counts.Stable + counts.Irc + (counts.Lazer ?? 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger?.Warn($"tomyum.bpy-api: counting players online failed: {ex.Message}");
            }
        }

        return Players?.OnlineCount ?? 0;
    }

    /// <summary>The domain for links: the configured one, else the request's host without its first label.</summary>
    public string Domain(PluginHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(Options.Domain))
            return Options.Domain.Trim();

        request.Headers.TryGetValue("Host", out var host);
        host ??= string.Empty;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && !host.EndsWith(']'))
            host = host[..colon];
        var dot = host.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? host[(dot + 1)..] : host;
    }

    /// <summary>Where a score's replay is: TomYum's stable file by its id, the spectator's lazer .osr by the website's id.</summary>
    public string? ReplayPath(string source, long key)
    {
        if (key <= 0)
            return null;

        var id = key.ToString(CultureInfo.InvariantCulture);
        if (source == "lazer")
            return string.IsNullOrEmpty(Options.LazerReplaysPath) ? null : Path.Combine(Options.LazerReplaysPath, id);
        return string.IsNullOrEmpty(Options.ReplaysPath) ? null : Path.GetFullPath(Path.Combine(Options.ReplaysPath, id + ".osr"));
    }
}
