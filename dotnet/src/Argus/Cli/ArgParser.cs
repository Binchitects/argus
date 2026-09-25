namespace Argus.Cli;

/// <summary>A usage error: argparse prints usage and exits 2.</summary>
public sealed class UsageError(string message) : Exception(message);

/// <summary>
/// The subset of argparse the CLI uses: long options with values, repeated
/// (append) options, flags, and optional positionals. Kept small and explicit
/// so every command's surface can be read in one place (see Commands.cs).
/// </summary>
public sealed class ArgSpec(string prog)
{
    public string Prog { get; } = prog;
    readonly List<Option> _options = [];
    readonly List<(string Name, bool Optional, string Help)> _positionals = [];

    sealed record Option(string Flag, string Dest, bool TakesValue, bool Required, bool Append, string Help, string? Metavar);

    public ArgSpec Opt(string flag, string help = "", bool required = false, string? dest = null, string? metavar = null)
    {
        _options.Add(new Option(flag, dest ?? flag.TrimStart('-').Replace('-', '_'), true, required, false, help, metavar));
        return this;
    }

    public ArgSpec Many(string flag, string help = "", string? dest = null, string? metavar = null)
    {
        _options.Add(new Option(flag, dest ?? flag.TrimStart('-').Replace('-', '_'), true, false, true, help, metavar));
        return this;
    }

    public ArgSpec Flag(string flag, string help = "")
    {
        _options.Add(new Option(flag, flag.TrimStart('-').Replace('-', '_'), false, false, false, help, null));
        return this;
    }

    public ArgSpec Positional(string name, bool optional = false, string help = "")
    {
        _positionals.Add((name, optional, help));
        return this;
    }

    public string Usage()
    {
        var parts = new List<string> { "usage:", Prog, "[-h]" };
        foreach (var o in _options)
        {
            var text = o.TakesValue ? $"{o.Flag} {o.Metavar ?? o.Dest.ToUpperInvariant()}" : o.Flag;
            parts.Add(o.Required ? text : $"[{text}]");
        }
        foreach (var (name, optional, _) in _positionals) parts.Add(optional ? $"[{name}]" : name);
        return string.Join(" ", parts);
    }

    public string Help()
    {
        var lines = new List<string> { Usage(), "" };
        if (_positionals.Count > 0)
        {
            lines.Add("positional arguments:");
            foreach (var (name, _, help) in _positionals) lines.Add($"  {name,-24} {help}");
            lines.Add("");
        }
        lines.Add("options:");
        lines.Add($"  {"-h, --help",-24} show this help message and exit");
        foreach (var o in _options)
        {
            var text = o.TakesValue ? $"{o.Flag} {o.Metavar ?? o.Dest.ToUpperInvariant()}" : o.Flag;
            lines.Add($"  {text,-24} {o.Help}");
        }
        return string.Join("\n", lines);
    }

    public Parsed Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var positionals = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-h" or "--help") throw new HelpRequested(Help());
            if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
            {
                string flag = a;
                string? inline = null;
                int eq = a.IndexOf('=');
                if (eq > 0) { flag = a[..eq]; inline = a[(eq + 1)..]; }
                var matches = _options.Where(o => o.Flag == flag).ToList();
                if (matches.Count == 0)
                {
                    var prefix = _options.Where(o => o.Flag.StartsWith(flag, StringComparison.Ordinal)).ToList();
                    if (prefix.Count == 1) matches = prefix;
                    else if (prefix.Count > 1)
                        throw new UsageError($"ambiguous option: {flag} could match {string.Join(", ", prefix.Select(p => p.Flag))}");
                }
                if (matches.Count == 0) throw new UsageError($"unrecognized arguments: {string.Join(" ", args.Skip(i))}");
                var opt = matches[0];
                if (!opt.TakesValue)
                {
                    if (inline is not null) throw new UsageError($"argument {opt.Flag}: ignored explicit argument '{inline}'");
                    flags.Add(opt.Dest);
                    continue;
                }
                string value;
                if (inline is not null) value = inline;
                else if (i + 1 < args.Count && !(args[i + 1].StartsWith('-') && args[i + 1].Length > 1)) value = args[++i];
                else throw new UsageError($"argument {opt.Flag}: expected one argument");
                if (opt.Append)
                {
                    if (!lists.TryGetValue(opt.Dest, out var l)) lists[opt.Dest] = l = [];
                    l.Add(value);
                }
                else values[opt.Dest] = value;
                continue;
            }
            positionals.Add(a);
        }
        var missing = _options.Where(o => o.Required && !values.ContainsKey(o.Dest)).Select(o => o.Flag).ToList();
        var required = _positionals.Where(p => !p.Optional).ToList();
        if (positionals.Count < required.Count)
            missing.AddRange(required.Skip(positionals.Count).Select(p => p.Name));
        if (missing.Count > 0) throw new UsageError($"the following arguments are required: {string.Join(", ", missing)}");
        if (positionals.Count > _positionals.Count)
            throw new UsageError($"unrecognized arguments: {string.Join(" ", positionals.Skip(_positionals.Count))}");
        var named = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int i = 0; i < _positionals.Count; i++) named[_positionals[i].Name] = i < positionals.Count ? positionals[i] : null;
        return new Parsed(values, lists, flags, named);
    }
}

public sealed class HelpRequested(string text) : Exception(text);

public sealed class Parsed(Dictionary<string, string?> values, Dictionary<string, List<string>> lists, HashSet<string> flags,
    Dictionary<string, string?> positionals)
{
    public string? Get(string dest) => values.GetValueOrDefault(dest);
    public string Req(string dest) => values[dest]!;
    public bool Flag(string dest) => flags.Contains(dest);
    public List<string>? List(string dest) => lists.GetValueOrDefault(dest);
    public string? Pos(string name) => positionals.GetValueOrDefault(name);

    public int? Int(string dest)
    {
        var v = Get(dest);
        if (v is null) return null;
        if (!int.TryParse(v, out var n)) throw new UsageError($"argument --{dest.Replace('_', '-')}: invalid int value: '{v}'");
        return n;
    }
}
