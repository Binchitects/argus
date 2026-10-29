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
/// Personal and health data in text: e-mail addresses, phone numbers (Iranian mobiles too), card numbers (Luhn-checked), IBANs
/// (their check digits right), IP and MAC addresses, US social security numbers; dates of birth and medical record numbers (by
/// the words that name them), NHS numbers and Iranian national codes (named, and their check digit right), US Medicare numbers
/// (MBI, by their shape); passport numbers (named), UK National Insurance numbers, Indian Aadhaar (Verhoeff-checked) and PAN
/// numbers, Canadian social insurance numbers (Luhn-checked), and crypto wallet addresses. Persian and Arabic digits are read as
/// 0-9.
/// </summary>
public static partial class Pii
{
    public static readonly string[] AllKinds = ["email", "phone", "card", "iban", "ip", "ssn", "dob", "mrn", "nhs", "national_id", "medicare",
        "passport", "nino", "aadhaar", "pan_in", "sin", "mac", "crypto_wallet"];

    /// <summary>Numbers that identify a person to a state: for a rule that keeps them from models on other providers.</summary>
    public static readonly string[] IdentityKinds = ["national_id", "passport", "ssn", "nino", "aadhaar", "pan_in", "sin", "medicare", "nhs"];

    /// <summary>The kinds that are health data or identify a patient (HIPAA's identifiers among them).</summary>
    public static readonly string[] HealthKinds = ["dob", "mrn", "nhs", "national_id", "medicare", "ssn"];

    private static readonly Dictionary<string, string> Markers = new(StringComparer.Ordinal)
    {
        ["email"] = "[email]", ["phone"] = "[phone]", ["card"] = "[card number]", ["iban"] = "[iban]", ["ip"] = "[ip address]", ["ssn"] = "[ssn]",
        ["dob"] = "[date of birth]", ["mrn"] = "[medical record number]", ["nhs"] = "[nhs number]", ["national_id"] = "[national id]", ["medicare"] = "[medicare number]",
        ["passport"] = "[passport number]", ["nino"] = "[national insurance number]", ["aadhaar"] = "[aadhaar number]", ["pan_in"] = "[pan number]",
        ["sin"] = "[social insurance number]", ["mac"] = "[mac address]", ["crypto_wallet"] = "[wallet address]",
    };

    /// <summary>The kinds' names for a person: "email" is "an email address".</summary>
    public static string Named(string kind) => kind switch
    {
        "email" => "an email address", "phone" => "a phone number", "card" => "a card number", "iban" => "an IBAN", "ip" => "an IP address",
        "ssn" => "a social security number", "dob" => "a date of birth", "mrn" => "a medical record number", "nhs" => "an NHS number",
        "national_id" => "a national code", "medicare" => "a Medicare number", "passport" => "a passport number", "nino" => "a National Insurance number",
        "aadhaar" => "an Aadhaar number", "pan_in" => "a PAN number", "sin" => "a social insurance number", "mac" => "a MAC address",
        "crypto_wallet" => "a crypto wallet address", _ => kind,
    };

