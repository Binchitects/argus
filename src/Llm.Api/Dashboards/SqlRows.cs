namespace Llm.Api.Dashboards;

/// <summary>What a panel's SQL is held to and how its rows read (<see cref="GatewayDatasource"/>): one statement, a role that only reads.</summary>
public static class SqlRows
{
    /// <summary>The role panel SQL runs as: it may read the schema gateway and nothing else.</summary>
    public const string ReadOnlyRole = "llm_dashboards";

    /// <summary>True when a ';' outside quotes and comments is followed by more SQL.</summary>
    public static bool HasSecondStatement(string sql)
    {
        var quote = '\0';
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var nl = sql.IndexOf('\n', i);
                i = nl < 0 ? sql.Length : nl;
            }
            else if (c == ';' && sql[(i + 1)..].Trim().Length > 0)
            {
                return true;
            }
        }
        return false;
    }

    internal static string TypeOf(Type t) =>
        t == typeof(string) ? "string"
        : t == typeof(bool) ? "boolean"
        : t == typeof(DateTime) || t == typeof(DateTimeOffset) ? "time"
        : t.IsPrimitive || t == typeof(decimal) ? "number"
        : "string";

    internal static object? Normalise(object v) => v switch
    {
        decimal m => (double)m,
        long or int or short or float or double => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture),
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        string or bool or DateTimeOffset => v,
        _ => v.ToString(),
    };
}
