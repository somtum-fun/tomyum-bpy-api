// bancho.py's constants (mods, game modes, privileges, ranked statuses) and how TomYum's and the
// website's data map onto them.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TomYum.Plugins.BpyApi;

/// <summary>bancho.py's legacy mod bitmask, its "HDDT" strings, and lazer's mod acronyms.</summary>
internal static class Mods
{
    public const long Relax = 1 << 7;
    public const long Autopilot = 1 << 13;

    /// <summary>bancho.py's <c>mod2modstr_dict</c>, in bit order (the order <c>repr(Mods)</c> writes them).</summary>
    private static readonly (long Bit, string Name)[] Names =
    [
        (1 << 0, "NF"), (1 << 1, "EZ"), (1 << 2, "TD"), (1 << 3, "HD"), (1 << 4, "HR"), (1 << 5, "SD"),
        (1 << 6, "DT"), (1 << 7, "RX"), (1 << 8, "HT"), (1 << 9, "NC"), (1 << 10, "FL"), (1 << 11, "AU"),
        (1 << 12, "SO"), (1 << 13, "AP"), (1 << 14, "PF"), (1 << 15, "4K"), (1 << 16, "5K"), (1 << 17, "6K"),
        (1 << 18, "7K"), (1 << 19, "8K"), (1 << 20, "FI"), (1 << 21, "RN"), (1 << 22, "CN"), (1 << 23, "TP"),
        (1 << 24, "9K"), (1 << 25, "CO"), (1 << 26, "1K"), (1 << 27, "3K"), (1 << 28, "2K"), (1 << 29, "V2"),
        (1 << 30, "MR"),
    ];

    /// <summary>
    /// Lazer mod acronyms (a score's <c>data.mods</c>) and their legacy bits, as osu!'s own conversion
    /// does it: NC and PF carry DT and SD too, like stable sends them. Mods with no legacy bit are left out.
    /// </summary>
    public static readonly IReadOnlyList<(string Acronym, long Bits)> LazerAcronyms =
    [
        ("NF", 1), ("EZ", 2), ("TD", 4), ("HD", 8), ("HR", 16), ("SD", 32), ("DT", 64), ("RX", 128),
        ("HT", 256), ("DC", 256), ("NC", 512 | 64), ("FL", 1024), ("AT", 2048), ("SO", 4096), ("AP", 8192),
        ("PF", 16384 | 32), ("4K", 1 << 15), ("5K", 1 << 16), ("6K", 1 << 17), ("7K", 1 << 18), ("8K", 1 << 19),
        ("FI", 1 << 20), ("RD", 1 << 21), ("CN", 1 << 22), ("TP", 1 << 23), ("9K", 1 << 24), ("1K", 1 << 26),
        ("3K", 1 << 27), ("2K", 1 << 28), ("SV2", 1 << 29), ("MR", 1 << 30),
    ];

    /// <summary><c>repr(Mods(value))</c> in bancho.py: "NM", or the two-letter names in bit order.</summary>
    public static string Readable(long mods)
    {
        if (mods == 0)
            return "NM";

        var builder = new StringBuilder();
        foreach (var (bit, name) in Names)
        {
            if ((mods & bit) != 0)
                builder.Append(name);
        }

        return builder.ToString();
    }

    /// <summary><c>Mods.from_modstr</c>: two-letter chunks, unknown ones skipped.</summary>
    public static long FromModString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        long mods = 0;
        for (var i = 0; i < text.Length; i += 2)
        {
            var chunk = text.Substring(i, Math.Min(2, text.Length - i)).ToUpperInvariant();
            foreach (var (bit, name) in Names)
            {
                if (name == chunk)
                {
                    mods |= bit;
                    break;
                }
            }
        }

        return mods;
    }

    /// <summary>The legacy bitmask of lazer mod acronyms.</summary>
    public static long FromAcronyms(IEnumerable<string> acronyms)
    {
        ArgumentNullException.ThrowIfNull(acronyms);
        long mods = 0;
        foreach (var acronym in acronyms)
        {
            foreach (var (name, bits) in LazerAcronyms)
            {
                if (string.Equals(name, acronym, StringComparison.OrdinalIgnoreCase))
                    mods |= bits;
            }
        }

        return mods;
    }

    /// <summary>
    /// The <c>mods</c> filter of /get_player_scores and /get_map_scores: an optional "=" (all of these
    /// mods, the default) or "~" (any of them), then a number or a mod string. Null without a filter.
    /// </summary>
    public static (long Mods, bool Strict)? ParseFilter(string? raw)
    {
        if (raw is null)
            return null;

        var strict = true;
        if (raw.Length > 0 && (raw[0] == '~' || raw[0] == '='))
        {
            strict = raw[0] == '=';
            raw = raw[1..];
        }

        if (raw.Length > 0 && raw.All(char.IsAsciiDigit) && long.TryParse(raw, out var number))
            return (number, strict);
        return (FromModString(raw), strict);
    }
}

