// bancho.py's v1 API for TomYum — entry point: settings, routes, and the login-time hook.
//
// Serves every endpoint of bancho.py's app/api/v1/api.py (the osu!somtum fork) with the same paths,
// parameters, JSON and errors, on api.<domain>/v1/... (bancho.py's host) and
// osu.<domain>/bpy-api/v1/..., from TomYum's and the website's databases. Needs only
// TomYum.Plugins.Abstractions, and Plugins:Trusted for the database. Human docs: README.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.BpyApi;

/// <summary>An API handler.</summary>
internal delegate Task<PluginHttpResponse> Endpoint(Api api, PluginHttpRequest request, CancellationToken cancellationToken);

/// <summary>The plugin: bancho.py's v1 API over TomYum.</summary>
public sealed class BpyApiPlugin : IPlugin
{
    public const string Id = "tomyum.bpy-api";

    private readonly List<IDisposable> _registrations = new();

    public PluginManifest Manifest { get; } = new(
        Id: Id,
        Name: "bancho.py API (v1)",
        Version: "1.0.0",
        Authors: "Phapoom Saksri",
        Description: "bancho.py's v1 API on api.<domain>/v1 and osu.<domain>/bpy-api/v1, over TomYum's data.");

    internal Api? Api { get; private set; }

    /// <summary>Every bancho.py v1 route: method, name (the path after /v1/), handler.</summary>
    internal static IReadOnlyList<(string Method, string Name, Endpoint Handler)> Routes { get; } = BuildRoutes();

    private static List<(string Method, string Name, Endpoint Handler)> BuildRoutes()
    {
        var routes = new List<(string Method, string Name, Endpoint Handler)>
        {
            ("GET", "calculate_pp", Unsupported.CalculatePpAsync),
            ("GET", "search_players", Players.SearchAsync),
            ("GET", "get_player_count", Players.CountAsync),
            ("GET", "get_player_info", Players.InfoAsync),
            ("GET", "special_players", Players.SpecialAsync),
            ("GET", "get_recent_scores", Scores.RecentAsync),
            ("GET", "get_recent_players", Players.RecentAsync),
            ("GET", "get_top_osu_std_scores", Scores.TopStdAsync),
            ("GET", "get_player_status", Players.StatusAsync),
            ("GET", "get_player_scores", Scores.PlayerAsync),
            ("GET", "get_player_achievements", Players.AchievementsAsync),
            ("GET", "get_player_most_played", Players.MostPlayedAsync),
            ("GET", "get_map_info", Maps.InfoAsync),
            ("GET", "get_beatmapset_info", Maps.SetInfoAsync),
            ("GET", "get_map_scores", Scores.MapAsync),
            ("GET", "get_score_info", Scores.InfoAsync),
            ("GET", "get_replay", Scores.ReplayAsync),
            ("GET", "get_match", Unsupported.MatchAsync),
            ("GET", "get_leaderboard", Leaderboards.PlayersAsync),
            ("GET", "get_user_private_info", Unsupported.PrivateInfoAsync),
            ("GET", "statistics", Players.StatisticsAsync),
            ("GET", "get_all_users", Players.AllUsersAsync),
            ("GET", "get_top_play", Scores.TopPlayAsync),
            ("GET", "get_clan", Leaderboards.ClanAsync),
            ("GET", "get_clans_leaderboard", Leaderboards.ClansAsync),
            ("GET", "get_mappool", Unsupported.MappoolAsync),
            ("GET", "search_beatmaps", Maps.SearchAsync),
            ("GET", "get_active_matches", Unsupported.ActiveMatchesAsync),
            ("GET", "get_total_scores", Scores.TotalAsync),
            ("GET", "get_rank_one_history", RankHistory.RankOneAsync),
        };
        routes.AddRange(Unsupported.BotActions.Select(name => ("POST", name, (Endpoint)Unsupported.BotActionAsync)));
        return routes;
    }

    /// <summary>Where the API is served: (subdomain or null for any host, path prefix).</summary>
    internal static List<(string? Subdomain, string Prefix)> Mounts(ApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var mounts = new List<(string? Subdomain, string Prefix)>();
        void Add(string subdomain, string prefix)
        {
            prefix = (prefix ?? string.Empty).Trim().TrimEnd('/');
            if (prefix.Length == 0)
                return;
            if (!prefix.StartsWith('/'))
                prefix = "/" + prefix;
            var host = string.IsNullOrWhiteSpace(subdomain) ? null : subdomain.Trim();
            if (!mounts.Contains((host, prefix)))
                mounts.Add((host, prefix));
        }

        Add(options.ApiSubdomain, options.ApiPrefix);
        Add(options.WebsiteSubdomain, options.WebsitePrefix);
        return mounts;
    }

