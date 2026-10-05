// Endpoints that need bancho.py-only things TomYum doesn't have. They answer the way bancho.py does
// when it has nothing to give (or isn't set up for them), so clients take the usual path.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.BpyApi;

internal static class Unsupported
{
    /// <summary>bancho.py's Discord-bot actions (votes, ranking, restrictions, donations): it answers this when BOT_API_TOKEN isn't set.</summary>
    public static readonly IReadOnlyList<string> BotActions =
    [
        "vote_beatmap", "love_beatmap", "rank_beatmap", "cancel_beatmap", "restrict_player", "unrestrict_player",
        "whitelist_player", "unwhitelist_player", "donation_approve", "donation_reject",
    ];

    public static Task<PluginHttpResponse> BotActionAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Respond.Json(
            new JObj { { "success", false }, { "response", new JObj { { "message", "bot api token is not configured on this server" } } } },
            503));

    /// <summary>GET /calculate_pp: bancho.py's per-user API keys don't exist on TomYum, so every key is unknown.</summary>
    public static Task<PluginHttpResponse> CalculatePpAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Respond.Status(401, "Invalid API key."));

    /// <summary>GET /get_user_private_info: needs a bancho.py frontend session cookie; TomYum has none.</summary>
    public static Task<PluginHttpResponse> PrivateInfoAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        args.RequiredInt("userid");
        return Task.FromResult(args.HasErrors ? args.ErrorResponse() : Respond.Status(401, "Unauthorized"));
    }

    /// <summary>GET /get_match: the host doesn't share its multiplayer rooms with plugins.</summary>
    public static Task<PluginHttpResponse> MatchAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        args.RequiredInt("id", 1, 64);
        return Task.FromResult(args.HasErrors ? args.ErrorResponse() : Respond.Status(404, "Match not found."));
    }

    /// <summary>GET /get_active_matches: the same, so no rooms are listed.</summary>
    public static Task<PluginHttpResponse> ActiveMatchesAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Respond.Success(new JObj { { "count", 0 }, { "matches", new List<object?>() } }));

    /// <summary>GET /get_mappool: TomYum has no tournament pools.</summary>
    public static Task<PluginHttpResponse> MappoolAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        args.RequiredInt("id", 1, int.MaxValue);
        return Task.FromResult(args.HasErrors ? args.ErrorResponse() : Respond.Status(404, "Pool not found."));
    }
}
