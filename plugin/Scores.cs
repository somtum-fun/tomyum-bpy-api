// Score endpoints: /get_player_scores, /get_map_scores, /get_score_info, /get_replay,
// /get_recent_scores, /get_top_osu_std_scores, /get_top_play, /get_total_scores.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.BpyApi;

internal static class Scores
{
    private static readonly string[] PlayerScoreKeys =
    [
        "id", "score", "pp", "acc", "max_combo", "mods", "n300", "n100", "n50", "nmiss", "ngeki", "nkatu", "grade",
        "status", "mode", "play_time", "time_elapsed", "perfect",
    ];

    // bancho.py's "first" query selects time_elapsed before play_time.
    private static readonly string[] FirstScoreKeys =
    [
        "id", "score", "pp", "acc", "max_combo", "mods", "n300", "n100", "n50", "nmiss", "ngeki", "nkatu", "grade",
        "status", "mode", "time_elapsed", "play_time", "perfect",
    ];

    private static readonly string[] MapScoreKeys =
    [
        "map_md5", "id", "score", "pp", "acc", "max_combo", "mods", "n300", "n100", "n50", "nmiss", "ngeki", "nkatu",
        "grade", "status", "mode", "play_time", "time_elapsed", "userid", "perfect", "player_name", "country",
        "clan_id", "clan_name", "clan_tag",
    ];

    private static readonly string[] ScoreInfoKeys =
    [
        "id", "map_md5", "score", "pp", "acc", "max_combo", "mods", "n300", "n100", "n50", "nmiss", "ngeki", "nkatu",
        "grade", "status", "mode", "play_time", "time_elapsed", "client_flags", "userid", "perfect", "online_checksum",
    ];

    private static readonly string[] TopPlayKeys =
    [
        "id", "userid", "map_md5", "score", "pp", "acc", "max_combo", "mods", "n300", "n100", "n50", "nmiss", "ngeki",
        "nkatu", "grade", "mode", "play_time", "time_elapsed", "perfect", "map_id", "map_status", "artist", "title",
        "version", "player_name", "player_privileges", "country", "global_rank", "country_rank",
    ];

    /// <summary>Scores of one source (alias sc) by visible players (alias u), filtered and ordered.</summary>
    private static string Select(Api api, long mode, string where, string orderBy, string limit, string extraColumns = "", string extraJoins = "") =>
        $"SELECT sc.*, u.username AS player_name, {Sql.Country("u")} AS country{extraColumns}"
        + $" FROM ({api.Sql.ScoresFor(mode)}) sc"
        + $" JOIN {api.Sql.O}.phpbb_users u ON u.user_id = sc.userid AND {Sql.Visible("u")}"
        + extraJoins
        + $" WHERE {where} ORDER BY {orderBy} LIMIT {limit}";

    /// <summary>The mods filter as SQL (bancho.py: all of them with "=", any of them with "~").</summary>
    private static string ModsWhere((long Mods, bool Strict)? filter, Dictionary<string, object?> parameters)
    {
        if (filter is not { } f)
            return string.Empty;
        parameters["@mods"] = f.Mods;
        return f.Strict ? " AND (sc.mods & @mods) = @mods" : " AND (sc.mods & @mods) <> 0";
    }

    /// <summary>GET /get_player_scores: a player's recent, best or first-place scores in one mode.</summary>
    public static async Task<PluginHttpResponse> PlayerAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var scope = args.Literal("scope", null, "recent", "best", "first");
        var userId = args.Int("id", null, 1, int.MaxValue);
        var username = args.Str("name", pattern: Players.UsernamePattern);
        var modsArg = args.Str("mods");
        var mode = args.Int("mode", 0, 0, 11) ?? 0;
        var limit = args.Int("limit", 25, 1, 100) ?? 25;
        var includeLoved = args.Bool("include_loved", false);
        var includeFailed = args.Bool("include_failed", true);
        if (args.HasErrors)
            return args.ErrorResponse();
        if (GameModes.IsUnused(mode))
            return Respond.Status(400, "Invalid gamemode.");
        if ((userId is null && username is null) || (userId is not null && username is not null))
            return Respond.Status(400, "Must provide either id OR name!");

