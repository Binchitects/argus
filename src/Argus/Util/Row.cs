using System.Text.Json.Nodes;

namespace Argus.Util;

/// <summary>
/// One result row: the columns in SELECT order, like <c>sqlite3.Row</c>.
///
/// Order is part of the contract. <c>dict(row)</c> in Python keeps the column
/// order of the query, and that order is what a client sees as the key order of
/// every tool result, so a dictionary that forgot it would change every answer's
/// shape.
/// </summary>
public sealed class Row
{
    readonly List<string> _names;
    readonly List<object?> _values;

    public Row() { _names = []; _values = []; }

    public Row(List<string> names, List<object?> values)
    {
        _names = names;
        _values = values;
    }

    public IReadOnlyList<string> Keys => _names;
    public int Count => _names.Count;

    int IndexOf(string name)
    {
        for (int i = 0; i < _names.Count; i++)
            if (_names[i] == name) return i;
        return -1;
    }

    public bool Has(string name) => IndexOf(name) >= 0;

    public object? this[string name]
    {
        get
        {
            int i = IndexOf(name);
            if (i < 0) throw new KeyNotFoundException(name);
            return _values[i];
        }
        set
        {
            int i = IndexOf(name);
            if (i < 0) { _names.Add(name); _values.Add(value); }
            else _values[i] = value;
        }
    }

    public object? this[int index] => _values[index];

    public object? Get(string name) { int i = IndexOf(name); return i < 0 ? null : _values[i]; }

    public long Long(string name) => Convert.ToInt64(this[name] ?? 0L);
    public long? LongOrNull(string name) => this[name] is null ? null : Convert.ToInt64(this[name]);
    public double Double(string name) => Convert.ToDouble(this[name] ?? 0.0);
    public string Str(string name) => this[name] switch { null => "", string s => s, var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" };
    public string? StrOrNull(string name) => this[name] switch { null => null, string s => s, var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) };
    public byte[]? Bytes(string name) => this[name] as byte[];
    public bool Truthy(string name) => this[name] switch
    {
        null => false,
        long l => l != 0,
        double d => d != 0,
        string s => s.Length > 0,
        byte[] b => b.Length > 0,
        bool b => b,
        _ => true,
    };

    public Row Copy() => new(new List<string>(_names), new List<object?>(_values));

    public void Remove(string name)
    {
        int i = IndexOf(name);
        if (i >= 0) { _names.RemoveAt(i); _values.RemoveAt(i); }
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        for (int i = 0; i < _names.Count; i++) o[_names[i]] = PyJson.From(_values[i]);
        return o;
    }

    public IEnumerable<KeyValuePair<string, object?>> Pairs()
    {
        for (int i = 0; i < _names.Count; i++) yield return new(_names[i], _values[i]);
    }

    /// <summary>Build a row from name/value pairs, in the order given.</summary>
    public static Row Of(params (string Name, object? Value)[] pairs)
    {
        var r = new Row();
        foreach (var (n, v) in pairs) r[n] = v;
        return r;
    }
}
