// JSON output shaped like bancho.py's (FastAPI + orjson): keys in the order bancho.py builds them,
// whole floats written as 95.0, datetimes as naive ISO 8601, non-ASCII text left as UTF-8.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TomYum.Plugins.BpyApi;

/// <summary>A JSON object that keeps its keys in insertion order (like a Python dict).</summary>
internal sealed class JObj : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly List<KeyValuePair<string, object?>> _items = new();

    public int Count => _items.Count;

    /// <summary>Sets <paramref name="key"/>: replaces an existing value in place, else appends.</summary>
    public object? this[string key]
    {
        get
        {
            foreach (var item in _items)
            {
                if (item.Key == key)
                    return item.Value;
            }

            return null;
        }
        set
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].Key == key)
                {
                    _items[i] = new KeyValuePair<string, object?>(key, value);
                    return;
                }
            }

            _items.Add(new KeyValuePair<string, object?>(key, value));
        }
    }

    public void Add(string key, object? value) => this[key] = value;

    public bool Remove(string key) => _items.RemoveAll(i => i.Key == key) > 0;

    public bool ContainsKey(string key) => _items.Exists(i => i.Key == key);

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Writes <see cref="JObj"/> trees (and plain values, lists, dictionaries) as UTF-8 JSON.</summary>
internal static class JsonText
{
    private static readonly JsonWriterOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Serialize(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
            Write(writer, value);
        return stream.ToArray();
    }

    public static string SerializeToString(object? value) => Encoding.UTF8.GetString(Serialize(value));

    /// <summary>A naive ISO 8601 time as orjson writes Python's datetime (microseconds only when there are some).</summary>
    public static string FormatDateTime(DateTime value)
    {
        var text = value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var micros = (value.Ticks % TimeSpan.TicksPerSecond) / 10;
        return micros == 0 ? text : text + "." + micros.ToString("000000", CultureInfo.InvariantCulture);
    }

    /// <summary>A float as Python's repr writes it: shortest round-trip form, and 95.0 for whole numbers.</summary>
    public static string FormatDouble(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (text.Contains('E', StringComparison.Ordinal))
            return text.Replace("E+", "e+", StringComparison.Ordinal).Replace("E-", "e-", StringComparison.Ordinal);
        return text.Contains('.', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int or long or short or sbyte or byte or ushort or uint:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong u:
                writer.WriteNumberValue(u);
                break;
            case double d:
                WriteDouble(writer, d);
                break;
            case float f:
                WriteDouble(writer, double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                break;
            case decimal m:
                WriteDouble(writer, (double)m);
                break;
            case DateTime dt:
                writer.WriteStringValue(FormatDateTime(dt));
                break;
            case JObj obj:
                writer.WriteStartObject();
                foreach (var (key, item) in obj)
                {
                    writer.WritePropertyName(key);
                    Write(writer, item);
                }

                writer.WriteEndObject();
                break;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    writer.WritePropertyName(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                    Write(writer, entry.Value);
                }

                writer.WriteEndObject();
                break;
            case IEnumerable list:
                writer.WriteStartArray();
                foreach (var item in list)
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static void WriteDouble(Utf8JsonWriter writer, double value)
    {
        // orjson writes NaN and infinities as null.
        if (double.IsNaN(value) || double.IsInfinity(value))
            writer.WriteNullValue();
        else
            writer.WriteRawValue(FormatDouble(value), skipInputValidation: true);
    }
}