        var user = await Players.FindAsync(api, userId, username, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Respond.Status(404, "Player not found.");

        var parameters = new Dictionary<string, object?> { ["@user"] = user.Long("id"), ["@mode"] = mode };
        var where = "sc.userid = @user AND sc.mode = @mode" + ModsWhere(Mods.ParseFilter(modsArg), parameters);
        string orderBy;
        var keys = PlayerScoreKeys;
        switch (scope)
        {
            case "best":
                where += includeLoved ? " AND sc.status = 2 AND sc.map_status IN (2, 3, 5)" : " AND sc.status = 2 AND sc.map_status IN (2, 3)";
                orderBy = "sc.pp DESC, sc.id";
                break;
            case "recent":
                if (!includeFailed)
                    where += " AND sc.status <> 0";
                orderBy = "sc.play_time DESC, sc.id DESC";
                break;
            default:
                where += " AND sc.status = 2 AND sc.map_status IN (2, 3, 5) AND " + NobodyBetter(api, mode);
                orderBy = "sc.play_time DESC, sc.id DESC";
                keys = FirstScoreKeys;
                break;
        }

        parameters["@limit"] = limit;
        var rows = await api.Db.QueryAsync(Select(api, mode, where, orderBy, "@limit"), parameters, cancellationToken).ConfigureAwait(false);
        var maps = await Maps.ManyAsync(api, rows.Select(r => r.Long("beatmap_id")), cancellationToken).ConfigureAwait(false);

        var scores = new List<object?>();
        foreach (var row in rows)
        {
            var score = row.Pick(keys);
            score.Add("beatmap", maps.TryGetValue(row.Long("beatmap_id"), out var map) ? map : null);
            score.Add("mods_readable", Mods.Readable(row.Long("mods")));
            scores.Add(score);
        }

        return Respond.Success(new JObj
        {
            { "scores", scores },
            { "player", new JObj { { "id", user.Long("id") }, { "name", user.Text("name") } } },
        });
    }

    /// <summary>
    /// For a "first" place: no other visible player has a better play on the map in the mode (by score
    /// in vanilla, by pp in Relax/Autopilot, the way each leaderboard is ordered).
    /// </summary>
    private static string NobodyBetter(Api api, long mode)
    {
        var sql = api.Sql;
        return GameModes.IsVanilla(mode)
            ? $"NOT EXISTS (SELECT 1 FROM {sql.O}.scores x JOIN {sql.O}.phpbb_users xu ON xu.user_id = x.user_id"
              + " WHERE x.beatmap_id = sc.beatmap_id AND x.ruleset_id = sc.mode AND x.user_id <> sc.userid AND x.passed = 1"
              + $" AND {Sql.Visible("xu")} AND NOT {Sql.RelaxPlay("x")} AND x.total_score > sc.sort_score)"
            : $"NOT EXISTS (SELECT 1 FROM {sql.T}.scores x JOIN {sql.O}.phpbb_users xu ON xu.user_id = x.UserId"
              + " WHERE x.BeatmapMd5 = sc.map_md5 AND x.Mode = sc.mode AND x.Status = 2 AND x.UserId <> sc.userid"
              + $" AND {Sql.Visible("xu")} AND IF(x.PpHeld, 0, x.Pp) > sc.pp)";
    }

    /// <summary>GET /get_map_scores: a map's leaderboard (each player's best) or its newest bests.</summary>
    public static async Task<PluginHttpResponse> MapAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var scope = args.Literal("scope", null, "recent", "best");
        var mapId = args.Int("id", null, 0, int.MaxValue);
        var md5 = args.Str("md5", minLength: 32, maxLength: 32);
        var modsArg = args.Str("mods");
        var mode = args.Int("mode", 0, 0, 11) ?? 0;
        var limit = args.Int("limit", 50, 1, 100) ?? 50;
        if (args.HasErrors)
            return args.ErrorResponse();
        if (GameModes.IsUnused(mode))
            return Respond.Status(400, "Invalid gamemode.");
        if (mapId is null && md5 is null)
            return Respond.Status(400, "Must provide either id or md5!");