    public static List<Finding> Find(string text, IReadOnlyCollection<string>? kinds = null)
    {
        bool On(string k) => kinds is not { Count: > 0 } || kinds.Contains(k);
        var found = new List<Finding>();
        // Persian and Arabic digits as 0-9, one for one: the places stay those of the text as written.
        text = Normal.LatinDigits(text);
        try
        {
            if (On("email"))
            {
                found.AddRange(Email().Matches(text).Select(m => new Finding("email", m.Index, m.Length)));
            }
            if (On("iban"))
            {
                found.AddRange(Iban().Matches(text).Where(m => IbanValid(m.Value)).Select(m => new Finding("iban", m.Index, m.Length)));
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
            if (On("passport"))
            {
                found.AddRange(Passport().Matches(text).Select(m => new Finding("passport", m.Groups["v"].Index, m.Groups["v"].Length)));
            }
            if (On("nino"))
            {
                found.AddRange(Nino().Matches(text).Select(m => new Finding("nino", m.Index, m.Length)));
            }
            if (On("aadhaar"))
            {
                found.AddRange(Aadhaar().Matches(text).Where(m => Verhoeff(Digits(m.Value))).Select(m => new Finding("aadhaar", m.Index, m.Length)));
            }
            if (On("pan_in"))
            {
                found.AddRange(PanIn().Matches(text).Select(m => new Finding("pan_in", m.Index, m.Length)));
            }
            if (On("sin"))
            {
                found.AddRange(Sin().Matches(text).Where(m => Luhn9(Digits(m.Value))).Select(m => new Finding("sin", m.Index, m.Length)));
            }
            if (On("mac"))
            {
                found.AddRange(Mac().Matches(text).Select(m => new Finding("mac", m.Index, m.Length)));
            }
            if (On("crypto_wallet"))
            {
                found.AddRange(Wallet().Matches(text).Select(m => new Finding("crypto_wallet", m.Index, m.Length)));
            }
            if (On("ip"))
            {
                found.AddRange(Ip().Matches(text).Where(m => m.Value.Split('.').All(p => int.Parse(p, CultureInfo.InvariantCulture) <= 255) && !m.Value.StartsWith("0.", StringComparison.Ordinal))
                    .Select(m => new Finding("ip", m.Index, m.Length)));
            }
            if (On("phone"))
            {
                found.AddRange(IranMobile().Matches(text).Select(m => new Finding("phone", m.Index, m.Length)));
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

    /// <summary>An IBAN's check digits: the country and check moved to the end, letters as 10-35, the number mod 97 is 1.</summary>
    internal static bool IbanValid(string iban)
    {
        var s = new string([.. iban.Where(char.IsLetterOrDigit)]).ToUpperInvariant();
        if (s.Length is < 15 or > 34)
        {
            return false;
        }
        var r = 0;
        foreach (var c in s[4..] + s[..4])
        {
            var v = char.IsDigit(c) ? c - '0' : c - 'A' + 10;
            r = v >= 10 ? (r * 100 + v) % 97 : (r * 10 + v) % 97;
        }
        return r == 1;
    }

    private static readonly int[,] VerhoeffD =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 2, 3, 4, 0, 6, 7, 8, 9, 5 }, { 2, 3, 4, 0, 1, 7, 8, 9, 5, 6 }, { 3, 4, 0, 1, 2, 8, 9, 5, 6, 7 },
        { 4, 0, 1, 2, 3, 9, 5, 6, 7, 8 }, { 5, 9, 8, 7, 6, 0, 4, 3, 2, 1 }, { 6, 5, 9, 8, 7, 1, 0, 4, 3, 2 }, { 7, 6, 5, 9, 8, 2, 1, 0, 4, 3 },
        { 8, 7, 6, 5, 9, 3, 2, 1, 0, 4 }, { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 },
    };

    private static readonly int[,] VerhoeffP =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 5, 7, 6, 2, 8, 3, 0, 9, 4 }, { 5, 8, 0, 3, 7, 9, 6, 1, 4, 2 }, { 8, 9, 1, 6, 0, 4, 3, 5, 2, 7 },
        { 9, 4, 5, 3, 1, 2, 6, 8, 7, 0 }, { 4, 2, 8, 6, 5, 7, 3, 9, 0, 1 }, { 2, 7, 9, 3, 8, 0, 6, 4, 1, 5 }, { 7, 0, 4, 6, 9, 1, 3, 2, 5, 8 },
    };

    /// <summary>An Aadhaar number's Verhoeff check digit.</summary>
    internal static bool Verhoeff(string d)
    {
        if (d.Length != 12)
        {
            return false;
        }
        var c = 0;
        for (var i = 0; i < d.Length; i++)
        {
            c = VerhoeffD[c, VerhoeffP[i % 8, d[d.Length - 1 - i] - '0']];
        }
        return c == 0;
    }

    /// <summary>A Canadian SIN's Luhn check (nine digits).</summary>
    private static bool Luhn9(string d)
    {
        if (d.Length != 9 || d[0] == '8')
        {
            return false;
        }
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var v = (d[i] - '0') * (i % 2 == 1 ? 2 : 1);
            sum += v > 9 ? v - 9 : v;
        }
        return sum % 10 == 0;
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

