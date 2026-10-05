// Player stats in bancho.py's `stats` shape: vanilla from the website's osu_user_stats* tables,
// Relax/Autopilot from TomYum's user_stats. Ranks count visible players with more pp, like the
// website's and TomYum's rankings.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.BpyApi;

internal static class Stats
{
    private static readonly string[] Tables = ["osu_user_stats", "osu_user_stats_taiko", "osu_user_stats_fruits", "osu_user_stats_mania"];

    /// <summary>The website's stats table for a vanilla mode.</summary>
    public static string VanillaTable(Sql sql, long mode) => $"{sql.O}.{Tables[GameModes.Vanilla(mode)]}";

    /// <summary>bancho.py's leaderboard sort keys and the website's columns for them.</summary>
    public static readonly IReadOnlyDictionary<string, string> VanillaColumns = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tscore"] = "total_score",
        ["rscore"] = "ranked_score",
        ["pp"] = "rank_score",
        ["acc"] = "accuracy_new",
        ["plays"] = "playcount",
        ["playtime"] = "total_seconds_played",
    };

    /// <summary>The same keys in TomYum's user_stats (it keeps no play time).</summary>
    public static readonly IReadOnlyDictionary<string, string> ModeColumns = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tscore"] = "TotalScore",
        ["rscore"] = "RankedScore",
        ["pp"] = "Pp",
        ["acc"] = "Accuracy",
        ["plays"] = "PlayCount",
        ["playtime"] = "0",
    };

    private static readonly string[] Keys =
    [
        "id", "mode", "tscore", "rscore", "pp", "plays", "playtime", "acc", "max_combo", "total_hits", "replay_views",
        "xh_count", "x_count", "sh_count", "s_count", "a_count", "rank", "country_rank",
    ];

    /// <summary>The columns of one vanilla mode's stats row (alias s = stats, u = the player).</summary>
    public static string VanillaColumnsSql(Sql sql, long mode, bool withRanks)
    {
        var table = VanillaTable(sql, mode);
        var columns =
            $"CAST({GameModes.Vanilla(mode).ToString(CultureInfo.InvariantCulture)} AS SIGNED) AS mode,"
            + " CAST(s.total_score AS SIGNED) AS tscore, CAST(s.ranked_score AS SIGNED) AS rscore,"
            + " CAST(ROUND(s.rank_score) AS SIGNED) AS pp, CAST(s.playcount AS SIGNED) AS plays,"
            + " CAST(s.total_seconds_played AS SIGNED) AS playtime, ROUND(s.accuracy_new, 3) AS acc,"
            + " CAST(s.max_combo AS SIGNED) AS max_combo, CAST(s.count300 + s.count100 + s.count50 AS SIGNED) AS total_hits,"
            + " CAST(s.replay_popularity AS SIGNED) AS replay_views,"
            + " CAST(COALESCE(s.xh_rank_count, 0) AS SIGNED) AS xh_count, CAST(s.x_rank_count AS SIGNED) AS x_count,"
            + " CAST(COALESCE(s.sh_rank_count, 0) AS SIGNED) AS sh_count, CAST(s.s_rank_count AS SIGNED) AS s_count,"
            + " CAST(s.a_rank_count AS SIGNED) AS a_count";
        if (withRanks)
        {
            var above = $"SELECT COUNT(*) FROM {table} ro JOIN {sql.O}.phpbb_users ru ON ru.user_id = ro.user_id"
                + $" WHERE {Sql.Visible("ru")} AND ro.rank_score > s.rank_score";
            columns += $", CAST(IF(s.rank_score > 0, 1 + ({above}), 0) AS SIGNED) AS `rank`,"
                + $" CAST(IF(s.rank_score > 0, 1 + ({above} AND ru.country_acronym = u.country_acronym), 0) AS SIGNED) AS country_rank";
        }

        return columns;
    }

    /// <summary>The columns of a Relax/Autopilot stats row (alias us = TomYum's user_stats, u = the player).</summary>
    public static string ModeColumnsSql(Sql sql, bool withRanks)
    {
        string Count(string grades) =>
            $"(SELECT COUNT(*) FROM {sql.T}.scores g WHERE g.UserId = us.UserId AND g.Mode = us.Mode AND g.Status = 2 AND g.Grade IN ({grades}))";

        var columns =
            "CAST(us.Mode AS SIGNED) AS mode, CAST(us.TotalScore AS SIGNED) AS tscore, CAST(us.RankedScore AS SIGNED) AS rscore,"
            + " CAST(ROUND(us.Pp) AS SIGNED) AS pp, CAST(us.PlayCount AS SIGNED) AS plays, CAST(0 AS SIGNED) AS playtime,"
            + " ROUND(us.Accuracy * 100, 3) AS acc,"
            + $" CAST((SELECT COALESCE(MAX(c.MaxCombo), 0) FROM {sql.T}.scores c WHERE c.UserId = us.UserId AND c.Mode = us.Mode) AS SIGNED) AS max_combo,"
            + $" CAST((SELECT COALESCE(SUM(c.Count300 + c.Count100 + c.Count50), 0) FROM {sql.T}.scores c WHERE c.UserId = us.UserId AND c.Mode = us.Mode) AS SIGNED) AS total_hits,"
            + " CAST(0 AS SIGNED) AS replay_views,"
            + $" {Count("'XH'")} AS xh_count, {Count("'X'")} AS x_count, {Count("'SH'")} AS sh_count, {Count("'S'")} AS s_count, {Count("'A'")} AS a_count";
        if (withRanks)
        {
            var above = $"SELECT COUNT(*) FROM {sql.T}.user_stats ro JOIN {sql.O}.phpbb_users ru ON ru.user_id = ro.UserId"
                + $" WHERE ro.Mode = us.Mode AND {Sql.Visible("ru")} AND ro.Pp > us.Pp";
            columns += $", CAST(IF(us.Pp > 0, 1 + ({above}), 0) AS SIGNED) AS `rank`,"
                + $" CAST(IF(us.Pp > 0, 1 + ({above} AND ru.country_acronym = u.country_acronym), 0) AS SIGNED) AS country_rank";
        }

        return columns;
    }

    /// <summary>A player's stats in every mode bancho.py keeps (zeros where they haven't played).</summary>
    public static async Task<List<JObj>> ForPlayerAsync(Api api, long userId, CancellationToken cancellationToken)
    {
        var sql = api.Sql;
        var parts = new List<string>();
        for (var mode = 0; mode < 4; mode++)
        {
            parts.Add($"SELECT {VanillaColumnsSql(sql, mode, withRanks: true)}"
                + $" FROM {VanillaTable(sql, mode)} s JOIN {sql.O}.phpbb_users u ON u.user_id = s.user_id WHERE s.user_id = @id");
        }

        parts.Add($"SELECT {ModeColumnsSql(sql, withRanks: true)}"
            + $" FROM {sql.T}.user_stats us JOIN {sql.O}.phpbb_users u ON u.user_id = us.UserId WHERE us.UserId = @id AND us.Mode >= 4");

        var rows = await api.Db.QueryAsync(
            string.Join(" UNION ALL ", parts),
            new Dictionary<string, object?> { ["@id"] = userId },
            cancellationToken).ConfigureAwait(false);

        var byMode = rows.GroupBy(r => r.Long("mode")).ToDictionary(g => g.Key, g => g.First());
        var result = new List<JObj>();
        foreach (var mode in GameModes.Stats)
        {
            var stat = new JObj();
            byMode.TryGetValue(mode, out var row);
            foreach (var key in Keys)
            {
                stat.Add(key, key switch
                {
                    "id" => (object)userId,
                    "mode" => (long)mode,
                    "acc" => row?.Double("acc") ?? 0.0,
                    _ => row?.Long(key) ?? 0L,
                });
            }

            result.Add(stat);
        }

        return result;
    }
}

