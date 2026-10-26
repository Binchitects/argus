using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Llm.Api.Safeguards;

/// <summary>What a detector found in a text: its kind (never the text itself) and where, to mask it.</summary>
public sealed record Finding(string Kind, int Index, int Length);

/// <summary>Findings in a text, masked: each replaced by a marker that names its kind.</summary>
public static class Findings
{
    /// <summary>In order, none overlapping (the longest at a place kept).</summary>
    public static List<Finding> Ordered(IEnumerable<Finding> found)
    {
        var ordered = new List<Finding>();
        foreach (var f in found.Where(f => f.Length > 0).OrderBy(f => f.Index).ThenByDescending(f => f.Length))
        {
            if (ordered.Count == 0 || f.Index >= ordered[^1].Index + ordered[^1].Length)
            {
                ordered.Add(f);
            }
        }
        return ordered;
    }

    public static string Mask(string text, IReadOnlyList<Finding> found, Func<Finding, string>? marker = null)
    {
        if (found.Count == 0)
        {
            return text;
        }
        var sb = new StringBuilder(text.Length);
        var at = 0;
        foreach (var f in Ordered(found))
        {
            sb.Append(text, at, f.Index - at).Append(marker?.Invoke(f) ?? $"[{f.Kind}]");
            at = f.Index + f.Length;
        }
        return sb.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>"an email address and a phone number": each kind once.</summary>
    public static string Kinds(IEnumerable<Finding> found)
    {
        var kinds = found.Select(f => f.Kind).Distinct().ToList();
        return kinds.Count <= 1 ? kinds.FirstOrDefault() ?? "" : string.Join(", ", kinds[..^1]) + " and " + kinds[^1];
    }
}

/// <summary>
/// Personal and health data in text: e-mail addresses, phone numbers, card numbers (Luhn-checked), IBANs, IP addresses, US social
/// security numbers; dates of birth and medical record numbers (by the words that name them), NHS numbers and Iranian national codes
/// (named, and their check digit right), and US Medicare numbers (MBI, by their shape).
/// </summary>
public static partial class Pii
{
    public static readonly string[] AllKinds = ["email", "phone", "card", "iban", "ip", "ssn", "dob", "mrn", "nhs", "national_id", "medicare"];

    /// <summary>The kinds that are health data or identify a patient (HIPAA's identifiers among them).</summary>
    public static readonly string[] HealthKinds = ["dob", "mrn", "nhs", "national_id", "medicare", "ssn"];

    private static readonly Dictionary<string, string> Markers = new(StringComparer.Ordinal)
    {
        ["email"] = "[email]", ["phone"] = "[phone]", ["card"] = "[card number]", ["iban"] = "[iban]", ["ip"] = "[ip address]", ["ssn"] = "[ssn]",
        ["dob"] = "[date of birth]", ["mrn"] = "[medical record number]", ["nhs"] = "[nhs number]", ["national_id"] = "[national id]", ["medicare"] = "[medicare number]",
    };

    /// <summary>The kinds' names for a person: "email" is "an email address".</summary>
    public static string Named(string kind) => kind switch
    {
        "email" => "an email address", "phone" => "a phone number", "card" => "a card number", "iban" => "an IBAN", "ip" => "an IP address",
        "ssn" => "a social security number", "dob" => "a date of birth", "mrn" => "a medical record number", "nhs" => "an NHS number",
        "national_id" => "a national code", "medicare" => "a Medicare number", _ => kind,
    };

