// Player endpoints: /search_players, /get_player_count, /get_player_info, /special_players,
// /get_recent_players, /get_player_status, /get_player_achievements, /get_player_most_played,
// /statistics, /get_all_users.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.BpyApi;

internal static class Players
{
    /// <summary>bancho.py's <c>regexes.USERNAME</c>.</summary>
    public static readonly Regex UsernamePattern = new(@"^[\w \[\]-]{2,15}$", RegexOptions.CultureInvariant);

    /// <summary>bancho.py's safe name: lower-case, spaces as underscores.</summary>
    public static string SafeName(string name) => name.ToLowerInvariant().Replace(' ', '_');

    /// <summary>
    /// The visible account with this id or name (website names keep spaces; bancho.py's safe names
    /// turn them into underscores, so both spellings are tried). Null when there's none, or it's
    /// restricted or deleted: TomYum keeps those out of public data.
    /// </summary>
    public static async Task<Row?> FindAsync(Api api, long? id, string? name, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>();
        string where;
        if (id is { } userId)
        {
            parameters["@id"] = userId;
            where = "u.user_id = @id";
        }
        else if (name is not null)
        {
            var lower = name.ToLowerInvariant();
            var candidates = new[] { lower, lower.Replace('_', ' '), lower.Replace(' ', '_') }.Distinct();
            where = $"u.username_clean IN ({Db.InList(parameters, "n", candidates)})";
        }
        else
        {
            return null;
        }

        var sql = $"SELECT u.user_id AS id, u.username AS name, {Sql.Country("u")} AS country,"
            + " CAST(u.user_lastvisit AS SIGNED) AS latest_activity"
            + $" FROM {api.Sql.O}.phpbb_users u WHERE {where} AND {Sql.Visible("u")} ORDER BY u.user_id LIMIT 1";
        return await api.Db.FirstAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GET /search_players: names containing <c>q</c>, by id (visible players and bots).</summary>
    public static async Task<PluginHttpResponse> SearchAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var search = args.Str("q");
        if (args.HasErrors)
            return args.ErrorResponse();

        var parameters = new Dictionary<string, object?> { ["@name"] = search is null ? null : "%" + search + "%" };
        var rows = await api.Db.QueryAsync(
            $"SELECT CAST(u.user_id AS SIGNED) AS id, u.username AS name FROM {api.Sql.O}.phpbb_users u"
            + $" WHERE u.username LIKE COALESCE(@name, u.username) AND {Sql.Visible("u")}"
            + $" ORDER BY u.user_id LIMIT {api.Options.SearchLimit.ToString(CultureInfo.InvariantCulture)}",
            parameters,
            cancellationToken).ConfigureAwait(false);

        return Respond.Success(new JObj
        {
            { "results", rows.Count },
            { "result", rows.Select(r => r.Pick("id", "name")).ToList() },
        });
    }

    /// <summary>GET /get_player_count: players online, accounts, and clans (the website's teams).</summary>
    public static async Task<PluginHttpResponse> CountAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var online = await api.OnlineCountAsync(cancellationToken).ConfigureAwait(false);
        var row = await api.Db.FirstAsync(
            $"SELECT (SELECT COUNT(*) FROM {api.Sql.O}.phpbb_users u WHERE u.user_type <> 1) AS total,"
            + $" (SELECT COUNT(*) FROM {api.Sql.O}.teams) AS clans",
            null,
            cancellationToken).ConfigureAwait(false);