    /// <summary>An Iranian mobile number: 09xx xxx xxxx, +98 9xx..., 0098 9xx..., with or without spaces.</summary>
    [GeneratedRegex(@"(?<![\d+])(?:\+98[ -]?|0098[ -]?|0)9\d{2}[ -]?\d{3}[ -]?\d{4}(?!\d)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IranMobile();

    [GeneratedRegex(@"\b(?:passport(?: (?:no|number|#))?\.?|passeport|reisepass|pasaporte)(?:\s+(?:is|was))?\s*[:#=]?\s*(?<v>[A-Z0-9]{6,9})\b|(?:شماره\s*)?(?:گذرنامه|پاسپورت)\s*[:#=]?\s*(?<v>[A-Z0-9]{6,9})\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Passport();

    [GeneratedRegex(@"\b(?!BG|GB|NK|KN|TN|NT|ZZ)[A-CEGHJ-PR-TW-Z][A-CEGHJ-NPR-TW-Z] ?\d{2} ?\d{2} ?\d{2} ?[A-D]\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Nino();

    [GeneratedRegex(@"(?<![\d-])[2-9]\d{3}[ -]?\d{4}[ -]?\d{4}(?![\d-])", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Aadhaar();

    [GeneratedRegex(@"\b[A-Z]{3}[ABCFGHLJPTK][A-Z]\d{4}[A-Z]\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PanIn();

    [GeneratedRegex(@"(?<![\d-])\d{3}[ -]\d{3}[ -]\d{3}(?![\d-])", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Sin();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:-])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:-])", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Mac();

    /// <summary>Ethereum (0x and 40 hex), Bitcoin's bech32 (bc1...) and TRON (T and 33 base58) addresses.</summary>
    [GeneratedRegex(@"\b0x[a-fA-F0-9]{40}\b|\bbc1[ac-hj-np-z02-9]{25,62}\b|\bT[1-9A-HJ-NP-Za-km-z]{33}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Wallet();
}

/// <summary>
/// Words, phrases and patterns: a phrase matches as whole words, ignoring case; "re:" starts a regular expression. Each is
/// looked for in the text as written and as <see cref="Normal"/> reads it, so "p@ssw0rd", "s e c r e t" or a Cyrillic "е"
/// in place of a Latin one still match.
/// </summary>
public static class Words
{
    public static IEnumerable<string> Patterns(string? patterns) =>
        (patterns ?? "").Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Each match, its kind the pattern it matched.</summary>
    public static List<Finding> Find(string text, string? patterns)
    {
        var found = new List<Finding>();
        Normal? normal = null;
        foreach (var raw in Patterns(patterns))
        {
            try
            {
                var regex = raw.StartsWith("re:", StringComparison.OrdinalIgnoreCase);
                var re = regex
                    ? new Regex(raw[3..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))
                    : new Regex(@"(?<!\w)" + Regex.Escape(raw) + @"(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                found.AddRange(re.Matches(text).Where(m => m.Length > 0).Select(m => new Finding(raw, m.Index, m.Length)));
                // As read through its disguises: a phrase read the same way too.
                normal ??= Normal.Of(text);
                var read = regex ? re : new Regex(@"(?<!\w)" + Regex.Escape(Normal.Of(raw).Text) + @"(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                found.AddRange(normal.Placed(read.Matches(normal.Text).Where(m => m.Length > 0).Select(m => new Finding(raw, m.Index, m.Length))));
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
/// Prompt injection and jailbreaks by their usual signs: telling the assistant to ignore its instructions (in English and
/// in Persian, Arabic, Chinese, Russian, Spanish, French, German, Turkish, Portuguese and Italian), to reveal its system
/// prompt, to take on a role without rules (DAN, developer mode, an "evil" or "grandma" persona); a fake system message;
/// such an instruction in base64; text hidden from people (Unicode tag characters, runs of zero-width characters, text
/// that reads in another order than it shows, an instruction in an HTML comment or a hidden element); and, in what tools
/// and files bring (<paramref name="external"/>), an order to send data somewhere. Each sign is looked for in the text as
/// written and as <see cref="Normal"/> reads it. The model can read the text too (a rule's ModelCheck), for what no pattern
/// catches.
/// </summary>
public static partial class Injection
{
    private static readonly (string Kind, Func<Regex> Pattern)[] Signs =
    [
        ("ignore instructions", Ignore),
        ("ignore instructions", IgnoreOther),
        ("reveal the system prompt", Reveal),
        ("reveal the system prompt", RevealMore),
        ("a role without rules", Role),
        ("a role without rules", Persona),
        ("a fake system message", FakeSystem),
    ];

    /// <summary>The signs of an order to act, in what a tool or file brings: the person never wrote it.</summary>
    private static readonly (string Kind, Func<Regex> Pattern)[] External =
    [
        ("an order to send data", SendData),
        ("an order to the assistant", AddressAssistant),
    ];

    public static List<Finding> Find(string text) => Find(text, external: false);

    public static List<Finding> Find(string text, bool external)
    {
        var found = new List<Finding>();
        var normal = Normal.Of(text);
        foreach (var (kind, pattern) in external ? [.. Signs, .. External] : Signs)
        {
            try
            {
                found.AddRange(pattern().Matches(text).Select(m => new Finding(kind, m.Index, m.Length)));
                found.AddRange(normal.Placed(pattern().Matches(normal.Text).Select(m => new Finding(kind, m.Index, m.Length))));
            }
            catch (RegexMatchTimeoutException)
            {
                // As far as it was read.
            }
        }
        // An instruction in base64: decoded, and read for the same signs.
        try
        {
            foreach (Match m in Base64().Matches(text))
            {
                if (Decoded(m.Value) is { } plain && Signs.Any(s => s.Pattern().IsMatch(plain) || s.Pattern().IsMatch(Normal.Of(plain).Text)))
                {
                    found.Add(new Finding("an encoded instruction", m.Index, m.Length));
                }
            }
            // An HTML comment or a hidden element that holds an instruction.
            foreach (Match m in HiddenHtml().Matches(text))
            {
                var inner = m.Groups["t"].Value;
                if (Signs.Concat(External).Any(s => s.Pattern().IsMatch(inner) || s.Pattern().IsMatch(Normal.Of(inner).Text)))
                {
                    found.Add(new Finding("a hidden instruction", m.Index, m.Length));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // As far as it was read.
        }
        // Characters that make text read in another order than it shows (Trojan Source).
        foreach (Match m in Bidi().Matches(text))
        {
            found.Add(new Finding("hidden text", m.Index, m.Length));
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

    /// <summary>A base64 text that decodes to something readable, else null.</summary>
    private static string? Decoded(string b64)
    {
        try
        {
            var padded = b64.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var plain = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return plain.Count(c => char.IsLetter(c) || c == ' ') >= plain.Length * 0.8 ? plain : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // Ignore the instructions, in other languages.
    [GeneratedRegex(@"(?:دستورات|دستورالعمل|قوانین|قواعد|محدودیت)[^.\n]{0,40}(?:را\s*)?(?:نادیده\s*بگیر|فراموش\s*کن|کنار\s*بگذار)|(?:نادیده\s*بگیر|فراموش\s*کن)[^.\n]{0,40}(?:دستورات|دستورالعمل|قوانین|قواعد)" +
        @"|(?:تجاهل|انس|تخط)[^.\n]{0,30}(?:التعليمات|الأوامر|القواعد)" +
        @"|(?:忽略|无视|忘记|忘掉)[^。\n]{0,20}(?:指令|指示|规则|提示)" +
        @"|(?:игнорируй|забудь|проигнорируй)[^.\n]{0,40}(?:инструкци|правила|указания)" +
        @"|(?:ignora|olvida|omite)[^.\n]{0,40}(?:instrucciones|reglas|indicaciones)" +
        @"|(?:ignore[sz]?|oublie[sz]?)[^.\n]{0,40}(?:instructions|consignes|règles)\s+(?:précédentes|antérieures|ci-dessus)" +
        @"|(?:ignoriere|vergiss|missachte)[^.\n]{0,40}(?:anweisungen|regeln|vorgaben)" +
        @"|(?:yoksay|unut|görmezden gel)[^.\n]{0,40}(?:talimat|kural)|(?:talimatları|kuralları)[^.\n]{0,30}(?:yoksay|unut|görmezden gel)" +
        @"|(?:ignore|esqueça|desconsidere)[^.\n]{0,40}(?:instruções|regras)" +
        @"|(?:ignora|dimentica)[^.\n]{0,40}(?:istruzioni|regole)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex IgnoreOther();

    // The system prompt asked for in other words.
    [GeneratedRegex(@"\b(?:repeat|print|output|write|copy|show)\b[^.\n]{0,25}\b(?:the |all )?(?:words|text|everything|lines)\b[^.\n]{0,15}\b(?:above|before this|so far|preceding)\b" +
        @"|\bwhat (?:were|are|was|is) your (?:first|initial|original|very first|system|hidden) (?:instructions?|prompt|message|rules)\b" +
        @"|\b(?:starting|beginning) with ""?you are""?" +
        @"|\b(?:your|the) (?:initial|system|original|pre-?prompt) (?:text|prompt|instructions?)\b[^.\n]{0,30}\b(?:in a code ?block|verbatim|word for word)\b" +
        @"|(?:پرامپت|دستورات)\s*(?:سیستمی|اولیه)\s*(?:خود|ات|ت)?\s*را\s*(?:نشان|بنویس|تکرار)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex RevealMore();

    // The known personas and the ways to ask for a model without its rules.
    [GeneratedRegex(@"\b(?:do anything now|DAN mode|STAN|DUDE|AIM \(always intelligent|evil confidant|opposite mode|anti-?gpt|BetterDAN|Mongo Tom|developer mode (?:enabled|output|on))\b" +
        @"|\bwithout any (?:moral|ethical|safety) (?:guidelines|constraints|restrictions|limits|filters)\b" +
        @"|\byou (?:have|are under) no (?:restrictions|filters|guidelines|rules|limits|policies)\b" +
        @"|\b(?:my )?(?:late|dead|deceased) grand(?:ma|mother|pa|father)\b[^.\n]{0,80}\b(?:used to|would)\b[^.\n]{0,40}\b(?:tell|read|recite|explain)\b" +
        @"|\b(?:stay in character|never break character)\b[^.\n]{0,60}\b(?:no matter|even if|whatever)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex Persona();

    // In what a tool or file brings: an order to send data somewhere.
    [GeneratedRegex(@"\b(?:send|email|e-mail|forward|upload|post|exfiltrate|transmit|leak)\b[^.\n]{0,60}\b(?:the |all |this |your |user'?s? )?(?:data|conversation|chat|history|messages?|files?|secrets?|keys?|credentials|passwords?|tokens?|contents?)\b[^.\n]{0,60}\b(?:to|at|into)\b\s+(?:https?://|\S+@\S+\.\w+|this (?:url|address|link|webhook))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex SendData();

    // In what a tool or file brings: words addressed to the assistant reading it.
    [GeneratedRegex(@"\b(?:attention|note|important|message) (?:to|for) (?:the )?(?:ai|assistant|llm|language model|model|agent|chatbot)\b|\b(?:ai|assistant|agent|llm)s? reading this\b|\bif you are an? (?:ai|llm|language model|assistant)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex AddressAssistant();

    [GeneratedRegex(@"(?<![A-Za-z0-9+/=_-])[A-Za-z0-9+/_-]{24,}={0,2}(?![A-Za-z0-9+/=_-])", RegexOptions.CultureInvariant, 500)]
    private static partial Regex Base64();

    [GeneratedRegex(@"<!--(?<t>[\s\S]{0,4000}?)-->|<(?<tag>\w+)[^>]*\bstyle\s*=\s*[""'][^""']*(?:display\s*:\s*none|visibility\s*:\s*hidden|font-size\s*:\s*0|opacity\s*:\s*0(?:\.0+)?\b|color\s*:\s*(?:#fff(?:fff)?|white))[^""']*[""'][^>]*>(?<t>[\s\S]{0,4000}?)</\k<tag>>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex HiddenHtml();

    [GeneratedRegex(@"[\u202A-\u202E\u2066-\u2069]+", RegexOptions.CultureInvariant, 500)]
    private static partial Regex Bidi();
}