/// <summary>Daily rank history: the website's ring of 90 days for vanilla, TomYum's table for Relax/Autopilot.</summary>
internal static class RankHistory
{
    private static readonly string[] Rulesets = ["osu", "taiko", "fruits", "mania"];

    /// <summary>All 90 day columns of osu_user_performance_rank.</summary>
    private static string DayColumns(string alias) =>
        string.Join(", ", Enumerable.Range(0, 90).Select(i => $"{alias}.r{i.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>
    /// The day each column of the ring holds: the column written last (<c>pp_rank_column_&lt;ruleset&gt;</c>)
    /// is the last day TomYum recorded (<c>tomyum_rank_history_day</c>, else today), each one before it a day earlier.
    /// </summary>
    internal static DateTime DayOf(int column, int lastColumn, DateTime lastDay) =>
        lastDay.Date.AddDays(-(((lastColumn - column) % 90) + 90) % 90);

    private static async Task<(int LastColumn, DateTime LastDay)?> RingAsync(Api api, int ruleset, CancellationToken cancellationToken)
    {
        var rows = await api.Db.QueryAsync(
            $"SELECT name, CAST(`count` AS SIGNED) AS value FROM {api.Sql.O}.osu_counts WHERE name IN (@column, 'tomyum_rank_history_day')",
            new Dictionary<string, object?> { ["@column"] = "pp_rank_column_" + Rulesets[ruleset] },
            cancellationToken).ConfigureAwait(false);
        var column = rows.FirstOrDefault(r => r.Text("name").StartsWith("pp_rank_column_", StringComparison.Ordinal));
        if (column is null)
            return null;

        var lastDay = DateTime.UtcNow.Date;
        var day = rows.FirstOrDefault(r => r.Text("name") == "tomyum_rank_history_day");
        if (day is not null && DateTime.TryParseExact(
                day.Long("value").ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            lastDay = parsed.Date;
        }

        return ((int)column.Long("value"), DateTime.SpecifyKind(lastDay, DateTimeKind.Utc));
    }

    /// <summary>A player's daily ranks in one mode, newest first, as bancho.py's ranking_history entries.</summary>
    public static async Task<List<object?>> ForPlayerAsync(Api api, long userId, int mode, CancellationToken cancellationToken)
    {
        var entries = new List<(DateTime Day, long Rank)>();
        if (GameModes.IsVanilla(mode))
        {
            var ring = await RingAsync(api, mode, cancellationToken).ConfigureAwait(false);
            var row = ring is null ? null : await api.Db.FirstAsync(
                $"SELECT {DayColumns("h")} FROM {api.Sql.O}.osu_user_performance_rank h WHERE h.user_id = @id AND h.mode = @mode",
                new Dictionary<string, object?> { ["@id"] = userId, ["@mode"] = mode },
                cancellationToken).ConfigureAwait(false);
            if (ring is { } r && row is not null)
            {
                for (var i = 0; i < 90; i++)
                {
                    var rank = row.Long("r" + i.ToString(CultureInfo.InvariantCulture));
                    if (rank > 0)
                        entries.Add((DayOf(i, r.LastColumn, r.LastDay), rank));
                }
            }
        }
        else
        {
            var rows = await api.Db.QueryAsync(
                $"SELECT h.Day AS day, CAST(h.Rank AS SIGNED) AS `rank` FROM {api.Sql.T}.mode_rank_history h WHERE h.UserId = @id AND h.Mode = @mode",
                new Dictionary<string, object?> { ["@id"] = userId, ["@mode"] = mode },
                cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (row.Time("day") is { } day && row.Long("rank") > 0)
                    entries.Add((day, row.Long("rank")));
            }
        }

        // TomYum and the website keep no daily country rank.
        return entries.OrderByDescending(e => e.Day)
            .Select(e => (object?)new JObj { { "timestamp", e.Day }, { "global_rank", e.Rank }, { "country_rank", 0L }, { "mode", (long)mode } })
            .ToList();
    }

    /// <summary>GET /get_rank_one_history: every reign at #1, per mode, newest first.</summary>
    public static async Task<PluginHttpResponse> RankOneAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var o = api.Sql.O;
        var history = new JObj();
        foreach (var mode in GameModes.Stats)
        {
            var days = new List<(DateTime Day, long UserId, string Name, string Country)>();
            if (GameModes.IsVanilla(mode))
            {
                if (await RingAsync(api, mode, cancellationToken).ConfigureAwait(false) is not { } ring)
                    continue;
                var anyFirst = string.Join(" OR ", Enumerable.Range(0, 90).Select(i => $"h.r{i.ToString(CultureInfo.InvariantCulture)} = 1"));
                var rows = await api.Db.QueryAsync(
                    $"SELECT CAST(h.user_id AS SIGNED) AS user_id, u.username AS username, {Sql.Country("u")} AS country, {DayColumns("h")}"
                    + $" FROM {o}.osu_user_performance_rank h JOIN {o}.phpbb_users u ON u.user_id = h.user_id"
                    + $" WHERE h.mode = @mode AND {Sql.Visible("u")} AND ({anyFirst})",
                    new Dictionary<string, object?> { ["@mode"] = mode },
                    cancellationToken).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    for (var i = 0; i < 90; i++)
                    {
                        if (row.Long("r" + i.ToString(CultureInfo.InvariantCulture)) == 1)
                            days.Add((RankHistory.DayOf(i, ring.LastColumn, ring.LastDay), row.Long("user_id"), row.Text("username"), row.Text("country")));
                    }
                }
            }
            else
            {
                var rows = await api.Db.QueryAsync(
                    $"SELECT h.Day AS day, CAST(h.UserId AS SIGNED) AS user_id, u.username AS username, {Sql.Country("u")} AS country"
                    + $" FROM {api.Sql.T}.mode_rank_history h JOIN {o}.phpbb_users u ON u.user_id = h.UserId"
                    + $" WHERE h.Mode = @mode AND h.Rank = 1 AND {Sql.Visible("u")}",
                    new Dictionary<string, object?> { ["@mode"] = mode },
                    cancellationToken).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    if (row.Time("day") is { } day)
                        days.Add((day, row.Long("user_id"), row.Text("username"), row.Text("country")));
                }
            }

            // Oldest first: each change of holder starts a reign. Newest first in the answer.
            var reigns = new List<object?>();
            long? previous = null;
            foreach (var day in days.OrderBy(d => d.Day).ThenBy(d => d.UserId))
            {
                if (day.UserId == previous)
                    continue;
                reigns.Add(new JObj
                {
                    { "user_id", day.UserId },
                    { "username", day.Name },
                    { "country", day.Country },
                    { "timestamp", JsonText.FormatDateTime(day.Day) },
                    { "mode", (long)mode },
                });
                previous = day.UserId;
            }

            if (reigns.Count > 0)
            {
                reigns.Reverse();
                history.Add(mode.ToString(CultureInfo.InvariantCulture), reigns);
            }
        }

        return Respond.Success(new JObj { { "history", history } });
    }
}
