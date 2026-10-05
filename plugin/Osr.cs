// .osr replay files: the header bancho.py's /get_replay puts in front of a stable replay's frames,
// and reading the frames back out of a complete .osr (lazer's, as the spectator server saves them).

using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TomYum.Plugins.BpyApi;

/// <summary>What a replay header holds.</summary>
internal sealed record ReplayHeader(
    int Mode,
    string MapMd5,
    string Username,
    int N300,
    int N100,
    int N50,
    int NGeki,
    int NKatu,
    int NMiss,
    int Score,
    int MaxCombo,
    bool Perfect,
    int Mods,
    DateTime PlayedAt,
    long ScoreId);

internal static class Osr
{
    /// <summary>The client version bancho.py writes into replays it builds.</summary>
    public const int Version = 20200207;

    /// <summary>
    /// bancho.py's replay checksum: md5 of "{n100+n300}p{n50}o{geki}o{katu}t{miss}a{map}r{combo}e{perfect}y{name}o{score}u0{mods}True".
    /// </summary>
    public static string Checksum(ReplayHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{header.N100 + header.N300}p{header.N50}o{header.NGeki}o{header.NKatu}t{header.NMiss}a{header.MapMd5}r{header.MaxCombo}e{(header.Perfect ? "True" : "False")}y{header.Username}o{header.Score}u0{header.Mods}True");
#pragma warning disable CA5351 // osu!'s replay checksum is md5; it isn't used for security
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
#pragma warning restore CA5351
    }

    /// <summary>A complete .osr: bancho.py's header, the frames as stored, and the score id.</summary>
    public static byte[] Build(ReplayHeader header, ReadOnlySpan<byte> frames)
    {
        ArgumentNullException.ThrowIfNull(header);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)header.Mode);
            writer.Write(Version);
            WriteString(writer, header.MapMd5);
            WriteString(writer, header.Username);
            WriteString(writer, Checksum(header));
            writer.Write((short)header.N300);
            writer.Write((short)header.N100);
            writer.Write((short)header.N50);
            writer.Write((short)header.NGeki);
            writer.Write((short)header.NKatu);
            writer.Write((short)header.NMiss);
            writer.Write(header.Score);
            writer.Write((short)header.MaxCombo);
            writer.Write((byte)(header.Perfect ? 1 : 0));
            writer.Write(header.Mods);
            writer.Write((byte)0); // no life bar graph
            // .NET ticks are what osu! stores (bancho.py adds the Unix epoch's ticks to a Unix time).
            writer.Write(DateTime.SpecifyKind(header.PlayedAt, DateTimeKind.Utc).Ticks);
            writer.Write(frames.Length);
            writer.Write(frames);
            writer.Write(header.ScoreId);
        }

        return stream.ToArray();
    }

    /// <summary>An osu! string: 0x00 for an empty one, else 0x0b, the ULEB128 length and the UTF-8 bytes.</summary>
    public static void WriteString(BinaryWriter writer, string value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (string.IsNullOrEmpty(value))
        {
            writer.Write((byte)0);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((byte)0x0b);
        var length = (uint)bytes.Length;
        do
        {
            var b = (byte)(length & 0x7f);
            length >>= 7;
            if (length != 0)
                b |= 0x80;
            writer.Write(b);
        }
        while (length != 0);
        writer.Write(bytes);
    }

    /// <summary>The compressed frames of a complete .osr (what bancho.py stores and serves without headers), or null when it can't be read.</summary>
    public static byte[]? Frames(ReadOnlySpan<byte> osr)
    {
        try
        {
            var position = 1 + 4; // mode, version
            for (var i = 0; i < 3; i++)
                SkipString(osr, ref position); // map md5, player, replay md5
            position += 6 * 2 + 4 + 2 + 1 + 4; // hit counts, score, combo, perfect, mods
            SkipString(osr, ref position); // life bar
            position += 8; // time
            var length = BinaryPrimitives.ReadInt32LittleEndian(osr.Slice(position, 4));
            position += 4;
            if (length < 0 || position + length > osr.Length)
                return null;
            return osr.Slice(position, length).ToArray();
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static void SkipString(ReadOnlySpan<byte> data, ref int position)
    {
        var marker = data[position++];
        if (marker == 0)
            return;
        if (marker != 0x0b)
            throw new ArgumentOutOfRangeException(nameof(data), "Not an osu! string.");

        var length = 0;
        var shift = 0;
        byte b;
        do
        {
            b = data[position++];
            length |= (b & 0x7f) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);
        position += length;
        if (position > data.Length)
            throw new ArgumentOutOfRangeException(nameof(data), "The string runs past the end.");
    }

    /// <summary>Python's <c>urllib.parse.quote</c> (unreserved characters and '/' kept), for Content-Disposition.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~' or '/')
                builder.Append(c);
            else
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>bancho.py's replay file name: "{player} - {artist} - {title} [{version}] ({date}).osr".</summary>
    public static string FileName(string username, string artist, string title, string version, DateTime playedAt) =>
        string.Create(CultureInfo.InvariantCulture, $"{username} - {artist} - {title} [{version}] ({playedAt:yyyy-MM-dd}).osr");
}
