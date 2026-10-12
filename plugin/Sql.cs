// The SQL that maps TomYum's and the website's tables onto bancho.py's `users`, `scores`, `maps`
// and `stats`. Only fixed fragments and validated database names are spliced in.
//
// Where things live:
//   - accounts: the website's phpbb_users; restricted (user_warnings) and deleted (user_type 1)
//     accounts are left out of everything public, like the website does;
//   - vanilla (modes 0-3): the website's `scores` (stable plays mirrored there with a
//     legacy_score_id, lazer plays natively) and osu_user_stats*; Relax/Autopilot plays in it
//     (lazer RX/AP) are left out, as on the website's vanilla boards;
//   - Relax/Autopilot (modes 4-6, 8): TomYum's own `scores` and `user_stats`;
//   - beatmaps: the website's osu_beatmaps/osu_beatmapsets, with TomYum's ranked status.

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TomYum.Plugins.BpyApi;

internal sealed class Sql
{
    private static readonly Regex DatabaseName = new("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant);

    public Sql(string osuWebDatabase, string tomyumDatabase, long modeScoreIdOffset, bool hasAccountFlags = true)
    {
        if (!DatabaseName.IsMatch(osuWebDatabase))
            throw new ArgumentException($"'{osuWebDatabase}' isn't a plain database name.", nameof(osuWebDatabase));
        if (!DatabaseName.IsMatch(tomyumDatabase))
            throw new ArgumentException($"'{tomyumDatabase}' isn't a plain database name.", nameof(tomyumDatabase));
        if (modeScoreIdOffset <= 0)
            throw new ArgumentOutOfRangeException(nameof(modeScoreIdOffset), "The Relax/Autopilot score id offset must be positive.");

        O = "`" + osuWebDatabase + "`";
        T = "`" + tomyumDatabase + "`";
        ModeScoreIdOffset = modeScoreIdOffset;
        HasAccountFlags = hasAccountFlags;
    }

    /// <summary>
    /// The rankings' inactivity rule, as TomYum's (<c>Ranking:InactiveDays</c>): a player without a score in
    /// a mode for this many days isn't ranked in it and doesn't count towards anyone else's rank. 0 = off.
    /// </summary>
    public int InactiveDays { get; init; }

    /// <summary>" AND (the vanilla stats row <paramref name="alias"/> played lately)", or nothing when the rule is off.</summary>
    public string VanillaActive(string alias) =>
        InactiveDays > 0 ? $" AND {alias}.last_played >= UTC_TIMESTAMP() - INTERVAL {InactiveDays.ToString(CultureInfo.InvariantCulture)} DAY" : string.Empty;

    /// <summary>" AND (the Relax/Autopilot stats row <paramref name="alias"/> has a recent score)", or nothing when the rule is off.</summary>
    public string ModeActive(string alias) =>
        InactiveDays > 0
            ? $" AND EXISTS (SELECT 1 FROM {T}.scores a WHERE a.UserId = {alias}.UserId AND a.Mode = {alias}.Mode"
              + $" AND a.SubmittedAt >= UTC_TIMESTAMP() - INTERVAL {InactiveDays.ToString(CultureInfo.InvariantCulture)} DAY)"
            : string.Empty;

    /// <summary>The website's database, quoted.</summary>
    public string O { get; }

    /// <summary>TomYum's database, quoted.</summary>
    public string T { get; }

    /// <summary>Added to TomYum's Relax/Autopilot score ids so they never meet the website's score ids.</summary>
    public long ModeScoreIdOffset { get; }

    /// <summary>Whether the website has TomYum's <c>tomyum_account_flags</c> (the anti-cheat whitelist).</summary>
    public bool HasAccountFlags { get; set; }

    private string Offset => ModeScoreIdOffset.ToString(CultureInfo.InvariantCulture);

    /// <summary>A player everyone can see: not restricted, not deleted.</summary>
    public static string Visible(string u) => $"{u}.user_type <> 1 AND {u}.user_warnings = 0";

    /// <summary>bancho.py stores countries lower-case ("th"); the website upper-case.</summary>
    public static string Country(string u) => $"LOWER({u}.country_acronym)";

    /// <summary>Whether the account is a chat bot (BanchoBot and the staff panel's bots: the website's bot group).</summary>
    public string IsBot(string userId) =>
        $"EXISTS (SELECT 1 FROM {O}.phpbb_user_group bg JOIN {O}.phpbb_groups bgg ON bgg.group_id = bg.group_id WHERE bg.user_id = {userId} AND bgg.identifier = 'bot')";

    /// <summary>
    /// bancho.py's <c>priv</c> for a website account: UNRESTRICTED|VERIFIED when visible, SUPPORTER for
    /// supporters, WHITELISTED for TomYum's anti-cheat whitelist, and the staff bits from its groups.
    /// </summary>
    public string Priv(string u)
    {
        var groups = new StringBuilder();
        foreach (var (group, bit) in Privileges.Groups)
            groups.Append(CultureInfo.InvariantCulture, $" WHEN '{group}' THEN {bit}");

        var priv = $"((CASE WHEN {Visible(u)} THEN {Privileges.Unrestricted | Privileges.Verified} ELSE 0 END)"
            + $" | (CASE WHEN {u}.osu_subscriber = 1 THEN {Privileges.Supporter} ELSE 0 END)"
            + $" | COALESCE((SELECT BIT_OR(CASE pg.identifier{groups} ELSE 0 END) FROM {O}.phpbb_user_group pug"
            + $" JOIN {O}.phpbb_groups pg ON pg.group_id = pug.group_id WHERE pug.user_id = {u}.user_id AND pug.user_pending = 0), 0)";
        if (HasAccountFlags)
        {
            priv += $" | (CASE WHEN EXISTS (SELECT 1 FROM {O}.tomyum_account_flags af WHERE af.user_id = {u}.user_id"
                + $" AND af.whitelisted_at IS NOT NULL) THEN {Privileges.Whitelisted} ELSE 0 END)";
        }

        return "CAST(" + priv + ") AS SIGNED)";
    }

    /// <summary>1 when a website score was played with Relax or Autopilot (lazer's mods).</summary>
    public static string RelaxPlay(string s) =>
        $"COALESCE(JSON_OVERLAPS(JSON_EXTRACT({s}.data, '$.mods[*].acronym'), '[\"RX\",\"AP\"]'), 0)";

    /// <summary>The legacy mod bitmask of a website score's lazer mods.</summary>
    public static string LazerMods(string s)
    {
        var terms = Mods.LazerAcronyms.Select(m =>
            $"COALESCE(JSON_CONTAINS(JSON_EXTRACT({s}.data, '$.mods[*].acronym'), '\"{m.Acronym}\"'), 0) * {m.Bits.ToString(CultureInfo.InvariantCulture)}");
        // Bits OR'd, not added: NC brings DT along, and a play could list both.
        return "(" + string.Join(" | ", terms) + ")";
    }

    private static string Stat(string s, string name) =>
        $"COALESCE(JSON_VALUE({s}.data, '$.statistics.{name}' RETURNING SIGNED), 0)";

    /// <summary>
    /// A LATERAL join (alias ob) with a row when website score <paramref name="s"/> is NOT its player's
    /// best on the map in its ruleset (bancho.py's status 2): a better passed vanilla play by the same
    /// player (pp, then score, then the older one). A lateral join rather than a subquery in the select
    /// list, so MySQL can still merge the score source into the outer query and use its indexes.
    /// </summary>
    public string BetterPlay(string s) =>
        $" LEFT JOIN LATERAL (SELECT 1 AS better FROM {O}.scores o WHERE o.beatmap_id = {s}.beatmap_id AND o.user_id = {s}.user_id"
        + $" AND o.ruleset_id = {s}.ruleset_id AND o.passed = 1 AND o.id <> {s}.id AND NOT {RelaxPlay("o")}"
        + $" AND (COALESCE(o.pp, 0) > COALESCE({s}.pp, 0) OR (COALESCE(o.pp, 0) = COALESCE({s}.pp, 0)"
        + $" AND (o.total_score > {s}.total_score OR (o.total_score = {s}.total_score AND o.id < {s}.id)))) LIMIT 1) ob ON TRUE";

    /// <summary>
    /// Vanilla scores in bancho.py's <c>scores</c> shape, from the website's table. A stable play takes
    /// its exact numbers (score, mods, hit counts) from TomYum's row; a lazer play's come from its data,
    /// converted the way the website converts them for osu!'s legacy API. Extra columns: sort_score
    /// (the website's ordering), map_status, replay_source and replay_key.
    /// </summary>
    public string VanillaScores =>
        "SELECT CAST(s.id AS SIGNED) AS id, s.id AS raw_id, s.user_id AS userid, s.beatmap_id AS beatmap_id, b.checksum AS map_md5,"
        + " CAST(COALESCE(t.ScoreValue, IF(s.legacy_score_id IS NULL, s.total_score, s.legacy_total_score)) AS SIGNED) AS score,"
        + " s.total_score AS sort_score,"
        + " ROUND(COALESCE(s.pp, 0), 3) AS pp,"
        + " ROUND(s.accuracy * 100, 4) AS acc,"
        + " CAST(s.max_combo AS SIGNED) AS max_combo,"
        + $" CAST(COALESCE(t.Mods, {LazerMods("s")}) AS SIGNED) AS mods,"
        + $" CAST(COALESCE(t.Count300, {Stat("s", "great")}) AS SIGNED) AS n300,"
        + $" CAST(COALESCE(t.Count100, IF(s.ruleset_id = 2, {Stat("s", "large_tick_hit")}, {Stat("s", "ok")})) AS SIGNED) AS n100,"
        + $" CAST(COALESCE(t.Count50, CASE s.ruleset_id WHEN 1 THEN 0 WHEN 2 THEN {Stat("s", "small_tick_hit")} ELSE {Stat("s", "meh")} END) AS SIGNED) AS n50,"
        + $" CAST(COALESCE(t.CountMiss, {Stat("s", "miss")}) AS SIGNED) AS nmiss,"
        + $" CAST(COALESCE(t.CountGeki, IF(s.ruleset_id = 3, {Stat("s", "perfect")}, 0)) AS SIGNED) AS ngeki,"
        + $" CAST(COALESCE(t.CountKatu, CASE s.ruleset_id WHEN 2 THEN {Stat("s", "small_tick_miss")} WHEN 3 THEN {Stat("s", "good")} ELSE 0 END) AS SIGNED) AS nkatu,"
        + " IF(s.passed = 1, s.`rank`, 'F') AS grade,"
        + " CAST(CASE WHEN s.passed = 0 THEN 0 WHEN ob.better IS NULL THEN 2 ELSE 1 END AS SIGNED) AS status,"
        + " s.ruleset_id AS mode,"
        + " s.ended_at AS play_time,"
        + " CAST(COALESCE(TIMESTAMPDIFF(MICROSECOND, s.started_at, s.ended_at) DIV 1000, 0) AS SIGNED) AS time_elapsed,"
        + $" CAST(COALESCE(t.Perfect, {Stat("s", "miss")} = 0 AND s.max_combo >= b.max_combo) AS SIGNED) AS perfect,"
        + $" CAST({RankedStatus.Sql("tb", "b")} AS SIGNED) AS map_status,"
        + " IF(s.legacy_score_id IS NULL, 'lazer', 'stable') AS replay_source,"
        + " CAST(COALESCE(s.legacy_score_id, s.id) AS SIGNED) AS replay_key"
        + $" FROM {O}.scores s"
        + $" JOIN {O}.osu_beatmaps b ON b.beatmap_id = s.beatmap_id"
        + $" LEFT JOIN {T}.beatmaps tb ON tb.Id = b.beatmap_id"
        + $" LEFT JOIN {T}.scores t ON t.Id = s.legacy_score_id"
        + BetterPlay("s")
        + $" WHERE NOT {RelaxPlay("s")}";

    /// <summary>
    /// Relax/Autopilot scores in the same shape, from TomYum's table (stable plays and the lazer RX/AP
    /// plays TomYum took in). Ids are offset by <see cref="ModeScoreIdOffset"/>. A lazer play's
    /// accuracy is the website's (it counts slider ticks); a stable play's is worked out like bancho.py.
    /// </summary>
    public string ModeScores =>
        $"SELECT {Offset} + t.Id AS id, t.Id AS raw_id, t.UserId AS userid, t.BeatmapId AS beatmap_id, t.BeatmapMd5 AS map_md5,"
        + " t.ScoreValue AS score, t.ScoreValue AS sort_score,"
        + " ROUND(IF(t.PpHeld, 0, t.Pp), 3) AS pp,"
        + $" ROUND(COALESCE(w.accuracy * 100, {Accuracy("t")}, 0), 4) AS acc,"
        + " CAST(t.MaxCombo AS SIGNED) AS max_combo, CAST(t.Mods AS SIGNED) AS mods,"
        + " CAST(t.Count300 AS SIGNED) AS n300, CAST(t.Count100 AS SIGNED) AS n100, CAST(t.Count50 AS SIGNED) AS n50,"
        + " CAST(t.CountMiss AS SIGNED) AS nmiss, CAST(t.CountGeki AS SIGNED) AS ngeki, CAST(t.CountKatu AS SIGNED) AS nkatu,"
        + " t.Grade AS grade, t.Status AS status, t.Mode AS mode,"
        + " t.SubmittedAt AS play_time,"
        + " CAST(COALESCE(TIMESTAMPDIFF(MICROSECOND, w.started_at, w.ended_at) DIV 1000, 0) AS SIGNED) AS time_elapsed,"
        + " CAST(t.Perfect AS SIGNED) AS perfect,"
        + $" CAST({RankedStatus.Sql("tb", "b")} AS SIGNED) AS map_status,"
        + " IF(t.FromLazer, 'lazer', 'stable') AS replay_source,"
        + " CAST(IF(t.FromLazer, COALESCE(t.OsuWebScoreId, 0), t.Id) AS SIGNED) AS replay_key"
        + $" FROM {T}.scores t"
        + $" JOIN {O}.osu_beatmaps b ON b.beatmap_id = t.BeatmapId"
        + $" LEFT JOIN {T}.beatmaps tb ON tb.Id = b.beatmap_id"
        + $" LEFT JOIN {O}.scores w ON t.FromLazer AND w.id = t.OsuWebScoreId"
        + " WHERE t.Mode >= 4";

    /// <summary>The score source for <paramref name="mode"/>.</summary>
    public string ScoresFor(long mode) => GameModes.IsVanilla(mode) ? VanillaScores : ModeScores;

    /// <summary>Accuracy (percent) from a TomYum score's hit counts, per ruleset, as bancho.py works it out.</summary>
    public static string Accuracy(string t) =>
        $"(CASE {t}.Mode % 4"
        + $" WHEN 0 THEN (300 * {t}.Count300 + 100 * {t}.Count100 + 50 * {t}.Count50) / NULLIF(300 * ({t}.Count300 + {t}.Count100 + {t}.Count50 + {t}.CountMiss), 0)"
        + $" WHEN 1 THEN ({t}.Count300 + 0.5 * {t}.Count100) / NULLIF({t}.Count300 + {t}.Count100 + {t}.CountMiss, 0)"
        + $" WHEN 2 THEN ({t}.Count300 + {t}.Count100 + {t}.Count50) / NULLIF({t}.Count300 + {t}.Count100 + {t}.Count50 + {t}.CountKatu + {t}.CountMiss, 0)"
        + $" ELSE (300 * ({t}.Count300 + {t}.CountGeki) + 200 * {t}.CountKatu + 100 * {t}.Count100 + 50 * {t}.Count50)"
        + $" / NULLIF(300 * ({t}.Count300 + {t}.Count100 + {t}.Count50 + {t}.CountGeki + {t}.CountKatu + {t}.CountMiss), 0)"
        + " END) * 100";

    /// <summary>bancho.py's <c>Beatmap.as_dict</c> columns (alias b = osu_beatmaps, bs = osu_beatmapsets, tb = TomYum's beatmaps).</summary>
    public static string BeatmapColumns =>
        "b.checksum AS md5, CAST(b.beatmap_id AS SIGNED) AS id, CAST(b.beatmapset_id AS SIGNED) AS set_id,"
        + " bs.artist AS artist, bs.title AS title, b.version AS version, bs.creator AS creator, b.last_update AS last_update,"
        + " CAST(b.total_length AS SIGNED) AS total_length, CAST(b.max_combo AS SIGNED) AS max_combo,"
        + $" CAST({RankedStatus.Sql("tb", "b")} AS SIGNED) AS status,"
        + " CAST(b.playcount AS SIGNED) AS plays, CAST(b.passcount AS SIGNED) AS passes, CAST(b.playmode AS SIGNED) AS mode,"
        + " b.bpm AS bpm, b.diff_size AS cs, b.diff_overall AS od, b.diff_approach AS ar, b.diff_drain AS hp, b.difficultyrating AS diff";

    public string BeatmapFrom =>
        $"{O}.osu_beatmaps b JOIN {O}.osu_beatmapsets bs ON bs.beatmapset_id = b.beatmapset_id LEFT JOIN {T}.beatmaps tb ON tb.Id = b.beatmap_id";

    /// <summary>A beatmap row as bancho.py's <c>Beatmap.as_dict</c> (no DAN maps here: <c>is_dan</c> is always false).</summary>
    public static JObj BeatmapJson(Row row)
    {
        var map = row.Pick(
            "md5", "id", "set_id", "artist", "title", "version", "creator", "last_update", "total_length", "max_combo",
            "status", "plays", "passes", "mode", "bpm", "cs", "od", "ar", "hp", "diff");
        map.Add("is_dan", false);
        return map;
    }
}