        return Respond.Success(new JObj
        {
            {
                "counts", new JObj
                {
                    { "online", online },
                    { "total", row?.Long("total") ?? 0 },
                    { "clans", row?.Long("clans") ?? 0 },
                }
            },
        });
    }

    /// <summary>GET /get_player_info: a player's info, stats in every mode, and rank history.</summary>
    public static async Task<PluginHttpResponse> InfoAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var scope = args.Literal("scope", null, "stats", "info", "all", "history");
        var userId = args.Int("id", null, 1, int.MaxValue);
        var username = args.Str("name", pattern: UsernamePattern);
        if (args.HasErrors)
            return args.ErrorResponse();

        if ((userId is null && username is null) || (userId is not null && username is not null))
            return Respond.Status(400, "Must provide either id OR name!");

        var user = await FindAsync(api, userId, username, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Respond.Status(404, "Player not found.");

        var id = user.Long("id");
        var data = new JObj();
        if (scope is "info" or "all")
            data.Add("info", await InfoJsonAsync(api, id, cancellationToken).ConfigureAwait(false) ?? new JObj());

        if (scope is "stats" or "all" or "history")
        {
            var stats = await Stats.ForPlayerAsync(api, id, cancellationToken).ConfigureAwait(false);
            if (scope is "stats" or "all")
            {
                var byMode = new JObj();
                foreach (var stat in stats)
                    byMode.Add(Convert.ToString(stat["mode"], CultureInfo.InvariantCulture)!, stat);
                data.Add("stats", byMode);
            }

            if (scope is "history" or "all")
            {
                var history = new List<object?>();
                foreach (var stat in stats)
                {
                    var mode = Convert.ToInt32(stat["mode"], CultureInfo.InvariantCulture);
                    if (Convert.ToInt64(stat["rank"], CultureInfo.InvariantCulture) == 0)
                        continue;
                    history.AddRange(await RankHistory.ForPlayerAsync(api, id, mode, cancellationToken).ConfigureAwait(false));
                }

                data.Add("history", history);
            }
        }

        return Respond.Success(new JObj { { "player", data } });
    }

    /// <summary>The public fields bancho.py's get_player_info lists under "info".</summary>
    private static async Task<JObj?> InfoJsonAsync(Api api, long id, CancellationToken cancellationToken)
    {
        var o = api.Sql.O;
        var row = await api.Db.FirstAsync(
            $"SELECT CAST(u.user_id AS SIGNED) AS id, u.username AS name, {api.Sql.Priv("u")} AS priv, {Sql.Country("u")} AS country,"
            + " CAST(u.user_regdate AS SIGNED) AS creation_time, CAST(u.user_lastvisit AS SIGNED) AS latest_activity,"
            + " u.osu_subscriber AS supporter, u.osu_subscriptionexpiry AS supporter_until,"
            + " CAST(u.osu_playmode AS SIGNED) AS preferred_mode, CAST(u.osu_playstyle AS SIGNED) AS play_style,"
            + " u.user_colour AS colour, u.username_previous AS previous_names,"
            + " u.user_from AS location, u.user_interests AS interests, u.user_occ AS occupation,"
            + " CAST(tm.team_id AS SIGNED) AS clan_id, CAST(tt.leader_id AS SIGNED) AS clan_leader_id,"
            + " up.post_text AS userpage, up.bbcode_uid AS userpage_uid,"
            + $" (SELECT IF(h.period = 0, 0, UNIX_TIMESTAMP(h.`timestamp`) + h.period) FROM {o}.osu_user_banhistory h"
            + " WHERE h.user_id = u.user_id AND h.ban_status IN (1, 2) ORDER BY h.`timestamp` DESC, h.ban_id DESC LIMIT 1) AS silence_end"
            + $" FROM {o}.phpbb_users u"
            + $" LEFT JOIN {o}.team_members tm ON tm.user_id = u.user_id"
            + $" LEFT JOIN {o}.teams tt ON tt.id = tm.team_id"
            + $" LEFT JOIN {o}.phpbb_posts up ON up.post_id = u.userpage_post_id"
            + " WHERE u.user_id = @id",
            new Dictionary<string, object?> { ["@id"] = id },
            cancellationToken).ConfigureAwait(false);
        if (row is null)
            return null;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var silenceEnd = row.Long("silence_end");
        var donorEnd = row.Bool("supporter") && row.Time("supporter_until") is { } until
            ? new DateTimeOffset(until).ToUnixTimeSeconds()
            : 0;
        var clanId = row.LongOrNull("clan_id") ?? 0;
        var colour = row.Str("colour");
        var userpage = row.Str("userpage");
        if (userpage is not null && row.Str("userpage_uid") is { Length: > 0 } uid)
            userpage = userpage.Replace(":" + uid, string.Empty, StringComparison.Ordinal);
        var name = row.Text("name");

        return new JObj
        {
            { "id", row.Long("id") },
            { "name", name },
            { "safe_name", SafeName(name) },
            { "priv", row.Long("priv") },
            { "country", row.Text("country") },
            { "silence_end", silenceEnd > now ? silenceEnd : 0 },
            { "donor_end", donorEnd },
            { "creation_time", row.Long("creation_time") },
            { "latest_activity", row.Long("latest_activity") },
            { "clan_id", clanId },
            { "clan_priv", clanId == 0 ? 0 : row.Long("clan_leader_id") == row.Long("id") ? 3 : 1 },
            { "preferred_mode", row.Long("preferred_mode") },
            { "play_style", row.Long("play_style") },
            { "custom_badge_name", null },
            { "custom_badge_icon", null },
            { "custom_badge_color", null },
            { "userpage_content", string.IsNullOrEmpty(userpage) ? null : userpage },
            { "votes", 0 },
            { "old_name", LastPreviousName(row.Str("previous_names")) },
            { "preferred_format", null },
            { "hide_clans_tags", 0 },
            { "profile_color", string.IsNullOrEmpty(colour) ? null : "#" + colour },
            { "favourite_mode", null },
            { "location", EmptyToNull(row.Str("location")) },
            { "interests", EmptyToNull(row.Str("interests")) },
            { "occupation", EmptyToNull(row.Str("occupation")) },
            { "restricted", false },
        };
    }

    /// <summary>The newest earlier name from the website's <c>username_previous</c> (a JSON list, or a plain name).</summary>
    internal static string? LastPreviousName(string? previous)
    {
        if (string.IsNullOrWhiteSpace(previous))
            return null;

        var text = previous.Trim();
        if (text.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var names = document.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();
                return names.Count > 0 ? names[^1] : null;
            }
            catch (JsonException)
            {
                return text;
            }
        }

        return text;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>GET /special_players: staff and supporters with coarse role labels (the website's groups).</summary>
    public static async Task<PluginHttpResponse> SpecialAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        request.Query.TryGetValue("roles", out var raw);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var allowed = Privileges.PublicRoles.Select(r => r.Label).ToHashSet(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(raw))
        {
            selected.UnionWith(allowed);
        }
        else
        {
            foreach (var part in raw.Split(','))
            {
                var label = part.Trim().ToLowerInvariant();
                if (label.Length > 0)
                    selected.Add(label);
            }

            if (selected.Remove("staff"))
                selected.UnionWith(["developer", "administrator", "moderator", "nat", "nominator"]);
            if (selected.Remove("donors"))
                selected.UnionWith(["premium", "supporter"]);
            if (selected.Contains("all"))
            {
                selected.Clear();
                selected.UnionWith(allowed);
            }

            var invalid = selected.Where(l => !allowed.Contains(l)).OrderBy(l => l, StringComparer.Ordinal).ToList();
            if (invalid.Count > 0)
            {
                return Respond.Json(
                    new JObj
                    {
                        { "status", $"Unknown role(s): {string.Join(", ", invalid)}." },
                        { "allowed", allowed.OrderBy(l => l, StringComparer.Ordinal).Concat(["staff", "donors", "all"]).ToList() },
                    },
                    400);
            }

            if (selected.Count == 0)
                return Respond.Status(400, "At least one role must be requested.");
        }

        long mask = 0;
        foreach (var (label, bit) in Privileges.PublicRoles)
        {
            if (selected.Contains(label))
                mask |= bit;
        }

        var rows = await api.Db.QueryAsync(
            $"SELECT * FROM (SELECT CAST(u.user_id AS SIGNED) AS id, u.username AS name, {api.Sql.Priv("u")} AS priv"
            + $" FROM {api.Sql.O}.phpbb_users u WHERE {Sql.Visible("u")} AND NOT {api.Sql.IsBot("u.user_id")}) p"
            + " WHERE (p.priv & @mask) <> 0 ORDER BY p.priv DESC, p.id ASC LIMIT 500",
            new Dictionary<string, object?> { ["@mask"] = mask },
            cancellationToken).ConfigureAwait(false);

        var players = new List<object?>();
        foreach (var row in rows)
        {
            var labels = Privileges.RoleLabels(row.Long("priv")).Where(selected.Contains).ToList();
            if (labels.Count == 0)
                continue;
            var name = row.Str("name");
            players.Add(new JObj
            {
                { "id", row.Long("id") },
                { "name", string.IsNullOrWhiteSpace(name) ? row.Long("id").ToString(CultureInfo.InvariantCulture) : name },
                { "roles", labels },
            });
        }

        return Respond.Json(
            new JObj { { "status", "success" }, { "players", players } },
            headers: new Dictionary<string, string> { ["Cache-Control"] = "public, max-age=300" });
    }

    /// <summary>GET /get_recent_players: the newest accounts.</summary>
    public static async Task<PluginHttpResponse> RecentAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var offset = Math.Max(0, args.Int("offset", 0) ?? 0);
        var limit = args.Int("limit", 25, 1, 100) ?? 25;
        if (args.HasErrors)
            return args.ErrorResponse();

        var rows = await api.Db.QueryAsync(
            $"SELECT CAST(u.user_id AS SIGNED) AS id, u.username AS name, CAST(u.user_lastvisit AS SIGNED) AS latest_activity"
            + $" FROM {api.Sql.O}.phpbb_users u WHERE {Sql.Visible("u")} AND NOT {api.Sql.IsBot("u.user_id")}"
            + " ORDER BY u.user_id DESC LIMIT @offset, @limit",
            new Dictionary<string, object?> { ["@offset"] = offset, ["@limit"] = limit },
            cancellationToken).ConfigureAwait(false);
        return Respond.Success(new JObj { { "recent_players", rows.Select(r => r.Pick("id", "name", "latest_activity")).ToList() } });
    }

    /// <summary>GET /get_player_status: what an online player is doing, or when they were last seen.</summary>
    public static async Task<PluginHttpResponse> StatusAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var userId = args.Int("id", null, 1, int.MaxValue);
        var username = args.Str("name", pattern: UsernamePattern);
        if (args.HasErrors)
            return args.ErrorResponse();
        if ((userId is null && username is null) || (userId is not null && username is not null))
            return Respond.Status(400, "Must provide either id OR name!");

        var user = await FindAsync(api, userId, username, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Respond.Status(404, "Player not found.");

        var online = api.Players?.FindByUserId(user.Long("id"));
        if (online is null)
        {
            return Respond.Success(new JObj
            {
                { "player_status", new JObj { { "online", false }, { "last_seen", user.Long("latest_activity") } } },
            });
        }

        JObj? beatmap = null;
        if (!string.IsNullOrEmpty(online.BeatmapMd5) || online.BeatmapId > 0)
            beatmap = await Maps.FindAsync(api, online.BeatmapId > 0 && string.IsNullOrEmpty(online.BeatmapMd5) ? online.BeatmapId : null, online.BeatmapMd5, cancellationToken).ConfigureAwait(false);

        return Respond.Success(new JObj
        {
            {
                "player_status", new JObj
                {
                    { "online", true },
                    { "login_time", api.LoginTimes.TryGetValue(online.UserId, out var at) ? at : null },
                    {
                        "status", new JObj
                        {
                            { "action", (int)online.Action },
                            { "info_text", online.ActionInfo },
                            { "mode", online.GameMode },
                            { "mods", (long)online.Mods },
                            { "beatmap", beatmap },
                        }
                    },
                }
            },
        });
    }

    /// <summary>GET /get_player_achievements: the website's medals, unlocked and not, with how rare each is.</summary>
    public static async Task<PluginHttpResponse> AchievementsAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var userId = args.Int("id", null, 1, int.MaxValue);
        var username = args.Str("name", pattern: UsernamePattern);
        if (args.HasErrors)
            return args.ErrorResponse();
        if ((userId is null && username is null) || (userId is not null && username is not null))
            return Respond.Status(400, "Must provide either id OR name!");

        var user = await FindAsync(api, userId, username, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Respond.Status(404, "Player not found.");

        var o = api.Sql.O;
        var id = user.Long("id");
        var rows = await api.Db.QueryAsync(
            "SELECT CAST(a.achievement_id AS SIGNED) AS id, a.slug AS file, a.name AS name, COALESCE(a.description, '') AS `desc`,"
            + " a.`grouping` AS category, a.quest_instructions AS hint,"
            + $" (SELECT COUNT(*) FROM {o}.osu_user_achievements ua JOIN {o}.phpbb_users au ON au.user_id = ua.user_id"
            + $" WHERE ua.achievement_id = a.achievement_id AND {Sql.Visible("au")}) AS unlock_count,"
            + $" EXISTS (SELECT 1 FROM {o}.osu_user_achievements mine WHERE mine.user_id = @id AND mine.achievement_id = a.achievement_id) AS unlocked"
            + $" FROM {o}.osu_achievements a WHERE a.enabled = 1 ORDER BY a.`grouping`, a.ordering, a.achievement_id",
            new Dictionary<string, object?> { ["@id"] = id },
            cancellationToken).ConfigureAwait(false);
        var totalPlayers = await api.Db.ScalarAsync(
            $"SELECT COUNT(*) FROM {o}.phpbb_users u WHERE {Sql.Visible("u")} AND NOT {api.Sql.IsBot("u.user_id")}",
            null,
            cancellationToken).ConfigureAwait(false);

        var unlocked = new List<object?>();
        var locked = new List<object?>();
        foreach (var row in rows)
        {
            var count = row.Long("unlock_count");
            var percentage = totalPlayers > 0 ? count * 100.0 / totalPlayers : 0;
            var achievement = new JObj
            {
                { "id", row.Long("id") },
                { "file", row.Text("file") },
                { "name", row.Text("name") },
                { "desc", row.Text("desc") },
                { "status", "approved" },
                { "category", row.Str("category") },
                { "icon_path", null },
                { "submitted_by", null },
                { "hint", EmptyToNull(row.Str("hint")) },
                { "allowed_beatmap_statuses", null },
                { "unlock_count", count },
                { "unlock_percentage", Math.Round(percentage, 2, MidpointRounding.ToEven) },
                { "rarity", Rarity(count, totalPlayers) },
            };
            (row.Bool("unlocked") ? unlocked : locked).Add(achievement);
        }

        return Respond.Success(new JObj
        {
            { "player", new JObj { { "id", id }, { "name", user.Text("name") } } },
            { "unlocked_achievements", unlocked },
            { "locked_achievements", locked },
        });
    }

    /// <summary>bancho.py's rarity tiers by the share of players who have it.</summary>
    internal static string Rarity(long unlockCount, long totalPlayers)
    {
        if (totalPlayers == 0)
            return "unknown";
        var percentage = unlockCount * 100.0 / totalPlayers;
        return percentage switch
        {
            < 1 => "legendary",
            < 5 => "epic",
            < 15 => "rare",
            < 40 => "uncommon",
            _ => "common",
        };
    }

    /// <summary>GET /get_player_most_played: the maps a player has the most plays on, in one mode.</summary>
    public static async Task<PluginHttpResponse> MostPlayedAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var userId = args.Int("id", null, 1, int.MaxValue);
        var username = args.Str("name", pattern: UsernamePattern);
        var mode = args.Int("mode", 0, 0, 11) ?? 0;
        var limit = args.Int("limit", 25, 1, 100) ?? 25;
        if (args.HasErrors)
            return args.ErrorResponse();
        if (GameModes.IsUnused(mode))
            return Respond.Status(400, "Invalid gamemode.");
        if (userId is null && username is null)
            return Respond.Status(400, "Must provide either id or name.");

        var user = await FindAsync(api, userId, userId is null ? username : null, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Respond.Status(404, "Player not found.");

        var sql = api.Sql;
        var parameters = new Dictionary<string, object?> { ["@user"] = user.Long("id"), ["@mode"] = mode, ["@limit"] = limit };
        var plays = GameModes.IsVanilla(mode)
            ? $"SELECT s.beatmap_id AS beatmap_id, COUNT(*) AS plays FROM {sql.O}.scores s"
              + $" WHERE s.user_id = @user AND s.ruleset_id = @mode AND NOT {Sql.RelaxPlay("s")} GROUP BY s.beatmap_id"
            : $"SELECT t.BeatmapId AS beatmap_id, COUNT(*) AS plays FROM {sql.T}.scores t"
              + " WHERE t.UserId = @user AND t.Mode = @mode GROUP BY t.BeatmapId";
        var rows = await api.Db.QueryAsync(
            $"SELECT b.checksum AS md5, CAST(b.beatmap_id AS SIGNED) AS id, CAST(b.beatmapset_id AS SIGNED) AS set_id,"
            + $" CAST({RankedStatus.Sql("tb", "b")} AS SIGNED) AS status, bs.artist AS artist, bs.title AS title, b.version AS version,"
            + " bs.creator AS creator, CAST(p.plays AS SIGNED) AS plays"
            + $" FROM ({plays}) p JOIN {sql.BeatmapFrom} ON b.beatmap_id = p.beatmap_id"
            + " ORDER BY p.plays DESC, b.beatmap_id LIMIT @limit",
            parameters,
            cancellationToken).ConfigureAwait(false);

        return Respond.Success(new JObj
        {
            { "maps", rows.Select(r => r.Pick("md5", "id", "set_id", "status", "artist", "title", "version", "creator", "plays")).ToList() },
        });
    }

    /// <summary>GET /statistics: players online, accounts, restricted accounts and the newest account.</summary>
    public static async Task<PluginHttpResponse> StatisticsAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var o = api.Sql.O;
        var online = await api.OnlineCountAsync(cancellationToken).ConfigureAwait(false);
        var counts = await api.Db.FirstAsync(
            $"SELECT (SELECT COUNT(*) FROM {o}.phpbb_users u WHERE u.user_type <> 1) AS total,"
            + $" (SELECT COUNT(*) FROM {o}.phpbb_users u WHERE u.user_type <> 1 AND u.user_warnings <> 0) AS restricted",
            null,
            cancellationToken).ConfigureAwait(false);
        var newest = await api.Db.FirstAsync(
            $"SELECT CAST(u.user_id AS SIGNED) AS id, u.username AS name, CAST(u.user_regdate AS SIGNED) AS creation_time"
            + $" FROM {o}.phpbb_users u WHERE {Sql.Visible("u")} ORDER BY u.user_regdate DESC, u.user_id DESC LIMIT 1",
            null,
            cancellationToken).ConfigureAwait(false);

        return Respond.Json(new JObj
        {
            { "status", "success" },
            { "version", api.Options.Version },
            {
                "counts", new JObj
                {
                    { "online", online },
                    { "total", counts?.Long("total") ?? 0 },
                    { "restricted", counts?.Long("restricted") ?? 0 },
                }
            },
            // Restricted players stay out of public data on TomYum, so the newest one isn't named.
            { "latest_restricted_player", null },
            { "latest_joined_player", newest?.Pick("id", "name", "creation_time") },
        });
    }

    /// <summary>GET /get_all_users: accounts with their bancho.py privileges, searchable by name or id.</summary>
    public static async Task<PluginHttpResponse> AllUsersAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var limit = args.Int("limit", 50, 1, 100) ?? 50;
        var offset = Math.Max(0, args.Int("offset", 0) ?? 0);
        var priv = args.Int("priv", null, 0, int.MaxValue);
        var restricted = args.BoolOrNull("restricted");
        var search = args.Str("q", maxLength: 32);
        if (args.HasErrors)
            return args.ErrorResponse();

        // Restricted accounts are hidden on TomYum: asking for them finds nothing.
        if (restricted == true)
            return Respond.Success(new JObj { { "users", new List<object?>() }, { "total_count", 0 } });

        var parameters = new Dictionary<string, object?>();
        var where = "WHERE 1 = 1";
        if (priv is { } wanted)
        {
            where += " AND (p.priv & @priv) = @priv";
            parameters["@priv"] = wanted;
        }

        var order = "ORDER BY p.playerid ASC";
        search = search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var escaped = search.ToLowerInvariant().Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
            parameters["@q_like"] = "%" + escaped + "%";
            parameters["@q_space_like"] = "%" + escaped.Replace("\\_", " ", StringComparison.Ordinal) + "%";
            parameters["@q_exact"] = search.ToLowerInvariant();
            parameters["@q_prefix"] = escaped + "%";
            var idMatch = string.Empty;
            var idOrder = string.Empty;
            if (search.All(char.IsAsciiDigit) && long.TryParse(search, NumberStyles.None, CultureInfo.InvariantCulture, out var searchId))
            {
                parameters["@q_id"] = searchId;
                idMatch = " OR p.playerid = @q_id";
                idOrder = "(p.playerid = @q_id) DESC, ";
            }

            where += $" AND (LOWER(p.name) LIKE @q_like OR LOWER(p.name) LIKE @q_space_like{idMatch})";
            order = $"ORDER BY {idOrder}(LOWER(p.name) = @q_exact) DESC, (LOWER(p.name) LIKE @q_prefix) DESC, p.playerid ASC";
        }

        var source = $"(SELECT CAST(u.user_id AS SIGNED) AS playerid, u.username AS name, {api.Sql.Priv("u")} AS priv"
            + $" FROM {api.Sql.O}.phpbb_users u WHERE {Sql.Visible("u")} AND NOT {api.Sql.IsBot("u.user_id")}) p";
        var total = await api.Db.ScalarAsync($"SELECT COUNT(*) FROM {source} {where}", parameters, cancellationToken).ConfigureAwait(false);
        var pageParameters = new Dictionary<string, object?>(parameters) { ["@limit"] = limit, ["@offset"] = offset };
        var rows = await api.Db.QueryAsync(
            $"SELECT p.playerid, p.name, p.priv FROM {source} {where} {order} LIMIT @limit OFFSET @offset",
            pageParameters,
            cancellationToken).ConfigureAwait(false);

        return Respond.Success(new JObj
        {
            { "users", rows.Select(r => r.Pick("playerid", "name", "priv")).ToList() },
            { "total_count", total },
        });
    }
}
