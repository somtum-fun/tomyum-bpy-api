using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

using Xunit;

namespace TomYum.Plugins.BpyApi.Tests;

public sealed class ModsTests
{
    [Theory]
    [InlineData(0, "NM")]
    [InlineData(72, "HDDT")]
    [InlineData(712, "HDDTRXNC")]
    [InlineData(8192 | 16, "HRAP")]
    [InlineData(1 << 29, "V2")]
    public void Readable_is_bancho_py_repr(long mods, string expected) => Assert.Equal(expected, Mods.Readable(mods));

    [Fact]
    public void Mod_strings_parse_in_two_letter_chunks()
    {
        Assert.Equal(72, Mods.FromModString("hddt"));
        Assert.Equal(8, Mods.FromModString("HDXX"));
        Assert.Equal(0, Mods.FromModString(string.Empty));
    }

    [Fact]
    public void Filters_take_equality_prefixes()
    {
        Assert.Null(Mods.ParseFilter(null));
        Assert.Equal((8L, false), Mods.ParseFilter("~HD"));
        Assert.Equal((72L, true), Mods.ParseFilter("=72"));
        Assert.Equal((72L, true), Mods.ParseFilter("72"));
        Assert.Equal((72L, true), Mods.ParseFilter("HDDT"));
    }

    [Fact]
    public void Lazer_acronyms_carry_the_bits_stable_sends()
    {
        Assert.Equal(512 | 64 | 8, Mods.FromAcronyms(["NC", "HD"]));
        Assert.Equal(16384 | 32, Mods.FromAcronyms(["PF"]));
        Assert.Equal(0, Mods.FromAcronyms(["CL", "DA"]));
    }
}

public sealed class JsonTextTests
{
    [Theory]
    [InlineData(95.0, "95.0")]
    [InlineData(1.5, "1.5")]
    [InlineData(0.0, "0.0")]
    [InlineData(1e20, "1e+20")]
    public void Floats_are_written_like_python(double value, string expected) => Assert.Equal(expected, JsonText.FormatDouble(value));

    [Fact]
    public void Datetimes_are_naive_iso_with_microseconds_only_when_present()
    {
        Assert.Equal("2026-10-04T20:31:30", JsonText.FormatDateTime(new DateTime(2026, 10, 4, 20, 31, 30, DateTimeKind.Utc)));
        Assert.Equal(
            "2026-09-30T20:53:29.593642",
            JsonText.FormatDateTime(new DateTime(2026, 9, 30, 20, 53, 29, DateTimeKind.Utc).AddTicks(5936420)));
    }

    [Fact]
    public void Objects_keep_their_order_and_text_stays_utf8()
    {
        var json = JsonText.SerializeToString(new JObj
        {
            { "z", 1 },
            { "a", "ไทย" },
            { "n", null },
            { "b", true },
            { "f", 2.0 },
            { "nan", double.NaN },
            { "list", new List<object?> { 1L, "x" } },
        });
        Assert.Equal("{\"z\":1,\"a\":\"ไทย\",\"n\":null,\"b\":true,\"f\":2.0,\"nan\":null,\"list\":[1,\"x\"]}", json);
    }

    [Fact]
    public void Setting_a_key_twice_keeps_its_place()
    {
        var obj = new JObj { { "a", 1 }, { "b", 2 } };
        obj["a"] = 3;
        Assert.Equal("{\"a\":3,\"b\":2}", JsonText.SerializeToString(obj));
    }
}