    public static List<Finding> Find(string text, IReadOnlyCollection<string>? kinds = null)
    {
        bool On(string k) => kinds is not { Count: > 0 } || kinds.Contains(k);
        var found = new List<Finding>();
        try
        {
            if (On("email"))
            {
                found.AddRange(Email().Matches(text).Select(m => new Finding("email", m.Index, m.Length)));
            }
            if (On("iban"))
            {
                found.AddRange(Iban().Matches(text).Select(m => new Finding("iban", m.Index, m.Length)));
            }
            if (On("card"))
            {
                found.AddRange(Card().Matches(text).Where(m => Luhn(new string([.. m.Value.Where(char.IsDigit)]))).Select(m => new Finding("card", m.Index, m.Length)));
            }
            if (On("ssn"))
            {
                found.AddRange(Ssn().Matches(text).Select(m => new Finding("ssn", m.Index, m.Length)));
            }
            if (On("dob"))
            {
                found.AddRange(Birth().Matches(text).Select(m => new Finding("dob", m.Groups["v"].Index, m.Groups["v"].Length)));
            }
            if (On("mrn"))
            {
                found.AddRange(Record().Matches(text).Select(m => new Finding("mrn", m.Groups["v"].Index, m.Groups["v"].Length)));
            }
            if (On("nhs"))
            {
                found.AddRange(Nhs().Matches(text).Where(m => NhsValid(Digits(m.Groups["v"].Value))).Select(m => new Finding("nhs", m.Groups["v"].Index, m.Groups["v"].Length)));
            }
            if (On("national_id"))
            {
                found.AddRange(NationalCode().Matches(text).Where(m => IranianCodeValid(Digits(m.Groups["v"].Value))).Select(m => new Finding("national_id", m.Groups["v"].Index, m.Groups["v"].Length)));
            }
            if (On("medicare"))
            {
                found.AddRange(Medicare().Matches(text).Select(m => new Finding("medicare", m.Index, m.Length)));
            }
            if (On("ip"))
            {
                found.AddRange(Ip().Matches(text).Where(m => m.Value.Split('.').All(p => int.Parse(p, CultureInfo.InvariantCulture) <= 255) && !m.Value.StartsWith("0.", StringComparison.Ordinal))
                    .Select(m => new Finding("ip", m.Index, m.Length)));
            }
            if (On("phone"))
            {
                found.AddRange(Phone().Matches(text).Select(m => new Finding("phone", m.Index, m.Length)));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A text that takes too long is let through as far as it was read.
        }
        // Earlier kinds win where they overlap (a card number is not also a phone number).
        var ordered = new List<Finding>();
        foreach (var f in found)
        {
            if (!ordered.Any(o => f.Index < o.Index + o.Length && o.Index < f.Index + f.Length))
            {
                ordered.Add(f);
            }
        }
        return ordered;
    }

    public static string Mask(string text) => Mask(text, null);

    public static string Mask(string text, IReadOnlyCollection<string>? kinds) => Findings.Mask(text, Find(text, kinds), f => Markers.GetValueOrDefault(f.Kind, "[personal data]"));

    private static string Digits(string s) => new([.. s.Where(char.IsAsciiDigit)]);

    /// <summary>An NHS number's tenth digit: 11 less the weighted sum (10 down to 2) of the nine before, mod 11; 10 is never one.</summary>
    internal static bool NhsValid(string d)
    {
        if (d.Length != 10)
        {
            return false;
        }
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (d[i] - '0') * (10 - i);
        }
        var check = 11 - sum % 11;
        check = check == 11 ? 0 : check;
        return check != 10 && check == d[9] - '0';
    }

    /// <summary>An Iranian national code's last digit: the weighted sum (10 down to 2) of the nine before, mod 11, under 2 itself, else 11 less it.</summary>
    internal static bool IranianCodeValid(string d)
    {
        if (d.Length != 10 || d.Distinct().Count() == 1)
        {
            return false;
        }
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (d[i] - '0') * (10 - i);
        }
        var r = sum % 11;
        return d[9] - '0' == (r < 2 ? r : 11 - r);
    }