        var map = await Maps.FindRowAsync(api, mapId, mapId is null ? md5 : null, cancellationToken).ConfigureAwait(false);
        if (map is null)
            return Respond.Status(404, "Map not found.");

        var parameters = new Dictionary<string, object?> { ["@mode"] = mode, ["@limit"] = limit };
        string where;
        if (GameModes.IsVanilla(mode))
        {
            parameters["@map"] = map.Long("id");
            where = "sc.beatmap_id = @map";
        }
        else
        {
            parameters["@md5"] = map.Text("md5");
            where = "sc.map_md5 = @md5";
        }

        where += " AND sc.mode = @mode AND sc.status = 2" + ModsWhere(Mods.ParseFilter(modsArg), parameters);
        // Like the leaderboards: vanilla by score, Relax/Autopilot by pp.
        var orderBy = scope == "best"
            ? (GameModes.IsVanilla(mode) ? "sc.sort_score DESC, sc.id" : "sc.pp DESC, sc.id")
            : "sc.play_time DESC, sc.id DESC";
        var rows = await api.Db.QueryAsync(
            Select(
                api, mode, where, orderBy, "@limit",
                ", CAST(c.id AS SIGNED) AS clan_id, c.name AS clan_name, c.short_name AS clan_tag",
                $" LEFT JOIN {api.Sql.O}.team_members cm ON cm.user_id = u.user_id LEFT JOIN {api.Sql.O}.teams c ON c.id = cm.team_id"),
            parameters,
            cancellationToken).ConfigureAwait(false);