/// <summary>bancho.py's game modes: vanilla 0-3, Relax 4-6 (7 unused), Autopilot 8 (9-11 unused).</summary>
internal static class GameModes
{
    /// <summary>The modes bancho.py keeps stats for (and TomYum: vanilla on the website, the rest in TomYum).</summary>
    public static readonly int[] Stats = [0, 1, 2, 3, 4, 5, 6, 8];

    /// <summary>Modes bancho.py answers "Invalid gamemode." for (RX mania, AP taiko/catch/mania).</summary>
    public static bool IsUnused(long mode) => mode is 7 or 9 or 10 or 11;

    /// <summary>Vanilla modes come from the website's scores and stats; Relax/Autopilot from TomYum's.</summary>
    public static bool IsVanilla(long mode) => mode is >= 0 and <= 3;

    public static int Vanilla(long mode) => (int)(mode % 4);
}

/// <summary>bancho.py's beatmap <c>RankedStatus</c> (the same values TomYum stores).</summary>
internal static class RankedStatus
{
    /// <summary>
    /// SQL for a beatmap's status: TomYum's own (the game server's, staff changes included) when it
    /// has the map, else the website's <c>approved</c> converted (osu!'s values are one lower).
    /// </summary>
    public static string Sql(string tomyumAlias, string osuWebAlias) =>
        $"COALESCE({tomyumAlias}.Status, CASE {osuWebAlias}.approved WHEN 1 THEN 2 WHEN 2 THEN 3 WHEN 3 THEN 4 WHEN 4 THEN 5 ELSE 0 END)";

    /// <summary>/search_beatmaps' status names (bancho.py has no graveyard or WIP: they collapse).</summary>
    public static readonly IReadOnlyList<(string Name, int Value)> SearchNames =
    [
        ("pending", 0), ("graveyard", 0), ("wip", 1), ("ranked", 2), ("approved", 3), ("qualified", 4), ("loved", 5),
    ];

    /// <summary>The name /search_beatmaps shows for a status (the last name with that value, like bancho.py's dict inversion).</summary>
    public static string SearchName(long status)
    {
        string? found = null;
        foreach (var (name, value) in SearchNames)
        {
            if (value == status)
                found = name;
        }

        return found ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>bancho.py's privilege bits, and the website's groups each one stands for.</summary>
internal static class Privileges
{
    public const long Unrestricted = 1 << 0;
    public const long Verified = 1 << 1;
    public const long Whitelisted = 1 << 2;
    public const long Supporter = 1 << 4;
    public const long Premium = 1 << 5;
    public const long Alumni = 1 << 7;
    public const long TourneyManager = 1 << 10;
    public const long Nominator = 1 << 11;
    public const long Moderator = 1 << 12;
    public const long Administrator = 1 << 13;
    public const long Developer = 1 << 14;
    public const long Nat = 1 << 15;
    public const long Voter = 1 << 16;
    public const long Helper = 1 << 17;

    /// <summary>osu-web group identifiers and the bancho.py bit each gives.</summary>
    public static readonly IReadOnlyList<(string Group, long Bit)> Groups =
    [
        ("alumni", Alumni), ("tc", TourneyManager), ("bng", Nominator), ("bng_limited", Nominator),
        ("gmt", Moderator), ("admin", Administrator), ("dev", Developer), ("nat", Nat), ("spt", Helper),
    ];

    /// <summary>/special_players' role labels, in bancho.py's order.</summary>
    public static readonly IReadOnlyList<(string Label, long Bit)> PublicRoles =
    [
        ("helper", Helper), ("developer", Developer), ("administrator", Administrator), ("moderator", Moderator),
        ("nat", Nat), ("nominator", Nominator), ("premium", Premium), ("supporter", Supporter),
    ];

    public static List<string> RoleLabels(long priv) =>
        PublicRoles.Where(r => (priv & r.Bit) != 0).Select(r => r.Label).ToList();
}
