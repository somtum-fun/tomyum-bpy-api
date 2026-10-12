// Rankings: /get_leaderboard, and clans (the website's teams): /get_clan, /get_clans_leaderboard.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.BpyApi;

internal static class Leaderboards
{
    private static readonly string[] LeaderboardKeys =
    [
        "player_id", "name", "country", "tscore", "rscore", "pp", "plays", "playtime", "acc", "max_combo",
        "xh_count", "x_count", "sh_count", "s_count", "a_count", "clan_id", "clan_name", "clan_tag",
    ];

    private static readonly Regex ClanSort = new("^(pp|rscore)$", RegexOptions.CultureInvariant);

    /// <summary>GET /get_leaderboard: the player ranking of a mode, by any of bancho.py's sort keys.</summary>
    public static async Task<PluginHttpResponse> PlayersAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var sort = args.Literal("sort", "pp", "tscore", "rscore", "pp", "acc", "plays", "playtime");
        var mode = args.Int("mode", 0, 0, 11) ?? 0;
        var limit = args.Int("limit", 50, 1, 100) ?? 50;
        var offset = Math.Max(0, args.Int("offset", 0) ?? 0);
        var country = args.Str("country", minLength: 2, maxLength: 2);
        if (args.HasErrors)
            return args.ErrorResponse();
        if (GameModes.IsUnused(mode))
            return Respond.Status(400, "Invalid gamemode.");

        var sql = api.Sql;
        var parameters = new Dictionary<string, object?>();
        string from;
        string columns;
        string order;
        if (GameModes.IsVanilla(mode))
        {
            var column = "s." + Stats.VanillaColumns[sort];
            from = $" FROM {Stats.VanillaTable(sql, mode)} s JOIN {sql.O}.phpbb_users u ON u.user_id = s.user_id AND {Sql.Visible("u")}"
                + $" WHERE {column} > 0{sql.VanillaActive("s")}";
            columns = Stats.VanillaColumnsSql(sql, mode, withRanks: false);
            order = $"{column} DESC, u.user_id";
        }
        else
        {
            var column = Stats.ModeColumns[sort] == "0" ? "0" : "us." + Stats.ModeColumns[sort];
            from = $" FROM {sql.T}.user_stats us JOIN {sql.O}.phpbb_users u ON u.user_id = us.UserId AND {Sql.Visible("u")}"
                + $" WHERE us.Mode = @mode AND {column} > 0{sql.ModeActive("us")}";
            parameters["@mode"] = mode;
            columns = Stats.ModeColumnsSql(sql, withRanks: false);
            order = column == "0" ? "u.user_id" : $"{column} DESC, u.user_id";
        }

        if (country is not null)
        {
            from += " AND u.country_acronym = @country";
            parameters["@country"] = country.ToUpperInvariant();
        }

        var total = await api.Db.ScalarAsync("SELECT COUNT(*)" + from, parameters, cancellationToken).ConfigureAwait(false);
        var pageParameters = new Dictionary<string, object?>(parameters) { ["@offset"] = offset, ["@limit"] = limit };
        var rows = await api.Db.QueryAsync(
            $"SELECT * FROM (SELECT CAST(u.user_id AS SIGNED) AS player_id, u.username AS name, {Sql.Country("u")} AS country, {columns},"
            + $" CAST((SELECT cm.team_id FROM {sql.O}.team_members cm WHERE cm.user_id = u.user_id) AS SIGNED) AS clan_id"
            + from
            + $" ORDER BY {order} LIMIT @offset, @limit) p"
            + $" LEFT JOIN (SELECT id AS team_id, name AS clan_name, short_name AS clan_tag FROM {sql.O}.teams) c ON c.team_id = p.clan_id",
            pageParameters,
            cancellationToken).ConfigureAwait(false);