        return Respond.Success(new JObj { { "scores", rows.Select(r => r.Pick(MapScoreKeys)).ToList() } });
    }

    /// <summary>
    /// Finds a score by the id this API gives it: a website score (vanilla; a lazer Relax/Autopilot
    /// play's website id finds TomYum's row for it), or TomYum's Relax/Autopilot id plus the offset.
    /// </summary>
    private static async Task<Row?> FindAsync(Api api, long id, CancellationToken cancellationToken, string extraColumns = "", string extraJoins = "")
    {
        var sql = api.Sql;
        var parameters = new Dictionary<string, object?>();
        long mode;
        string where;
        if (id >= sql.ModeScoreIdOffset)
        {
            mode = 4;
            parameters["@id"] = id - sql.ModeScoreIdOffset;
            where = "sc.raw_id = @id";
        }
        else
        {
            var relax = await api.Db.FirstAsync(
                $"SELECT t.Id AS id FROM {sql.T}.scores t WHERE t.OsuWebScoreId = @id AND t.Mode >= 4 LIMIT 1",
                new Dictionary<string, object?> { ["@id"] = id },
                cancellationToken).ConfigureAwait(false);
            mode = relax is null ? 0 : 4;
            parameters["@id"] = relax?.Long("id") ?? id;
            where = "sc.raw_id = @id";
        }

        var rows = await api.Db.QueryAsync(Select(api, mode, where, "sc.id", "1", extraColumns, extraJoins), parameters, cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>GET /get_score_info.</summary>
    public static async Task<PluginHttpResponse> InfoAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var id = args.RequiredInt("id", 0, long.MaxValue);
        if (args.HasErrors)
            return args.ErrorResponse();

        var row = await FindAsync(api, id, cancellationToken, ", CAST(0 AS SIGNED) AS client_flags, '' AS online_checksum").ConfigureAwait(false);
        if (row is null)
            return Respond.Status(404, "Score not found.");
        return Respond.Success(new JObj { { "score", row.Pick(ScoreInfoKeys) } });
    }

    /// <summary>
    /// GET /get_replay: the .osr with bancho.py's header (or, with include_headers=false, the frames
    /// only). Stable replays are TomYum's frame files; lazer ones the spectator server's complete .osr,
    /// served as saved.
    /// </summary>
    public static async Task<PluginHttpResponse> ReplayAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var id = args.RequiredInt("id", 0, long.MaxValue);
        var includeHeaders = args.Bool("include_headers", true);
        if (args.HasErrors)
            return args.ErrorResponse();

        var row = await FindAsync(
            api, id, cancellationToken,
            ", bs.artist AS artist, bs.title AS title, b.version AS version",
            $" JOIN {api.Sql.O}.osu_beatmaps b ON b.beatmap_id = sc.beatmap_id JOIN {api.Sql.O}.osu_beatmapsets bs ON bs.beatmapset_id = b.beatmapset_id")
            .ConfigureAwait(false);
        var path = row is null ? null : api.ReplayPath(row.Text("replay_source"), row.Long("replay_key"));
        if (row is null || path is null || !File.Exists(path))
            return Respond.Status(404, "Replay not found.");

        var file = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var complete = row.Text("replay_source") == "lazer";
        var fileHeaders = new Dictionary<string, string> { ["Content-Description"] = "File Transfer" };
        if (!includeHeaders)
        {
            var frames = complete ? Osr.Frames(file) : file;
            if (frames is null)
                return Respond.Status(404, "Replay not found.");
            return Respond.Bytes(frames, "application/octet-stream", fileHeaders);
        }

        var playedAt = row.Time("play_time") ?? DateTime.UtcNow;
        var username = row.Text("player_name");
        var body = complete
            ? file
            : Osr.Build(
                new ReplayHeader(
                    GameModes.Vanilla(row.Long("mode")), row.Text("map_md5"), username,
                    row.Int("n300"), row.Int("n100"), row.Int("n50"), row.Int("ngeki"), row.Int("nkatu"), row.Int("nmiss"),
                    row.Int("score"), row.Int("max_combo"), row.Bool("perfect"), row.Int("mods"), playedAt, id),
                file);
        var name = Osr.FileName(username, row.Text("artist"), row.Text("title"), row.Text("version"), playedAt);
        fileHeaders["Content-Disposition"] = "attachment; filename*=UTF-8''" + Osr.Quote(name);
        return Respond.Bytes(body, "application/octet-stream", fileHeaders);
    }

    /// <summary>GET /get_recent_scores: the newest scores on the server, every mode.</summary>
    public static async Task<PluginHttpResponse> RecentAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var offset = Math.Max(0, args.Int("offset", 0) ?? 0);
        var limit = args.Int("limit", 25, 1, 100) ?? 25;
        if (args.HasErrors)
            return args.ErrorResponse();

        var sql = api.Sql;
        var take = (offset + limit).ToString(CultureInfo.InvariantCulture);
        const string Columns = "sc.id, sc.map_md5, sc.score, sc.pp, sc.acc, sc.max_combo, sc.mods, sc.play_time, sc.userid, u.username AS player_name";
        // Each branch newest first by its table's id (its primary key), then both by time.
        string Branch(string source) =>
            $"(SELECT {Columns} FROM ({source}) sc JOIN {sql.O}.phpbb_users u ON u.user_id = sc.userid AND {Sql.Visible("u")}"
            + $" ORDER BY sc.raw_id DESC LIMIT {take})";
        var rows = await api.Db.QueryAsync(
            Branch(sql.VanillaScores) + " UNION ALL " + Branch(sql.ModeScores)
            + " ORDER BY play_time DESC, id DESC LIMIT @offset, @limit",
            new Dictionary<string, object?> { ["@offset"] = offset, ["@limit"] = limit },
            cancellationToken).ConfigureAwait(false);

        return Respond.Success(new JObj
        {
            { "recent_scores", rows.Select(r => r.Pick("id", "map_md5", "score", "pp", "acc", "max_combo", "mods", "play_time", "userid", "player_name")).ToList() },
        });
    }

    /// <summary>GET /get_top_osu_std_scores: the best osu! plays in Vanilla, Relax and Autopilot together, by pp.</summary>
    public static async Task<PluginHttpResponse> TopStdAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var offset = Math.Max(0, args.Int("offset", 0) ?? 0);
        var limit = args.Int("limit", 25, 1, 100) ?? 25;
        if (args.HasErrors)
            return args.ErrorResponse();

        var sql = api.Sql;
        var take = (offset + limit).ToString(CultureInfo.InvariantCulture);
        string Branch(string source, string modes) =>
            $"(SELECT sc.pp, sc.userid, u.username AS player_name, sc.id FROM ({source}) sc"
            + $" JOIN {sql.O}.phpbb_users u ON u.user_id = sc.userid AND {Sql.Visible("u")}"
            + $" WHERE sc.mode IN ({modes}) AND sc.status = 2 ORDER BY sc.pp DESC LIMIT {take})";
        var rows = await api.Db.QueryAsync(
            Branch(sql.VanillaScores, "0") + " UNION ALL " + Branch(sql.ModeScores, "4, 8")
            + " ORDER BY pp DESC, id LIMIT @offset, @limit",
            new Dictionary<string, object?> { ["@offset"] = offset, ["@limit"] = limit },
            cancellationToken).ConfigureAwait(false);

        return Respond.Success(new JObj
        {
            {
                "top_osu_std_scores", rows.Select(r => (object?)new JObj
                {
                    { "pp", r.Json("pp") },
                    { "player", new JObj { { "id", r.Long("userid") }, { "name", r.Text("player_name") } } },
                }).ToList()
            },
        });
    }

    /// <summary>GET /get_top_play: a mode's best plays on ranked and approved maps, with global and country places.</summary>
    public static async Task<PluginHttpResponse> TopPlayAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var mode = args.RequiredInt("mode", 0, 11);
        var limit = args.Int("limit", 10, 1, 100) ?? 10;
        var offset = args.Int("offset", 0, 0) ?? 0;
        var country = args.Str("country", minLength: 2, maxLength: 2);
        if (args.HasErrors)
            return args.ErrorResponse();
        if (GameModes.IsUnused(mode))
            return Respond.Status(400, "Invalid gamemode.");

        var sql = api.Sql;
        var parameters = new Dictionary<string, object?> { ["@mode"] = mode };
        var where = "sc.mode = @mode AND sc.status = 2 AND sc.map_status IN (2, 3)";
        if (!string.IsNullOrEmpty(country))
        {
            where += " AND u.country_acronym = @country";
            parameters["@country"] = country.ToUpperInvariant();
        }

        var from = $" FROM ({sql.ScoresFor(mode)}) sc"
            + $" JOIN {sql.O}.phpbb_users u ON u.user_id = sc.userid AND {Sql.Visible("u")}"
            + $" JOIN {sql.O}.osu_beatmaps b2 ON b2.beatmap_id = sc.beatmap_id"
            + $" JOIN {sql.O}.osu_beatmapsets bs2 ON bs2.beatmapset_id = b2.beatmapset_id"
            + $" WHERE {where}";
        var total = await api.Db.ScalarAsync("SELECT COUNT(*)" + from, parameters, cancellationToken).ConfigureAwait(false);

        var pageParameters = new Dictionary<string, object?>(parameters) { ["@limit"] = limit, ["@offset"] = offset };
        var rows = await api.Db.QueryAsync(
            "SELECT sc.*, CAST(b2.beatmap_id AS SIGNED) AS map_id, bs2.artist AS artist, bs2.title AS title, b2.version AS version,"
            + $" u.username AS player_name, {sql.Priv("u")} AS player_privileges, {Sql.Country("u")} AS country,"
            + " CAST(RANK() OVER (ORDER BY sc.pp DESC) AS SIGNED) AS global_rank,"
            + " CAST(RANK() OVER (PARTITION BY u.country_acronym ORDER BY sc.pp DESC) AS SIGNED) AS country_rank"
            + from
            + " ORDER BY sc.pp DESC, sc.id LIMIT @limit OFFSET @offset",
            pageParameters,
            cancellationToken).ConfigureAwait(false);

        return Respond.Success(new JObj
        {
            { "total", total },
            { "top_plays", rows.Select(r => r.Pick(TopPlayKeys)).ToList() },
        });
    }

    /// <summary>GET /get_total_scores: every score kept: the website's (vanilla, and lazer Relax/Autopilot) and TomYum's stable Relax/Autopilot plays.</summary>
    public static async Task<PluginHttpResponse> TotalAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var total = await api.Db.ScalarAsync(
            $"SELECT (SELECT COUNT(*) FROM {api.Sql.O}.scores) + (SELECT COUNT(*) FROM {api.Sql.T}.scores t WHERE t.Mode >= 4 AND NOT t.FromLazer)",
            null,
            cancellationToken).ConfigureAwait(false);
        return Respond.Success(new JObj { { "total_scores", total } });
    }
}
