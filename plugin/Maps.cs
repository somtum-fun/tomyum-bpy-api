// Beatmap endpoints: /get_map_info, /get_beatmapset_info, /search_beatmaps.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.BpyApi;

internal static class Maps
{
    /// <summary>Custom (uploaded) sets and maps start here (TomYum's <c>Beatmap.CustomIdFloor</c>).</summary>
    public const long CustomIdFloor = 10_000_000;

    /// <summary>A beatmap as <c>Beatmap.as_dict</c>, by id or md5; null when the website doesn't have it.</summary>
    public static async Task<JObj?> FindAsync(Api api, long? id, string? md5, CancellationToken cancellationToken)
    {
        var row = await FindRowAsync(api, id, md5, cancellationToken).ConfigureAwait(false);
        return row is null ? null : Sql.BeatmapJson(row);
    }

    public static async Task<Row?> FindRowAsync(Api api, long? id, string? md5, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>();
        string where;
        if (id is { } beatmapId)
        {
            parameters["@id"] = beatmapId;
            where = "b.beatmap_id = @id";
        }
        else if (!string.IsNullOrEmpty(md5))
        {
            parameters["@md5"] = md5;
            where = "b.checksum = @md5";
        }
        else
        {
            return null;
        }

        return await api.Db.FirstAsync(
            $"SELECT {Sql.BeatmapColumns} FROM {api.Sql.BeatmapFrom} WHERE {where} AND b.deleted_at IS NULL LIMIT 1",
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Beatmaps as <c>as_dict</c> by id, for many score rows at once.</summary>
    public static async Task<Dictionary<long, JObj>> ManyAsync(Api api, IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var distinct = ids.Distinct().ToList();
        var result = new Dictionary<long, JObj>();
        if (distinct.Count == 0)
            return result;

        var parameters = new Dictionary<string, object?>();
        var rows = await api.Db.QueryAsync(
            $"SELECT {Sql.BeatmapColumns} FROM {api.Sql.BeatmapFrom} WHERE b.beatmap_id IN ({Db.InList(parameters, "b", distinct)})",
            parameters,
            cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
            result[row.Long("id")] = Sql.BeatmapJson(row);
        return result;
    }

    /// <summary>GET /get_map_info.</summary>
    public static async Task<PluginHttpResponse> InfoAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var id = args.Int("id", null, 3, int.MaxValue);
        var md5 = args.Str("md5", minLength: 32, maxLength: 32);
        if (args.HasErrors)
            return args.ErrorResponse();
        if (id is null && md5 is null)
            return Respond.Status(400, "Must provide either id or md5!");

        var map = await FindAsync(api, id, id is null ? md5 : null, cancellationToken).ConfigureAwait(false);
        if (map is null)
            return Respond.Status(404, "Map not found.");

        var extras = await SetExtrasAsync(api, request, Convert.ToInt64(map["set_id"], CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        if (extras is not null)
            map.Add("somtum", extras);
        return Respond.Success(new JObj { { "map", map } });
    }

    /// <summary>GET /get_beatmapset_info: a set's difficulties.</summary>
    public static async Task<PluginHttpResponse> SetInfoAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var setId = args.RequiredInt("set_id", 0, int.MaxValue);
        if (args.HasErrors)
            return args.ErrorResponse();

        var o = api.Sql.O;
        var rows = await api.Db.QueryAsync(
            $"SELECT {Sql.BeatmapColumns}, CAST(b.user_id AS SIGNED) AS owner_id, CAST(bs.user_id AS SIGNED) AS set_owner_id, ou.username AS owner_name"
            + $" FROM {api.Sql.BeatmapFrom}"
            + $" LEFT JOIN {o}.phpbb_users ou ON ou.user_id = b.user_id AND {Sql.Visible("ou")}"
            + " WHERE b.beatmapset_id = @set AND b.deleted_at IS NULL ORDER BY b.beatmap_id",
            new Dictionary<string, object?> { ["@set"] = setId },
            cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
            return Respond.Status(404, "Beatmap set not found.");

        var beatmaps = new List<object?>();
        long setStatus = 0;
        string? creator = null;
        foreach (var row in rows)
        {
            // A guest difficulty's owner: only for uploaded sets, where the ids are this server's
            // (an official set's mapper ids are osu!'s, not ours).
            JObj? owner = null;
            if (setId >= CustomIdFloor && row.Long("owner_id") != row.Long("set_owner_id") && row.Str("owner_name") is { } ownerName)
                owner = new JObj { { "user_id", row.Long("owner_id") }, { "name", ownerName } };

            beatmaps.Add(new JObj
            {
                { "beatmap_id", row.Long("id") },
                { "difficulty_name", row.Text("version") },
                { "mode", row.Long("mode") },
                { "cs", row.Json("cs") },
                { "ar", row.Json("ar") },
                { "od", row.Json("od") },
                { "hp", row.Json("hp") },
                { "diff", row.Json("diff") },
                { "status", row.Long("status") },
                { "owner", owner },
            });
            setStatus = row.Long("status");
            creator ??= row.Str("creator");
        }

        return Respond.Success(new JObj
        {
            { "beatmaps", beatmaps },
            { "author", creator },
            { "is_dan", false },
            { "somtum", await SetExtrasAsync(api, request, setId, cancellationToken).ConfigureAwait(false) },
        });
    }

    /// <summary>
    /// The fork's <c>somtum</c> block for a set uploaded to this server (TomYum's custom sets): who
    /// uploaded it and where its preview and cover are. Null for other sets. TomYum has no vote
    /// queue, so <c>votes</c> is always empty.
    /// </summary>
    public static async Task<JObj?> SetExtrasAsync(Api api, PluginHttpRequest request, long setId, CancellationToken cancellationToken)
    {
        if (setId < CustomIdFloor)
            return null;

        var row = await api.Db.FirstAsync(
            $"SELECT CAST(c.owner_id AS SIGNED) AS owner_id, c.uploaded_at AS uploaded_at, u.username AS name"
            + $" FROM {api.Sql.T}.custom_beatmapsets c JOIN {api.Sql.O}.phpbb_users u ON u.user_id = c.owner_id AND {Sql.Visible("u")}"
            + " WHERE c.set_id = @set",
            new Dictionary<string, object?> { ["@set"] = setId },
            cancellationToken).ConfigureAwait(false);
        if (row is null)
            return null;

        var (preview, cover) = AssetUrls(api.Domain(request), setId, custom: true);
        return new JObj
        {
            { "is_somtum_uploaded", true },
            {
                "uploaded_by", new JObj
                {
                    { "user_id", row.Long("owner_id") },
                    { "name", row.Text("name") },
                    { "uploaded_at", row.Time("uploaded_at") is { } at ? JsonText.FormatDateTime(at) : null },
                }
            },
            { "votes", new List<object?>() },
            { "preview_url", preview },
            { "cover_url", cover },
        };
    }

    /// <summary>Where a set's audio preview and cover are: TomYum's for uploaded sets, osu!'s for the rest.</summary>
    public static (string Preview, string Cover) AssetUrls(string domain, long setId, bool custom)
    {
        var id = setId.ToString(CultureInfo.InvariantCulture);
        return custom
            ? ($"https://osu.{domain}/uploads/default/beatmaps/{id}/preview.mp3", $"https://osu.{domain}/uploads/default/beatmaps/{id}/covers/cover.jpg")
            : ($"https://b.ppy.sh/preview/{id}.mp3", $"https://assets.ppy.sh/beatmaps/{id}/covers/cover.jpg");
    }

    private static readonly Dictionary<string, string> SearchSorts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["last_update_desc"] = "MAX(b.last_update) DESC",
        ["last_update_asc"] = "MAX(b.last_update) ASC",
        ["plays_desc"] = "SUM(b.playcount) DESC",
        ["diff_asc"] = "MIN(b.difficultyrating) ASC",
        ["diff_desc"] = "MAX(b.difficultyrating) DESC",
    };

    /// <summary>GET /search_beatmaps: sets (official and uploaded ones) with filters.</summary>
    public static async Task<PluginHttpResponse> SearchAsync(Api api, PluginHttpRequest request, CancellationToken cancellationToken)
    {
        var args = new QueryArgs(request.Query);
        var q = args.Str("q", string.Empty, maxLength: 128) ?? string.Empty;
        var mode = args.Int("mode", -1, -1, 3) ?? -1;
        var statusFilter = args.Str("status", "any") ?? "any";
        var osuStatus = args.Int("osu_status");
        var somtumOnly = args.Bool("somtum_only", false);
        var danOnly = args.Bool("dan_only", false);
        var creator = args.Str("creator", string.Empty) ?? string.Empty;
        var artist = args.Str("artist", string.Empty) ?? string.Empty;
        var limit = args.Int("limit", 25, 1, 100) ?? 25;
        var offset = args.Int("offset", 0, 0) ?? 0;
        var sort = args.Str("sort", "last_update_desc") ?? "last_update_desc";
        if (args.HasErrors)
            return args.ErrorResponse();

        List<int> statuses;
        if (statusFilter != "any")
        {
            var names = statusFilter.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var bad = names.Where(n => RankedStatus.SearchNames.All(s => s.Name != n)).ToList();
            if (bad.Count > 0)
                return Respond.Status(400, $"Invalid status filter: {string.Join(", ", bad)}.");
            statuses = names.Select(n => RankedStatus.SearchNames.First(s => s.Name == n).Value).Distinct().ToList();
        }
        else
        {
            statuses = RankedStatus.SearchNames.Select(s => s.Value).Distinct().ToList();
        }

        var sql = api.Sql;
        var status = RankedStatus.Sql("tb", "b");
        var parameters = new Dictionary<string, object?>();
        var where = new List<string> { "b.deleted_at IS NULL", $"{status} IN ({Db.InList(parameters, "st", statuses)})" };
        if (mode != -1)
        {
            where.Add("b.playmode = @mode");
            parameters["@mode"] = mode;
        }

        if (creator.Length > 0)
        {
            where.Add("bs.creator = @creator");
            parameters["@creator"] = creator;
        }

        if (artist.Length > 0)
        {
            where.Add("bs.artist LIKE @artist");
            parameters["@artist"] = "%" + artist + "%";
        }

        if (somtumOnly)
            where.Add($"b.beatmapset_id >= {CustomIdFloor.ToString(CultureInfo.InvariantCulture)}");

        // No DAN maps on TomYum.
        if (danOnly)
            where.Add("1 = 0");

        if (osuStatus is { } official)
        {
            // An official set's status on osu! (the website's own value for it); uploaded sets have none.
            where.Add($"b.beatmapset_id < {CustomIdFloor.ToString(CultureInfo.InvariantCulture)} AND bs.approved = @osu_status");
            parameters["@osu_status"] = official;
        }

        var selectExtra = string.Empty;
        if (q.Length > 0)
        {
            where.Add("(bs.artist LIKE @q OR bs.title LIKE @q OR bs.creator LIKE @q OR b.version LIKE @q)");
            parameters["@q"] = "%" + q + "%";
            if (sort == "relevance")
                selectExtra = ", SUM(CASE WHEN bs.title LIKE @q THEN 3 WHEN bs.artist LIKE @q THEN 2 ELSE 1 END) AS relevance";
        }

        var orderBy = sort == "relevance" && q.Length > 0
            ? "relevance DESC"
            : SearchSorts.TryGetValue(sort, out var known) ? known : SearchSorts["last_update_desc"];
        var whereSql = string.Join(" AND ", where);
        var countParameters = new Dictionary<string, object?>(parameters);
        parameters["@limit"] = limit;
        parameters["@offset"] = offset;

        var rows = await api.Db.QueryAsync(
            "SELECT CAST(b.beatmapset_id AS SIGNED) AS set_id, MIN(bs.artist) AS artist, MIN(bs.title) AS title, MIN(bs.creator) AS creator,"
            + $" CAST(MIN({status}) AS SIGNED) AS status, CAST(MAX(bs.approved) AS SIGNED) AS osu_status,"
            + " CAST(MAX(c.owner_id) AS SIGNED) AS uploaded_by_id, MAX(c.uploaded_at) AS uploaded_at, MAX(b.last_update) AS last_update"
            + selectExtra
            + $" FROM {sql.BeatmapFrom} LEFT JOIN {sql.T}.custom_beatmapsets c ON c.set_id = b.beatmapset_id"
            + $" WHERE {whereSql} GROUP BY b.beatmapset_id ORDER BY {orderBy}, b.beatmapset_id DESC LIMIT @limit OFFSET @offset",
            parameters,
            cancellationToken).ConfigureAwait(false);
        var total = await api.Db.ScalarAsync(
            $"SELECT COUNT(DISTINCT b.beatmapset_id) FROM {sql.BeatmapFrom} WHERE {whereSql}",
            countParameters,
            cancellationToken).ConfigureAwait(false);

        var setIds = rows.Select(r => r.Long("set_id")).ToList();
        var difficulties = new Dictionary<long, List<object?>>();
        var uploaders = new Dictionary<long, string>();
        if (setIds.Count > 0)
        {
            var diffParameters = new Dictionary<string, object?>();
            var diffRows = await api.Db.QueryAsync(
                "SELECT CAST(b.beatmap_id AS SIGNED) AS id, CAST(b.beatmapset_id AS SIGNED) AS set_id, b.version AS version,"
                + " CAST(b.playmode AS SIGNED) AS mode, b.difficultyrating AS diff, b.diff_approach AS ar, b.diff_overall AS od,"
                + $" b.diff_drain AS hp, b.diff_size AS cs FROM {sql.O}.osu_beatmaps b"
                + $" WHERE b.beatmapset_id IN ({Db.InList(diffParameters, "s", setIds)}) AND b.deleted_at IS NULL ORDER BY b.beatmap_id",
                diffParameters,
                cancellationToken).ConfigureAwait(false);
            foreach (var d in diffRows)
            {
                if (!difficulties.TryGetValue(d.Long("set_id"), out var list))
                    difficulties[d.Long("set_id")] = list = new List<object?>();
                list.Add(new JObj
                {
                    { "id", d.Long("id") },
                    { "version", d.Text("version") },
                    { "mode", d.Long("mode") },
                    { "diff", d.Double("diff") },
                    { "ar", d.Double("ar") },
                    { "od", d.Double("od") },
                    { "hp", d.Double("hp") },
                    { "cs", d.Double("cs") },
                });
            }

            var uploaderIds = rows.Where(r => !r.IsNull("uploaded_by_id")).Select(r => r.Long("uploaded_by_id")).Distinct().ToList();
            if (uploaderIds.Count > 0)
            {
                var userParameters = new Dictionary<string, object?>();
                var userRows = await api.Db.QueryAsync(
                    $"SELECT CAST(u.user_id AS SIGNED) AS id, u.username AS name FROM {sql.O}.phpbb_users u"
                    + $" WHERE u.user_id IN ({Db.InList(userParameters, "u", uploaderIds)}) AND {Sql.Visible("u")}",
                    userParameters,
                    cancellationToken).ConfigureAwait(false);
                foreach (var u in userRows)
                    uploaders[u.Long("id")] = u.Text("name");
            }
        }

        var domain = api.Domain(request);
        var sets = new List<object?>();
        foreach (var row in rows)
        {
            var setId = row.Long("set_id");
            var custom = setId >= CustomIdFloor;
            JObj? uploadedBy = null;
            if (row.LongOrNull("uploaded_by_id") is { } uploaderId && uploaders.TryGetValue(uploaderId, out var uploaderName))
            {
                uploadedBy = new JObj
                {
                    { "user_id", uploaderId },
                    { "name", uploaderName },
                    { "uploaded_at", row.Time("uploaded_at") is { } at ? JsonText.FormatDateTime(at) : null },
                };
            }

            var (preview, cover) = AssetUrls(domain, setId, custom);
            sets.Add(new JObj
            {
                { "set_id", setId },
                { "artist", row.Str("artist") },
                { "title", row.Str("title") },
                { "creator", row.Str("creator") },
                { "status", RankedStatus.SearchName(row.Long("status")) },
                { "osu_status", custom ? null : row.LongOrNull("osu_status") },
                { "somtum_only", custom },
                { "is_dan", false },
                { "uploaded_by", uploadedBy },
                { "last_update", row.Time("last_update") is { } updated ? JsonText.FormatDateTime(updated) : null },
                { "preview_url", preview },
                { "cover_url", cover },
                { "beatmaps", difficulties.TryGetValue(setId, out var diffs) ? diffs : new List<object?>() },
            });
        }

        return Respond.Success(new JObj
        {
            { "beatmapsets", sets },
            { "meta", new JObj { { "total", total }, { "offset", offset }, { "limit", limit } } },
        });
    }
}
