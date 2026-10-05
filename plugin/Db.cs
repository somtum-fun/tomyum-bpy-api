// Read-only database access over the host's trusted connection (IPluginContext.Data). Every query
// here is a SELECT; values always go in as parameters, only fixed SQL fragments are spliced in.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.BpyApi;

/// <summary>One result row, by column name (case-insensitive), with lenient typed getters.</summary>
internal sealed class Row
{
    private readonly Dictionary<string, object?> _values;

    public Row(Dictionary<string, object?> values) => _values = values;

    public IEnumerable<string> Columns => _values.Keys;

    public object? this[string column] => _values.TryGetValue(column, out var value) && value is not DBNull ? value : null;

    public bool IsNull(string column) => this[column] is null;

    public long Long(string column) => this[column] switch
    {
        null => 0,
        bool b => b ? 1 : 0,
        string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
        float or double or decimal => (long)Math.Round(Convert.ToDouble(this[column], CultureInfo.InvariantCulture)),
        var value => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };

    public long? LongOrNull(string column) => IsNull(column) ? null : Long(column);

    public int Int(string column) => (int)Long(column);

    public double Double(string column) => this[column] switch
    {
        null => 0,
        bool b => b ? 1 : 0,
        float f => double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
        var value => Convert.ToDouble(value, CultureInfo.InvariantCulture),
    };

    public string? Str(string column) => this[column] switch
    {
        null => null,
        string s => s,
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        var value => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    public string Text(string column) => Str(column) ?? string.Empty;

    public bool Bool(string column) => Long(column) != 0;

    public DateTime? Time(string column) => this[column] switch
    {
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        DateTimeOffset dto => dto.UtcDateTime,
        _ => null,
    };

    /// <summary>
    /// The value as bancho.py's JSON has it: whole numbers as integers, booleans (MySQL's tinyint(1))
    /// as 0/1, decimals as floats.
    /// </summary>
    public object? Json(string column) => this[column] switch
    {
        null => null,
        bool b => b ? 1 : 0,
        decimal m => (double)m,
        float f => double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
        DateTime dt => dt,
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        var value => value,
    };

    /// <summary>A JSON object of <paramref name="columns"/>, in that order.</summary>
    public JObj Pick(params string[] columns)
    {
        var obj = new JObj();
        foreach (var column in columns)
            obj.Add(column, Json(column));
        return obj;
    }
}

/// <summary>Opens connections and runs parameterised SELECTs.</summary>
internal sealed class Db
{
    private readonly IPluginDataAccess _data;

    public Db(IPluginDataAccess data) => _data = data;

    public async Task<List<Row>> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
            return await QueryAsync(connection, sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Row?> FirstAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<long> ScalarAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        var row = await FirstAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        if (row is null)
            return 0;
        foreach (var column in row.Columns)
            return row.Long(column);
        return 0;
    }

    /// <summary>Sees every query before it runs (the tests use it to EXPLAIN them).</summary>
    internal static Action<string, IReadOnlyDictionary<string, object?>?>? Trace { get; set; }

    public static async Task<List<Row>> QueryAsync(
        DbConnection connection, string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        Trace?.Invoke(sql, parameters);
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            if (parameters is not null)
            {
                foreach (var (name, value) in parameters)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }
            }

            var rows = new List<Row>();
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var values = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < reader.FieldCount; i++)
                        values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(new Row(values));
                }
            }

            return rows;
        }
    }

    /// <summary>Adds <c>@prefix0, @prefix1, …</c> for <paramref name="values"/> and returns the list for an IN (...).</summary>
    public static string InList<T>(Dictionary<string, object?> parameters, string prefix, IEnumerable<T> values)
    {
        var names = new List<string>();
        foreach (var value in values)
        {
            var name = "@" + prefix + names.Count.ToString(CultureInfo.InvariantCulture);
            parameters[name] = value;
            names.Add(name);
        }

        return names.Count == 0 ? "NULL" : string.Join(", ", names);
    }
}