public sealed class QueryArgsTests
{
    private static Dictionary<string, string> Query(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static JsonElement Detail(QueryArgs args)
    {
        var response = args.ErrorResponse();
        Assert.Equal(422, response.StatusCode);
        using var document = JsonDocument.Parse(response.Body.ToArray());
        return document.RootElement.GetProperty("detail").Clone();
    }

    [Fact]
    public void Missing_required_values_are_fastapi_errors()
    {
        var args = new QueryArgs(Query());
        args.Literal("scope", null, "recent", "best", "first");
        var error = Detail(args)[0];
        Assert.Equal("missing", error.GetProperty("type").GetString());
        Assert.Equal("query", error.GetProperty("loc")[0].GetString());
        Assert.Equal("scope", error.GetProperty("loc")[1].GetString());
        Assert.Equal("Field required", error.GetProperty("msg").GetString());
    }

    [Fact]
    public void Literals_list_what_they_expect()
    {
        var args = new QueryArgs(Query(("scope", "worst")));
        args.Literal("scope", null, "recent", "best", "first");
        var error = Detail(args)[0];
        Assert.Equal("literal_error", error.GetProperty("type").GetString());
        Assert.Equal("Input should be 'recent', 'best' or 'first'", error.GetProperty("msg").GetString());
    }

    [Fact]
    public void Integers_are_parsed_and_bounded()
    {
        Assert.Equal(5, new QueryArgs(Query(("id", "5"))).Int("id", null, 1, 10));

        var notANumber = new QueryArgs(Query(("id", "abc")));
        notANumber.Int("id");
        Assert.Equal("int_parsing", Detail(notANumber)[0].GetProperty("type").GetString());

        var tooSmall = new QueryArgs(Query(("id", "0")));
        tooSmall.Int("id", null, 1);
        var error = Detail(tooSmall)[0];
        Assert.Equal("greater_than_equal", error.GetProperty("type").GetString());
        Assert.Equal("Input should be greater than or equal to 1", error.GetProperty("msg").GetString());
        Assert.Equal(1, error.GetProperty("ctx").GetProperty("ge").GetInt32());

        var tooBig = new QueryArgs(Query(("limit", "101")));
        tooBig.Int("limit", 25, 1, 100);
        Assert.Equal("less_than_equal", Detail(tooBig)[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Booleans_take_pydantic_words()
    {
        Assert.True(new QueryArgs(Query(("x", "yes"))).Bool("x", false));
        Assert.False(new QueryArgs(Query(("x", "0"))).Bool("x", true));
        Assert.True(new QueryArgs(Query()).Bool("x", true));
        var bad = new QueryArgs(Query(("x", "maybe")));
        bad.Bool("x", false);
        Assert.Equal("bool_parsing", Detail(bad)[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Strings_check_length_then_pattern()
    {
        var shortMd5 = new QueryArgs(Query(("md5", "abc")));
        shortMd5.Str("md5", minLength: 32, maxLength: 32);
        Assert.Equal("String should have at least 32 characters", Detail(shortMd5)[0].GetProperty("msg").GetString());

        var badName = new QueryArgs(Query(("name", "a!")));
        badName.Str("name", pattern: Players.UsernamePattern);
        Assert.Equal("string_pattern_mismatch", Detail(badName)[0].GetProperty("type").GetString());

        Assert.Equal("some user", new QueryArgs(Query(("name", "some user"))).Str("name", pattern: Players.UsernamePattern));
    }
}

public sealed class OsrTests
{
    private static ReplayHeader Header() => new(
        0, "2594db3fea9db8d15b730de1b7e1ad17", "blueskychan_", 34, 0, 0, 11, 0, 0, 47630, 68, true, 64,
        new DateTime(2026, 10, 4, 20, 31, 30, DateTimeKind.Utc), 52);

    [Fact]
    public void Checksum_matches_bancho_py()
    {
        // hashlib.md5("34p0o11o0t0a2594db3fea9db8d15b730de1b7e1ad17r68eTrueyblueskychan_o47630u064True")
        Assert.Equal("dc87c743d463dc663a4095ae60ab5129", Osr.Checksum(Header()));
    }

    [Fact]
    public void Built_replays_read_back_their_frames()
    {
        byte[] frames = [0x5d, 0x00, 0x00, 0x20, 0x00, 1, 2, 3, 4, 5];
        var osr = Osr.Build(Header(), frames);
        Assert.Equal(0, osr[0]);
        Assert.Equal(Osr.Version, BitConverter.ToInt32(osr, 1));
        Assert.Equal(frames, Osr.Frames(osr));
        Assert.Equal(52, BitConverter.ToInt64(osr, osr.Length - 8));
    }

    [Fact]
    public void Broken_files_give_no_frames()
    {
        Assert.Null(Osr.Frames([0, 1, 2]));
        Assert.Null(Osr.Frames([0, 0, 0, 0, 0, 0x0b, 0xff]));
    }

    [Theory]
    [InlineData("peppy - Hanasaka Yui - Harumachi Clover [Laura's Normal] (2026-10-04).osr",
        "peppy%20-%20Hanasaka%20Yui%20-%20Harumachi%20Clover%20%5BLaura%27s%20Normal%5D%20%282026-10-04%29.osr")]
    [InlineData("ไทย a/b~c", "%E0%B9%84%E0%B8%97%E0%B8%A2%20a/b~c")]
    public void Quote_matches_urllib(string value, string expected) => Assert.Equal(expected, Osr.Quote(value));

    [Fact]
    public void File_names_follow_bancho_py()
    {
        Assert.Equal(
            "peppy - A - B [C] (2026-10-04).osr",
            Osr.FileName("peppy", "A", "B", "C", new DateTime(2026, 10, 4, 1, 2, 3, DateTimeKind.Utc)));
    }
}

public sealed class SmallRulesTests
{
    [Fact]
    public void Rank_history_ring_days_count_back_from_the_last_column()
    {
        var last = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(last, RankHistory.DayOf(4, 4, last));
        Assert.Equal(last.AddDays(-4), RankHistory.DayOf(0, 4, last));
        Assert.Equal(last.AddDays(-89), RankHistory.DayOf(5, 4, last));
    }

    [Fact]
    public void Previous_names_take_the_newest()
    {
        Assert.Null(Players.LastPreviousName(null));
        Assert.Equal("Lilian", Players.LastPreviousName("Lilian"));
        Assert.Equal("b", Players.LastPreviousName("[\"a\",\"b\"]"));
    }

    [Theory]
    [InlineData(0, 0, "unknown")]
    [InlineData(0, 200, "legendary")]
    [InlineData(4, 100, "epic")]
    [InlineData(10, 100, "rare")]
    [InlineData(39, 100, "uncommon")]
    [InlineData(40, 100, "common")]
    public void Rarity_tiers(long unlocked, long total, string expected) => Assert.Equal(expected, Players.Rarity(unlocked, total));

    [Fact]
    public void Search_status_names_invert_like_bancho_py()
    {
        Assert.Equal("graveyard", RankedStatus.SearchName(0));
        Assert.Equal("loved", RankedStatus.SearchName(5));
        Assert.Equal("-1", RankedStatus.SearchName(-1));
    }

    [Fact]
    public void Database_names_must_be_plain()
    {
        Assert.Throws<ArgumentException>(() => new Sql("osu`; DROP", "tomyum", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Sql("osu", "tomyum", 0));
        var sql = new Sql("web", "game", 1000);
        Assert.Contains("`web`.scores", sql.VanillaScores, StringComparison.Ordinal);
        Assert.Contains("`game`.scores", sql.ModeScores, StringComparison.Ordinal);
        Assert.Contains("1000 + t.Id", sql.ModeScores, StringComparison.Ordinal);
    }

    [Fact]
    public void Privilege_sql_is_balanced()
    {
        foreach (var flags in new[] { true, false })
        {
            var sql = new Sql("osu", "tomyum", 1) { HasAccountFlags = flags };
            var priv = sql.Priv("u");
            Assert.Equal(priv.Count(c => c == '('), priv.Count(c => c == ')'));
            Assert.Equal(flags, priv.Contains("tomyum_account_flags", StringComparison.Ordinal));
        }
    }
}

public sealed class PluginTests
{
    private static async Task<(BpyApiPlugin Plugin, FakeContext Context)> StartAsync(Dictionary<string, string>? config = null)
    {
        var context = new FakeContext(config: config);
        var plugin = new BpyApiPlugin();
        await plugin.InitializeAsync(context);
        return (plugin, context);
    }

    private static string Body(PluginHttpResponse? response) => Encoding.UTF8.GetString(response!.Body.Span);

    [Fact]
    public async Task Every_route_is_served_on_both_hosts_and_nowhere_else()
    {
        var (_, context) = await StartAsync();
        foreach (var (method, name, _) in BpyApiPlugin.Routes)
        {
            Assert.NotNull(await context.Routes.SendAsync(method, "api.blueskychan.dev", "/v1/" + name));
            Assert.NotNull(await context.Routes.SendAsync(method, "osu.blueskychan.dev", "/bpy-api/v1/" + name));
            Assert.Null(await context.Routes.SendAsync(method, "c.blueskychan.dev", "/v1/" + name));
            Assert.Null(await context.Routes.SendAsync(method, "osu.blueskychan.dev", "/v1/" + name));
        }
    }

    [Fact]
    public async Task Unknown_paths_and_methods_answer_like_fastapi()
    {
        var (_, context) = await StartAsync();
        var missing = await context.Routes.SendAsync("GET", "api.blueskychan.dev", "/v1/nope");
        Assert.Equal(404, missing!.StatusCode);
        Assert.Equal("{\"detail\":\"Not Found\"}", Body(missing));

        var wrongMethod = await context.Routes.SendAsync("POST", "api.blueskychan.dev", "/v1/get_player_info");
        Assert.Equal(405, wrongMethod!.StatusCode);
        Assert.Equal("{\"detail\":\"Method Not Allowed\"}", Body(wrongMethod));
    }

    [Fact]
    public async Task Without_database_access_the_data_endpoints_say_so()
    {
        var (_, context) = await StartAsync();
        var response = await context.Routes.SendAsync("GET", "api.blueskychan.dev", "/v1/get_player_info?scope=all&id=5");
        Assert.Equal(503, response!.StatusCode);
        Assert.Contains("Plugins:Trusted", Body(response), StringComparison.Ordinal);
        Assert.Contains(context.Log.Lines, l => l.StartsWith("warn:", StringComparison.Ordinal));

        // Validation still comes first, like FastAPI.
        var invalid = await context.Routes.SendAsync("GET", "api.blueskychan.dev", "/v1/get_player_info?scope=nope&id=5");
        Assert.Equal(422, invalid!.StatusCode);
    }

    [Fact]
    public async Task Only_the_public_api_is_served()
    {
        var (_, context) = await StartAsync();
        // bancho.py's extras (API keys, sessions, matches, pools, the bot's actions) are unknown paths here.
        foreach (var (method, path) in new[]
                 {
                     ("GET", "/v1/calculate_pp?id=75"), ("GET", "/v1/get_match?id=3"), ("GET", "/v1/get_mappool?id=1"),
                     ("GET", "/v1/get_user_private_info?userid=5"), ("GET", "/v1/get_active_matches"),
                     ("POST", "/v1/vote_beatmap"), ("POST", "/v1/rank_beatmap"), ("POST", "/v1/restrict_player"),
                     ("POST", "/v1/whitelist_player"), ("POST", "/v1/donation_approve"),
                 })
        {
            var response = await context.Routes.SendAsync(method, "api.blueskychan.dev", path);
            Assert.Equal((404, "{\"detail\":\"Not Found\"}"), (response!.StatusCode, Body(response)));
        }
    }

    [Fact]
    public async Task Mounts_follow_the_config()
    {
        var (_, context) = await StartAsync(new Dictionary<string, string>
        {
            ["apiSubdomain"] = string.Empty,
            ["apiPrefix"] = "api/v1/",
            ["websitePrefix"] = string.Empty,
        });
        Assert.NotNull(await context.Routes.SendAsync("GET", "anything.example.com", "/api/v1/get_player_count"));
        Assert.Null(await context.Routes.SendAsync("GET", "osu.example.com", "/bpy-api/v1/get_player_count"));
    }

    [Fact]
    public async Task Logins_are_remembered_and_shutdown_unregisters_everything()
    {
        var (plugin, context) = await StartAsync();
        await context.Bus.PublishAsync(new PlayerLoginEvent(5, "blueskychan_", "TH"));
        Assert.True(plugin.Api!.LoginTimes.ContainsKey(5));

        await plugin.ShutdownAsync();
        Assert.Empty(context.Routes.Routes);
        Assert.Equal(0, context.Bus.Count);
    }

    [Fact]
    public void Domain_comes_from_config_or_the_host()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = "api.blueskychan.dev:443" };
        var request = new PluginHttpRequest("GET", "/v1/x", new Dictionary<string, string>(), new Dictionary<string, string>(), headers, null, null);
        Assert.Equal("blueskychan.dev", new Api(null, new ApiOptions(), null, null, null).Domain(request));
        Assert.Equal("example.com", new Api(null, new ApiOptions { Domain = "example.com" }, null, null, null).Domain(request));
    }

    [Fact]
    public void Replays_are_found_where_tomyum_keeps_them()
    {
        var api = new Api(null, new ApiOptions { ReplaysPath = "/data/replays", LazerReplaysPath = "/lazer" }, null, null, null);
        Assert.Equal("/data/replays/46.osr", api.ReplayPath("stable", 46));
        Assert.Equal("/lazer/50", api.ReplayPath("lazer", 50));
        Assert.Null(api.ReplayPath("lazer", 0));
        Assert.Null(new Api(null, new ApiOptions { LazerReplaysPath = string.Empty }, null, null, null).ReplayPath("lazer", 50));
    }

    [Fact]
    public async Task Online_counts_add_up_every_client()
    {
        var context = new FakeContext();
        var api = new Api(null, new ApiOptions(), context.Players, context.Online, null);
        Assert.Equal(6, await api.OnlineCountAsync(default));
        context.Online.Counts = new HostOnlineCounts(1, 0, null, 0);
        Assert.Equal(1, await api.OnlineCountAsync(default));
    }
}