        // The inner ORDER BY doesn't survive the join: order the page again the same way.
        var ordered = rows.OrderByDescending(r => SortValue(r, sort)).ThenBy(r => r.Long("player_id"));
        return Respond.Success(new JObj
        {
            { "total", total },
            { "leaderboard", ordered.Select(r => r.Pick(LeaderboardKeys)).ToList() },
        });
    }

    private static double SortValue(Row row, string sort) => sort switch
    {
        "acc" => row.Double("acc"),
        "pp" => row.Double("pp"),
        _ => row.Long(sort),
    };

    /// <summary>The pp and ranked score a player has over every mode bancho.py keeps (alias u = the player).</summary>
    private static string TotalOverModes(Sql sql, string vanillaColumn, string modeColumn)
    {
        var parts = Enumerable.Range(0, 4)
            .Select(m => $"COALESCE((SELECT ROUND(x.{vanillaColumn}) FROM {Stats.VanillaTable(sql, m)} x WHERE x.user_id = u.user_id), 0)")
            .Append($"COALESCE((SELECT SUM(ROUND(x.{modeColumn})) FROM {sql.T}.user_stats x WHERE x.UserId = u.user_id AND x.Mode >= 4), 0)");
        return "CAST(" + string.Join(" + ", parts) + " AS SIGNED)";
    }

    /// <summary>GET /get_clan: a team, its members, and their pp and ranked score over every mode.</summary>
    public static async Task<PluginHttpResponse> ClanAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var clanId = args.RequiredInt("id", 1, int.MaxValue);
        if (args.HasErrors)
            return args.ErrorResponse();

        var sql = api.Sql;
        var clan = await api.Db.FirstAsync(
            $"SELECT CAST(t.id AS SIGNED) AS id, t.name AS name, t.short_name AS tag, CAST(t.leader_id AS SIGNED) AS leader_id FROM {sql.O}.teams t WHERE t.id = @id",
            new Dictionary<string, object?> { ["@id"] = clanId },
            cancellationToken).ConfigureAwait(false);
        if (clan is null)
            return Respond.Status(404, "Clan not found.");

        var members = await api.Db.QueryAsync(
            $"SELECT CAST(u.user_id AS SIGNED) AS id, u.username AS name, {Sql.Country("u")} AS country,"
            + $" {TotalOverModes(sql, "rank_score", "Pp")} AS pp, {TotalOverModes(sql, "ranked_score", "RankedScore")} AS rscore"
            + $" FROM {sql.O}.team_members m JOIN {sql.O}.phpbb_users u ON u.user_id = m.user_id AND {Sql.Visible("u")}"
            + " WHERE m.team_id = @id ORDER BY m.created_at, u.user_id",
            new Dictionary<string, object?> { ["@id"] = clanId },
            cancellationToken).ConfigureAwait(false);

        var leaderId = clan.Long("leader_id");
        var owner = members.FirstOrDefault(m => m.Long("id") == leaderId);
        return Respond.Json(new JObj
        {
            { "id", clan.Long("id") },
            { "name", clan.Text("name") },
            { "tag", clan.Text("tag") },
            { "total_pp", members.Sum(m => m.Long("pp")) },
            { "total_rscore", members.Sum(m => m.Long("rscore")) },
            {
                "members", members.Select(m => (object?)new JObj
                {
                    { "id", m.Long("id") },
                    { "name", m.Text("name") },
                    { "country", m.Text("country") },
                    { "rank", m.Long("id") == leaderId ? "Owner" : "Member" },
                }).ToList()
            },
            {
                "owner", owner is null ? null : new JObj
                {
                    { "id", owner.Long("id") },
                    { "name", owner.Text("name") },
                    { "country", owner.Text("country") },
                    { "rank", "Owner" },
                }
            },
        });
    }

    /// <summary>GET /get_clans_leaderboard: teams by their members' summed pp or ranked score in one mode.</summary>
    public static async Task<PluginHttpResponse> ClansAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var sortBy = args.Str("sort_by", "pp", pattern: ClanSort) ?? "pp";
        var mode = args.Int("mode", 0, 0, 11) ?? 0;
        var limit = args.Int("limit", 50, 1, 100) ?? 50;
        if (args.HasErrors)
            return args.ErrorResponse();

        var sql = api.Sql;
        var parameters = new Dictionary<string, object?> { ["@mode"] = mode, ["@limit"] = limit };
        var (statsJoin, pp, rscore) = GameModes.IsVanilla(mode)
            ? ($" LEFT JOIN {Stats.VanillaTable(sql, mode)} s ON s.user_id = u.user_id", "s.rank_score", "s.ranked_score")
            : ($" LEFT JOIN {sql.T}.user_stats s ON s.UserId = u.user_id AND s.Mode = @mode", "s.Pp", "s.RankedScore");
        var rows = await api.Db.QueryAsync(
            $"SELECT CAST(t.id AS SIGNED) AS clan_id, t.name AS name, t.short_name AS tag,"
            + $" CAST(COALESCE(SUM(ROUND({pp})), 0) AS SIGNED) AS pp, CAST(COALESCE(SUM({rscore}), 0) AS SIGNED) AS rscore,"
            + " CAST(COUNT(u.user_id) AS SIGNED) AS member_count"
            + $" FROM {sql.O}.teams t LEFT JOIN {sql.O}.team_members m ON m.team_id = t.id"
            + $" LEFT JOIN {sql.O}.phpbb_users u ON u.user_id = m.user_id AND {Sql.Visible("u")}"
            + statsJoin
            + $" GROUP BY t.id, t.name, t.short_name ORDER BY {(sortBy == "rscore" ? "rscore" : "pp")} DESC, t.id LIMIT @limit",
            parameters,
            cancellationToken).ConfigureAwait(false);

        var clans = new List<object?>();
        var rank = 0;
        foreach (var row in rows)
        {
            var clan = row.Pick("clan_id", "name", "tag", "pp", "rscore", "member_count");
            clan.Add("global_rank", ++rank);
            clans.Add(clan);
        }

        return Respond.Success(new JObj { { "clans", clans } });
    }
}
