// Responses and query-string parsing that behave like bancho.py's FastAPI routes: the same status
// codes and bodies, and FastAPI's 422 {"detail": [...]} for parameters that don't validate.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.BpyApi;

internal static class Respond
{
    public const string JsonType = "application/json";

    public static PluginHttpResponse Json(object body, int statusCode = 200, IReadOnlyDictionary<string, string>? headers = null) =>
        new(statusCode, JsonType, JsonText.Serialize(body), headers);

    /// <summary>bancho.py's usual error: <c>{"status": "..."}</c>.</summary>
    public static PluginHttpResponse Status(int statusCode, string message) =>
        Json(new JObj { { "status", message } }, statusCode);

    public static PluginHttpResponse Success(JObj body)
    {
        var result = new JObj { { "status", "success" } };
        foreach (var (key, value) in body)
            result.Add(key, value);
        return Json(result);
    }

    /// <summary>FastAPI's answer to a path it doesn't have.</summary>
    public static PluginHttpResponse NotFound() => Json(new JObj { { "detail", "Not Found" } }, 404);

    /// <summary>FastAPI's answer to a known path with another method.</summary>
    public static PluginHttpResponse MethodNotAllowed() => Json(new JObj { { "detail", "Method Not Allowed" } }, 405);

    public static PluginHttpResponse InternalError() =>
        new(500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Internal Server Error"));

    public static PluginHttpResponse Bytes(byte[] body, string contentType, IReadOnlyDictionary<string, string>? headers = null) =>
        new(200, contentType, body, headers);
}

/// <summary>
/// Reads query parameters with FastAPI's rules (ints, bools, literals, length and pattern limits) and
/// collects its validation errors, so a bad request gets the same 422 body bancho.py sends.
/// </summary>
internal sealed class QueryArgs
{
    private static readonly string[] TrueWords = ["1", "true", "t", "yes", "y", "on"];
    private static readonly string[] FalseWords = ["0", "false", "f", "no", "n", "off"];

    private readonly IReadOnlyDictionary<string, string> _query;
    private readonly List<JObj> _errors = new();

    public QueryArgs(IReadOnlyDictionary<string, string> query) => _query = query;

    public bool HasErrors => _errors.Count > 0;

    public IReadOnlyList<JObj> Errors => _errors;

    /// <summary>The 422 response for the errors collected so far.</summary>
    public PluginHttpResponse ErrorResponse() => Respond.Json(new JObj { { "detail", _errors.ToList() } }, 422);

    public bool Has(string name) => _query.ContainsKey(name);

    /// <summary>An optional integer (<paramref name="fallback"/> when absent).</summary>
    public long? Int(string name, long? fallback = null, long? ge = null, long? le = null)
    {
        if (!_query.TryGetValue(name, out var raw))
            return fallback;

        if (!long.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            Error("int_parsing", name, "Input should be a valid integer, unable to parse string as an integer", raw);
            return fallback;
        }

        if (ge is { } min && value < min)
        {
            Error("greater_than_equal", name, $"Input should be greater than or equal to {min.ToString(CultureInfo.InvariantCulture)}", raw, new JObj { { "ge", min } });
            return fallback;
        }

        if (le is { } max && value > max)
        {
            Error("less_than_equal", name, $"Input should be less than or equal to {max.ToString(CultureInfo.InvariantCulture)}", raw, new JObj { { "le", max } });
            return fallback;
        }

        return value;
    }

    /// <summary>A required integer.</summary>
    public long RequiredInt(string name, long? ge = null, long? le = null)
    {
        if (!_query.ContainsKey(name))
        {
            Missing(name);
            return 0;
        }

        return Int(name, null, ge, le) ?? 0;
    }

    /// <summary>An optional string with FastAPI's length and pattern checks.</summary>
    public string? Str(string name, string? fallback = null, int? minLength = null, int? maxLength = null, Regex? pattern = null)
    {
        if (!_query.TryGetValue(name, out var raw))
            return fallback;

        if (minLength is { } min && raw.Length < min)
        {
            var unit = min == 1 ? "character" : "characters";
            Error("string_too_short", name, $"String should have at least {min.ToString(CultureInfo.InvariantCulture)} {unit}", raw, new JObj { { "min_length", min } });
            return fallback;
        }

        if (maxLength is { } max && raw.Length > max)
        {
            var unit = max == 1 ? "character" : "characters";
            Error("string_too_long", name, $"String should have at most {max.ToString(CultureInfo.InvariantCulture)} {unit}", raw, new JObj { { "max_length", max } });
            return fallback;
        }

        if (pattern is not null && !pattern.IsMatch(raw))
        {
            Error("string_pattern_mismatch", name, $"String should match pattern '{pattern}'", raw, new JObj { { "pattern", pattern.ToString() } });
            return fallback;
        }

        return raw;
    }

    /// <summary>One of <paramref name="allowed"/>; required when <paramref name="fallback"/> is null.</summary>
    public string Literal(string name, string? fallback, params string[] allowed)
    {
        if (!_query.TryGetValue(name, out var raw))
        {
            if (fallback is null)
                Missing(name);
            return fallback ?? allowed[0];
        }

        if (Array.IndexOf(allowed, raw) >= 0)
            return raw;

        var quoted = allowed.Select(a => $"'{a}'").ToList();
        var expected = quoted.Count == 1 ? quoted[0] : string.Join(", ", quoted.Take(quoted.Count - 1)) + " or " + quoted[^1];
        Error("literal_error", name, $"Input should be {expected}", raw, new JObj { { "expected", expected } });
        return fallback ?? allowed[0];
    }

    public bool Bool(string name, bool fallback) => BoolOrNull(name) ?? fallback;

    public bool? BoolOrNull(string name)
    {
        if (!_query.TryGetValue(name, out var raw))
            return null;

        var word = raw.Trim().ToLowerInvariant();
        if (TrueWords.Contains(word))
            return true;
        if (FalseWords.Contains(word))
            return false;

        Error("bool_parsing", name, "Input should be a valid boolean, unable to interpret input", raw);
        return null;
    }

    private void Missing(string name) =>
        _errors.Add(new JObj { { "type", "missing" }, { "loc", new object[] { "query", name } }, { "msg", "Field required" }, { "input", null } });

    private void Error(string type, string name, string message, string input, JObj? context = null)
    {
        var error = new JObj { { "type", type }, { "loc", new object[] { "query", name } }, { "msg", message }, { "input", input } };
        if (context is not null)
            error.Add("ctx", context);
        _errors.Add(error);
    }
}