    private static bool Luhn(string digits)
    {
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            sum += i % 2 == 1 ? (d * 2 > 9 ? d * 2 - 9 : d * 2) : d;
        }
        return sum % 10 == 0;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,4})?\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Iban();

    [GeneratedRegex(@"\b(?:\d[ -]?){12,18}\d\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Card();

    [GeneratedRegex(@"\b(?!000|666|9\d\d)\d{3}-(?!00)\d{2}-(?!0000)\d{4}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Ssn();

    private const string Date = @"(?:\d{4}-\d{1,2}-\d{1,2}|\d{1,2}[./-]\d{1,2}[./-]\d{2,4}|\d{1,2}\s+(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+\d{4}|(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+\d{1,2},?\s+\d{4})";

    [GeneratedRegex(@"\b(?:dob|d\.o\.b\.?|date of birth|birth ?date|born(?: on)?|geboren(?: am)?|né(?:e)? le|تاریخ تولد)(?:\s+(?:is|was|of))?\s*[:\-=]?\s*(?<v>" + Date + ")", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Birth();

    [GeneratedRegex(@"\b(?:mrn|medical record(?: number| no\.?| #)?|patient (?:id|number|no\.?))(?:\s+(?:is|was|of))?\s*[:#=]?\s*(?<v>(?=[A-Z-]*\d)[A-Z0-9][A-Z0-9-]{4,14})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Record();

    [GeneratedRegex(@"\bNHS(?: number| no\.?| #)?(?:\s+(?:is|was|of))?\s*[:#=]?\s*(?<v>\d{3}[ -]?\d{3}[ -]?\d{4})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Nhs();

    [GeneratedRegex(@"(?:\bnational (?:code|id|number)|\bmelli code|کد ?ملی|شماره ملی)(?:\s+(?:is|was|of))?\s*[:#=]?\s*(?<v>\d{3}-?\d{6}-?\d)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex NationalCode();

    /// <summary>A Medicare Beneficiary Identifier: 11 characters in its fixed shape (no S, L, O, I, B or Z), dashes allowed after the 4th and 7th.</summary>
    [GeneratedRegex(@"\b[1-9][AC-HJKMNP-RT-Y][AC-HJKMNP-RT-Y0-9]\d-?[AC-HJKMNP-RT-Y][AC-HJKMNP-RT-Y0-9]\d-?[AC-HJKMNP-RT-Y]{2}\d{2}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Medicare();

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Ip();

    [GeneratedRegex(@"(?<!\w)(?:\+\d{1,3}[ -]?)?(?:\(\d{2,4}\)[ -]?)?\d{3,5}[ -]\d{3,6}(?:[ -]\d{2,4})?(?!\w)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Phone();
}

/// <summary>Words, phrases and patterns: a phrase matches as whole words, ignoring case; "re:" starts a regular expression.</summary>
public static class Words
{
    public static IEnumerable<string> Patterns(string? patterns) =>
        (patterns ?? "").Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Each match, its kind the pattern it matched.</summary>
    public static List<Finding> Find(string text, string? patterns)
    {
        var found = new List<Finding>();
        foreach (var raw in Patterns(patterns))
        {
            try
            {
                var re = raw.StartsWith("re:", StringComparison.OrdinalIgnoreCase)
                    ? new Regex(raw[3..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))
                    : new Regex(@"(?<!\w)" + Regex.Escape(raw) + @"(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                found.AddRange(re.Matches(text).Where(m => m.Length > 0).Select(m => new Finding(raw, m.Index, m.Length)));
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                // A pattern that does not compile, or takes too long, finds nothing.
            }
        }
        return Findings.Ordered(found);
    }

    /// <summary>The pattern's problem, for the rule's form: null when it compiles.</summary>
    public static string? Invalid(string? patterns)
    {
        foreach (var raw in Patterns(patterns).Where(p => p.StartsWith("re:", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                _ = new Regex(raw[3..], RegexOptions.None, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex)
            {
                return $"{raw}: {ex.Message}";
            }
        }
        return null;
    }
}

/// <summary>Links and domains in a text, against a list: refused ones, or any not on the allowed list.</summary>
public static partial class Links
{
    public static List<Finding> Find(string text, IReadOnlyCollection<string> domains, bool allowedOnly)
    {
        var found = new List<Finding>();
        try
        {
            foreach (Match m in Url().Matches(text))
            {
                var host = m.Groups["host"].Value.TrimEnd('.').ToLowerInvariant();
                var listed = domains.Any(d => Covers(d, host));
                if (allowedOnly ? !listed : listed)
                {
                    found.Add(new Finding(host, m.Index, m.Length));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // As far as it was read.
        }
        return found;
    }

    /// <summary>A domain covers itself and its subdomains: example.com covers docs.example.com.</summary>
    public static bool Covers(string domain, string host)
    {
        var d = domain.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant();
        return d.Length > 0 && (host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"\b(?:https?|ftp)://(?<host>[A-Za-z0-9.-]+)(?::\d+)?[^\s<>""')\]]*|\bwww\.(?<host>[A-Za-z0-9.-]+\.[A-Za-z]{2,})[^\s<>""')\]]*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Url();
}

/// <summary>
/// Prompt injection and jailbreaks by their usual signs: telling the assistant to ignore its instructions, to reveal
/// its system prompt, to take on a role without rules; and text hidden from people (Unicode tag characters, runs of
/// zero-width characters). The model can read the text too (a rule's ModelCheck), for what no pattern catches.
/// </summary>
public static partial class Injection
{
    private static readonly (string Kind, Func<Regex> Pattern)[] Signs =
    [
        ("ignore instructions", Ignore),
        ("reveal the system prompt", Reveal),
        ("a role without rules", Role),
        ("a fake system message", FakeSystem),
    ];

    public static List<Finding> Find(string text)
    {
        var found = new List<Finding>();
        foreach (var (kind, pattern) in Signs)
        {
            try
            {
                found.AddRange(pattern().Matches(text).Select(m => new Finding(kind, m.Index, m.Length)));
            }
            catch (RegexMatchTimeoutException)
            {
                // As far as it was read.
            }
        }
        // Unicode tag characters (U+E0000-E007F) carry text no one sees; so do long runs of zero-width ones.
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.ConvertToUtf32(text[i], text[i + 1]) is >= 0xE0000 and <= 0xE007F)
            {
                var start = i;
                while (i + 1 < text.Length && char.IsHighSurrogate(text[i]) && char.ConvertToUtf32(text[i], text[i + 1]) is >= 0xE0000 and <= 0xE007F)
                {
                    i += 2;
                }
                found.Add(new Finding("hidden text", start, i - start));
            }
        }
        foreach (Match m in ZeroWidth().Matches(text))
        {
            found.Add(new Finding("hidden text", m.Index, m.Length));
        }
        return Findings.Ordered(found);
    }

    [GeneratedRegex(@"\b(?:ignore|disregard|forget|override|bypass)\b[^.\n]{0,40}\b(?:previous|prior|above|earlier|all|your|the|any)\b[^.\n]{0,30}\b(?:instructions?|prompts?|rules|directions|guidelines|guardrails|restrictions)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex Ignore();

    [GeneratedRegex(@"\b(?:reveal|show|print|repeat|output|tell me|leak|dump)\b[^.\n]{0,30}\b(?:your|the)\b[^.\n]{0,15}\b(?:system prompt|initial prompt|hidden instructions|system message|instructions you were given)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex Reveal();

    [GeneratedRegex(@"\b(?:you are now|act as|pretend to be|from now on you are|enter|enable)\b[^.\n]{0,30}\b(?:DAN|developer mode|jailbreak(?:ed)?|unrestricted|unfiltered|no (?:rules|restrictions|limits|filters)|without (?:rules|restrictions|limits|filters))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex Role();

    [GeneratedRegex(@"(?:^|\n)\s*(?:\[\s*system\s*\]|<\|?\s*(?:system|im_start\|?>\s*system)\s*\|?>|###\s*system\s*:|system\s*override\s*:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex FakeSystem();

    [GeneratedRegex(@"[​‌‍⁠﻿]{8,}", RegexOptions.CultureInvariant, 500)]
    private static partial Regex ZeroWidth();
}
