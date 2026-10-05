using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Xunit;

namespace TomYum.Plugins.BpyApi.Tests;

/// <summary>
/// Runs only with BPY_LIVE_DB set to a MySQL connection string for a TomYum server's database
/// (the connection is switched to read-only first). Every endpoint is called against real data; with
/// BPY_LIVE_DUMP set to a directory, each answer is saved there to look over.
/// Override the ids with BPY_LIVE_USER, BPY_LIVE_MAP, ... when running against another server.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BPY_LIVE_DB")))
            Skip = "Set BPY_LIVE_DB to a MySQL connection string to run the live database checks.";
    }
}

public sealed class LiveDatabaseTests
{
    private static string Setting(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    private static readonly string User = Setting("BPY_LIVE_USER", "5");
    private static readonly string UserName = Setting("BPY_LIVE_USER_NAME", "blueskychan_");
    private static readonly string Map = Setting("BPY_LIVE_MAP", "1639735");
    private static readonly string RelaxMap = Setting("BPY_LIVE_RELAX_MAP", "1639738");
    private static readonly string MapMd5 = Setting("BPY_LIVE_MAP_MD5", "2594db3fea9db8d15b730de1b7e1ad17");
    private static readonly string Set = Setting("BPY_LIVE_SET", "780651");
    private static readonly string CustomSet = Setting("BPY_LIVE_CUSTOM_SET", "10000000");
    private static readonly string StableScore = Setting("BPY_LIVE_STABLE_SCORE", "52");
    private static readonly string LazerScore = Setting("BPY_LIVE_LAZER_SCORE", "50");
    private static readonly string LazerRelaxScore = Setting("BPY_LIVE_LAZER_RELAX_SCORE", "44");
    private static readonly string RelaxScore = Setting("BPY_LIVE_RELAX_SCORE", "10000000044");
    private static readonly string Clan = Setting("BPY_LIVE_CLAN", "1");

    private static async Task<FakeContext> StartAsync()
    {
        // BPY_LIVE_REPLAYS / BPY_LIVE_LAZER_REPLAYS: copies of the server's replay folders, to check /get_replay.
        var config = new Dictionary<string, string>
        {
            ["replaysPath"] = Setting("BPY_LIVE_REPLAYS", "data/replays"),
            ["lazerReplaysPath"] = Setting("BPY_LIVE_LAZER_REPLAYS", "/app/lazer-replays"),
        };
        var context = new FakeContext(new ReadOnlyData(Environment.GetEnvironmentVariable("BPY_LIVE_DB")!), config);
        await new BpyApiPlugin().InitializeAsync(context).ConfigureAwait(false);
        return context;
    }

    private static async Task<JsonElement> GetJsonAsync(FakeContext context, string pathAndQuery, int status = 200, string host = "api.blueskychan.dev")
    {
        var response = await context.Routes.SendAsync("GET", host, pathAndQuery).ConfigureAwait(false);
        Assert.NotNull(response);
        var body = Encoding.UTF8.GetString(response!.Body.Span);
        Dump(pathAndQuery, body);
        Assert.True(response.StatusCode == status, $"{pathAndQuery}: {response.StatusCode} {body} {string.Join("\n", context.Log.Lines)}");
        Assert.Equal("application/json", response.ContentType);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static void Dump(string pathAndQuery, string body)
    {
        var directory = Environment.GetEnvironmentVariable("BPY_LIVE_DUMP");
        if (string.IsNullOrEmpty(directory))
            return;
        Directory.CreateDirectory(directory);
        var name = pathAndQuery.Replace("/", "_", StringComparison.Ordinal).Replace("?", "__", StringComparison.Ordinal)
            .Replace("&", "_", StringComparison.Ordinal).Replace("=", "-", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(directory, name.Trim('_') + ".json"), body);
    }

    private static void HasKeys(JsonElement element, params string[] keys)
    {
        var actual = new List<string>();
        foreach (var property in element.EnumerateObject())
            actual.Add(property.Name);
        Assert.Equal(keys, actual);
    }

    [LiveFact]
    public async Task Players_endpoints_answer_with_bancho_py_shapes()
    {
        var context = await StartAsync();

        var search = await GetJsonAsync(context, "/v1/search_players?q=sky");
        Assert.True(search.GetProperty("results").GetInt32() >= 1);

        var count = await GetJsonAsync(context, "/v1/get_player_count");
        Assert.Equal(6, count.GetProperty("counts").GetProperty("online").GetInt32());

        var all = await GetJsonAsync(context, $"/v1/get_player_info?scope=all&id={User}");
        var player = all.GetProperty("player");
        HasKeys(player.GetProperty("info"),
            "id", "name", "safe_name", "priv", "country", "silence_end", "donor_end", "creation_time", "latest_activity", "clan_id",
            "clan_priv", "preferred_mode", "play_style", "custom_badge_name", "custom_badge_icon", "custom_badge_color",
            "userpage_content", "votes", "old_name", "preferred_format", "hide_clans_tags", "profile_color", "favourite_mode",
            "location", "interests", "occupation", "restricted");
        HasKeys(player.GetProperty("stats").GetProperty("0"),
            "id", "mode", "tscore", "rscore", "pp", "plays", "playtime", "acc", "max_combo", "total_hits", "replay_views",
            "xh_count", "x_count", "sh_count", "s_count", "a_count", "rank", "country_rank");
        HasKeys(player.GetProperty("stats"), "0", "1", "2", "3", "4", "5", "6", "8");
        Assert.Equal(JsonValueKind.Array, player.GetProperty("history").ValueKind);

        await GetJsonAsync(context, $"/v1/get_player_info?scope=stats&name={UserName}");
        await GetJsonAsync(context, $"/v1/get_player_info?scope=history&id={User}");
        await GetJsonAsync(context, "/v1/get_player_info?scope=info&id=999999", 404);
        await GetJsonAsync(context, $"/v1/get_player_info?scope=info&id={User}&name={UserName}", 400);

        var special = await GetJsonAsync(context, "/v1/special_players");
        Assert.True(special.GetProperty("players").GetArrayLength() >= 1);
        await GetJsonAsync(context, "/v1/special_players?roles=staff");
        await GetJsonAsync(context, "/v1/special_players?roles=nope", 400);

        await GetJsonAsync(context, "/v1/get_recent_players?limit=5");
        var status = await GetJsonAsync(context, $"/v1/get_player_status?id={User}");
        Assert.False(status.GetProperty("player_status").GetProperty("online").GetBoolean());

        // Online (the host's player view), with the time the plugin saw the login.
        context.Players.Players.Add(new Abstractions.PluginPlayerInfo(long.Parse(User, System.Globalization.CultureInfo.InvariantCulture), UserName, 0, 2, "playing", MapMd5, 0, 72, 0));
        await context.Bus.PublishAsync(new Abstractions.PlayerLoginEvent(long.Parse(User, System.Globalization.CultureInfo.InvariantCulture), UserName, "TH"));
        var online = (await GetJsonAsync(context, $"/v1/get_player_status?name={UserName}")).GetProperty("player_status");
        Assert.True(online.GetProperty("online").GetBoolean());
        Assert.Equal(JsonValueKind.Number, online.GetProperty("login_time").ValueKind);
        Assert.Equal(long.Parse(Map, System.Globalization.CultureInfo.InvariantCulture), online.GetProperty("status").GetProperty("beatmap").GetProperty("id").GetInt64());

        // Deleted (and restricted) accounts are hidden.
        await GetJsonAsync(context, $"/v1/get_player_info?scope=info&id={Setting("BPY_LIVE_HIDDEN_USER", "33")}", 404);

        var medals = await GetJsonAsync(context, $"/v1/get_player_achievements?id={User}");
        Assert.True(medals.GetProperty("unlocked_achievements").GetArrayLength() >= 1);

        await GetJsonAsync(context, $"/v1/get_player_most_played?id={User}");
        await GetJsonAsync(context, $"/v1/get_player_most_played?id={User}&mode=4");

        var statistics = await GetJsonAsync(context, "/v1/statistics");
        Assert.True(statistics.GetProperty("counts").GetProperty("total").GetInt32() > 0);

        var users = await GetJsonAsync(context, "/v1/get_all_users?limit=5");
        Assert.True(users.GetProperty("total_count").GetInt32() > 0);
        await GetJsonAsync(context, "/v1/get_all_users?q=sky");
        await GetJsonAsync(context, $"/v1/get_all_users?q={User}");
        await GetJsonAsync(context, "/v1/get_all_users?priv=8192");
        var restricted = await GetJsonAsync(context, "/v1/get_all_users?restricted=true");
        Assert.Equal(0, restricted.GetProperty("total_count").GetInt32());
    }

    [LiveFact]
    public async Task Score_endpoints_answer_with_bancho_py_shapes()
    {
        var context = await StartAsync();

        foreach (var scope in new[] { "best", "recent", "first" })
        {
            foreach (var mode in new[] { "0", "4" })
            {
                var scores = await GetJsonAsync(context, $"/v1/get_player_scores?scope={scope}&id={User}&mode={mode}");
                Assert.Equal(JsonValueKind.Array, scores.GetProperty("scores").ValueKind);
            }
        }

        var best = await GetJsonAsync(context, $"/v1/get_player_scores?scope=recent&id={User}&mode=0&limit=1");
        HasKeys(best.GetProperty("scores")[0],
            "id", "score", "pp", "acc", "max_combo", "mods", "n300", "n100", "n50", "nmiss", "ngeki", "nkatu", "grade", "status",
            "mode", "play_time", "time_elapsed", "perfect", "beatmap", "mods_readable");
        await GetJsonAsync(context, $"/v1/get_player_scores?scope=best&id={User}&mods=~HD");
        await GetJsonAsync(context, $"/v1/get_player_scores?scope=best&id={User}&mode=7", 400);

        var board = await GetJsonAsync(context, $"/v1/get_map_scores?scope=best&id={Map}");
        Assert.True(board.GetProperty("scores").GetArrayLength() >= 1);
        HasKeys(board.GetProperty("scores")[0],
            "map_md5", "id", "score", "pp", "acc", "max_combo", "mods", "n300", "n100", "n50", "nmiss", "ngeki", "nkatu",
            "grade", "status", "mode", "play_time", "time_elapsed", "userid", "perfect", "player_name", "country",
            "clan_id", "clan_name", "clan_tag");
        await GetJsonAsync(context, $"/v1/get_map_scores?scope=recent&md5={MapMd5}");
        var relaxBoard = await GetJsonAsync(context, $"/v1/get_map_scores?scope=best&id={RelaxMap}&mode=4");
        Assert.True(relaxBoard.GetProperty("scores").GetArrayLength() >= 1);

        foreach (var id in new[] { StableScore, LazerScore, LazerRelaxScore, RelaxScore })
        {
            var info = await GetJsonAsync(context, $"/v1/get_score_info?id={id}");
            HasKeys(info.GetProperty("score"),
                "id", "map_md5", "score", "pp", "acc", "max_combo", "mods", "n300", "n100", "n50", "nmiss", "ngeki", "nkatu",
                "grade", "status", "mode", "play_time", "time_elapsed", "client_flags", "userid", "perfect", "online_checksum");
        }

        await GetJsonAsync(context, "/v1/get_score_info?id=9999999", 404);

        var recent = await GetJsonAsync(context, "/v1/get_recent_scores?limit=10");
        Assert.True(recent.GetProperty("recent_scores").GetArrayLength() >= 1);
        await GetJsonAsync(context, "/v1/get_recent_scores?limit=3&offset=2");
        await GetJsonAsync(context, "/v1/get_top_osu_std_scores?limit=10");
        var top = await GetJsonAsync(context, "/v1/get_top_play?mode=0");
        Assert.True(top.GetProperty("total").GetInt32() >= 1);
        await GetJsonAsync(context, "/v1/get_top_play?mode=4&country=th");
        var total = await GetJsonAsync(context, "/v1/get_total_scores");
        Assert.True(total.GetProperty("total_scores").GetInt32() > 0);
    }

    [LiveFact]
    public async Task Maps_rankings_and_clans_answer()
    {
        var context = await StartAsync();

        var map = await GetJsonAsync(context, $"/v1/get_map_info?id={Map}");
        HasKeys(map.GetProperty("map"),
            "md5", "id", "set_id", "artist", "title", "version", "creator", "last_update", "total_length", "max_combo", "status",
            "plays", "passes", "mode", "bpm", "cs", "od", "ar", "hp", "diff", "is_dan");
        await GetJsonAsync(context, $"/v1/get_map_info?md5={MapMd5}");
        var custom = await GetJsonAsync(context, $"/v1/get_map_info?id={CustomSet}");
        Assert.True(custom.GetProperty("map").TryGetProperty("somtum", out _));
        await GetJsonAsync(context, "/v1/get_map_info?id=2", 422);

        var set = await GetJsonAsync(context, $"/v1/get_beatmapset_info?set_id={Set}");
        Assert.True(set.GetProperty("beatmaps").GetArrayLength() >= 1);
        await GetJsonAsync(context, $"/v1/get_beatmapset_info?set_id={CustomSet}");
        await GetJsonAsync(context, "/v1/get_beatmapset_info?set_id=9999999", 404);

        var search = await GetJsonAsync(context, "/v1/search_beatmaps?q=a&limit=5");
        Assert.True(search.GetProperty("meta").GetProperty("total").GetInt32() >= 1);
        await GetJsonAsync(context, "/v1/search_beatmaps?status=ranked,loved&mode=0&sort=plays_desc");
        await GetJsonAsync(context, "/v1/search_beatmaps?somtum_only=true");
        await GetJsonAsync(context, "/v1/search_beatmaps?q=a&sort=relevance");
        await GetJsonAsync(context, "/v1/search_beatmaps?status=bogus", 400);

        foreach (var sort in new[] { "pp", "tscore", "rscore", "acc", "plays", "playtime" })
        {
            await GetJsonAsync(context, $"/v1/get_leaderboard?sort={sort}");
            await GetJsonAsync(context, $"/v1/get_leaderboard?sort={sort}&mode=4");
        }

        var leaderboard = await GetJsonAsync(context, "/v1/get_leaderboard?country=th");
        Assert.True(leaderboard.GetProperty("total").GetInt32() >= 1);

        var clan = await GetJsonAsync(context, $"/v1/get_clan?id={Clan}");
        Assert.True(clan.GetProperty("members").GetArrayLength() >= 1);
        await GetJsonAsync(context, "/v1/get_clans_leaderboard");
        await GetJsonAsync(context, "/v1/get_clans_leaderboard?sort_by=rscore&mode=4");

        var rankOne = await GetJsonAsync(context, "/v1/get_rank_one_history");
        Assert.Equal(JsonValueKind.Object, rankOne.GetProperty("history").ValueKind);

        // The same API on the website's host.
        await GetJsonAsync(context, $"/bpy-api/v1/get_map_info?id={Map}", host: "osu.blueskychan.dev");
    }

    [LiveFact]
    public async Task Replays_come_back_as_osr_files()
    {
        var context = await StartAsync();
        foreach (var id in new[] { StableScore, LazerScore, LazerRelaxScore })
        {
            var response = await context.Routes.SendAsync("GET", "api.blueskychan.dev", $"/v1/get_replay?id={id}");
            Assert.NotNull(response);
            if (response!.StatusCode == 404)
                continue; // the replay files are inside the server's container; outside it they aren't found.
            Assert.Equal("application/octet-stream", response.ContentType);
            Assert.StartsWith("attachment; filename*=UTF-8''", response.Headers!["Content-Disposition"], StringComparison.Ordinal);
            var osr = response.Body.ToArray();
            var frames = Osr.Frames(osr);
            Assert.NotNull(frames);

            var raw = await context.Routes.SendAsync("GET", "api.blueskychan.dev", $"/v1/get_replay?id={id}&include_headers=false");
            Assert.Equal(frames, raw!.Body.ToArray());
            var directory = Environment.GetEnvironmentVariable("BPY_LIVE_DUMP");
            if (!string.IsNullOrEmpty(directory))
                File.WriteAllBytes(Path.Combine(directory, $"replay-{id}.osr"), osr);
        }
    }
}

public sealed class LiveExplainTests
{
    private static readonly string[] PlanColumns = ["id", "select_type", "table", "type", "key", "rows", "Extra"];

    /// <summary>EXPLAINs every query a set of calls makes (read-only) and saves the plans to BPY_LIVE_DUMP/explain.txt.</summary>
    [LiveFact]
    public async Task Queries_can_be_explained()
    {
        var queries = new List<(string Sql, IReadOnlyDictionary<string, object?>? Parameters)>();
        Db.Trace = (sql, parameters) => queries.Add((sql, parameters is null ? null : new Dictionary<string, object?>(parameters)));
        try
        {
            var data = new ReadOnlyData(Environment.GetEnvironmentVariable("BPY_LIVE_DB")!);
            var context = new FakeContext(data);
            await new BpyApiPlugin().InitializeAsync(context);
            foreach (var path in new[]
            {
                "/v1/get_player_scores?scope=best&id=5", "/v1/get_player_scores?scope=recent&id=5&mode=4",
                "/v1/get_player_scores?scope=first&id=5", "/v1/get_map_scores?scope=best&id=1639735",
                "/v1/get_map_scores?scope=best&id=1639738&mode=4", "/v1/get_score_info?id=52", "/v1/get_score_info?id=10000000044",
                "/v1/get_recent_scores", "/v1/get_top_play?mode=0", "/v1/get_leaderboard", "/v1/get_player_info?scope=all&id=5",
            })
            {
                await context.Routes.SendAsync("GET", "api.blueskychan.dev", path);
            }

            Db.Trace = null;
            var report = new StringBuilder();
            var connection = await data.OpenConnectionAsync();
            await using (connection)
            {
                foreach (var (sql, parameters) in queries)
                {
                    report.AppendLine("### " + sql);
                    foreach (var row in await Db.QueryAsync(connection, "EXPLAIN " + sql, parameters, default))
                    {
                        report.AppendLine(string.Join(" | ", PlanColumns.Select(c => c + "=" + row.Str(c))));
                    }
                }
            }

            var directory = Environment.GetEnvironmentVariable("BPY_LIVE_DUMP");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "explain.txt"), report.ToString());
            }
        }
        finally
        {
            Db.Trace = null;
        }
    }
}