    internal static ApiOptions ReadOptions(IPluginConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var defaults = new ApiOptions();
        return new ApiOptions
        {
            OsuWebDatabase = config.Get("osuWebDatabase", defaults.OsuWebDatabase) ?? defaults.OsuWebDatabase,
            TomyumDatabase = config.Get("tomyumDatabase", defaults.TomyumDatabase) ?? defaults.TomyumDatabase,
            ModeScoreIdOffset = config.Get("modeScoreIdOffset", defaults.ModeScoreIdOffset),
            Domain = config.Get("domain", defaults.Domain) ?? string.Empty,
            ReplaysPath = config.Get("replaysPath", defaults.ReplaysPath) ?? string.Empty,
            LazerReplaysPath = config.Get("lazerReplaysPath", defaults.LazerReplaysPath) ?? string.Empty,
            Version = config.Get("version", defaults.Version) ?? defaults.Version,
            ApiSubdomain = config.Get("apiSubdomain", defaults.ApiSubdomain) ?? string.Empty,
            ApiPrefix = config.Get("apiPrefix", defaults.ApiPrefix) ?? string.Empty,
            WebsiteSubdomain = config.Get("websiteSubdomain", defaults.WebsiteSubdomain) ?? string.Empty,
            WebsitePrefix = config.Get("websitePrefix", defaults.WebsitePrefix) ?? string.Empty,
            SearchLimit = Math.Clamp(config.Get("searchLimit", defaults.SearchLimit), 1, 100_000),
        };
    }

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var logger = context.Logger;
        var options = ReadOptions(context.Config);
        var api = new Api(
            context.Data, options, context.GetHostService<IHostPlayerView>(), context.GetHostService<IHostOnlineCounts>(), logger);
        Api = api;

        if (!api.HasDatabase)
        {
            logger.Warn($"{Id}: no database access, so only the endpoints that need none answer. Add \"{Id}\" to Plugins:Trusted.");
        }
        else
        {
            await ProbeSchemaAsync(api, logger, cancellationToken).ConfigureAwait(false);
        }

        _registrations.Add(context.Events.Subscribe<PlayerLoginEvent>((e, _) =>
        {
            api.LoginTimes[e.UserId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            return Task.CompletedTask;
        }));

        var mounts = Mounts(options);
        foreach (var (subdomain, prefix) in mounts)
        {
            foreach (var (method, name, handler) in Routes)
                _registrations.Add(context.Http.Map(method, $"{prefix}/{name}", (request, ct) => RunAsync(api, handler, request, ct), subdomain: subdomain));

            // Anything else under the prefix: FastAPI's 404, or 405 for a known path with another method.
            foreach (var method in new[] { "GET", "POST", "PUT", "PATCH", "DELETE" })
            {
                _registrations.Add(context.Http.Map(
                    method,
                    prefix + "/{path}",
                    (request, _) => Task.FromResult(Fallback(request)),
                    subdomain: subdomain));
            }
        }

        logger.Info($"{Id}: bancho.py's v1 API on " + string.Join(", ", mounts.Select(m => (m.Subdomain is null ? "*" : m.Subdomain + ".<domain>") + m.Prefix)));
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        foreach (var registration in _registrations)
            registration.Dispose();
        _registrations.Clear();
        return Task.CompletedTask;
    }

    internal static PluginHttpResponse Fallback(PluginHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.RouteValues.TryGetValue("path", out var name);
        return Routes.Any(r => r.Name == name) ? Respond.MethodNotAllowed() : Respond.NotFound();
    }

    internal static async Task<PluginHttpResponse> RunAsync(Api api, Endpoint handler, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await handler(api, request, cancellationToken).ConfigureAwait(false);
        }
        catch (NoDatabaseException ex)
        {
            return Respond.Status(503, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // one failed request answers 500 like FastAPI; the error goes to the log
        catch (Exception ex)
#pragma warning restore CA1031
        {
            api.Logger?.Error($"{Id}: {request.Method} {request.Path} failed", ex);
            return Respond.InternalError();
        }
    }

    /// <summary>Optional website tables: TomYum's account flags (the anti-cheat whitelist) may not exist on every install.</summary>
    private static async Task ProbeSchemaAsync(Api api, IPluginLogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var count = await api.Db.ScalarAsync(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @db AND table_name = 'tomyum_account_flags'",
                new Dictionary<string, object?> { ["@db"] = api.Options.OsuWebDatabase },
                cancellationToken).ConfigureAwait(false);
            api.Sql.HasAccountFlags = count > 0;
        }
#pragma warning disable CA1031 // the database may be down at startup; the default (flags present) stays
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.Warn($"{Id}: couldn't look at the website's tables yet ({ex.Message}); assuming TomYum's account flags are there.");
        }
    }
}
