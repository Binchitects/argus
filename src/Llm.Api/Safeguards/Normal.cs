using System.Text;

namespace Llm.Api.Safeguards;

/// <summary>
/// A text as the word and injection detectors read it besides as written, so a disguise does not get past them: zero-width,
/// joining and direction characters and Unicode's hidden tag characters dropped; full-width letters, ligatures and other
/// compatibility forms as their plain letters (NFKC); Cyrillic and Greek letters that look Latin as the Latin ones; Persian
/// and Arabic digits as 0-9; digits and signs between letters as the letters they stand for ("p@ssw0rd"); letters spaced
/// out one by one joined again ("i g n o r e"); all in lower case. Each character keeps the place in the text it came from,
/// so a finding masks what was written.
/// </summary>
public sealed class Normal
{
    private Normal(string text, int[] map)
    {
        Text = text;
        Map = map;
    }

    /// <summary>The text as read.</summary>
    public string Text { get; }

    /// <summary>Each of <see cref="Text"/>'s characters' place in the text as written.</summary>
    public int[] Map { get; }

    /// <summary>Where a span of <see cref="Text"/> is in the text as written.</summary>
    public (int Index, int Length) Original(int index, int length)
    {
        var start = Map[index];
        var end = Map[Math.Min(index + length, Map.Length) - 1] + 1;
        return (start, end - start);
    }

    /// <summary>The findings of a detector run on the read text, placed in the text as written.</summary>
    public IEnumerable<Finding> Placed(IEnumerable<Finding> found) =>
        found.Where(f => f.Length > 0).Select(f => Original(f.Index, f.Length) is var (i, n) ? f with { Index = i, Length = n } : f);

    private static readonly Dictionary<char, char> LookAlike = new()
    {
        // Cyrillic
        ['а'] = 'a', ['в'] = 'b', ['е'] = 'e', ['ё'] = 'e', ['к'] = 'k', ['м'] = 'm', ['н'] = 'h', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c',
        ['т'] = 't', ['у'] = 'y', ['х'] = 'x', ['і'] = 'i', ['ї'] = 'i', ['ј'] = 'j', ['ѕ'] = 's', ['ԁ'] = 'd', ['ԛ'] = 'q', ['ԝ'] = 'w',
        ['ɡ'] = 'g', ['ɩ'] = 'i', ['ⅰ'] = 'i', ['ℓ'] = 'l',
        // Greek
        ['α'] = 'a', ['β'] = 'b', ['ε'] = 'e', ['ι'] = 'i', ['κ'] = 'k', ['ν'] = 'v', ['ο'] = 'o', ['ρ'] = 'p', ['τ'] = 't', ['υ'] = 'u',
        ['χ'] = 'x', ['γ'] = 'y', ['ω'] = 'w', ['η'] = 'n',
    };

    // Digits and signs that stand for letters, read so only between letters (a number stays a number).
    private static readonly Dictionary<char, char> Leet = new()
    {
        ['0'] = 'o', ['1'] = 'i', ['3'] = 'e', ['4'] = 'a', ['5'] = 's', ['7'] = 't', ['8'] = 'b', ['@'] = 'a', ['$'] = 's', ['!'] = 'i', ['|'] = 'l',
    };

    private static bool Hidden(int cp) =>
        cp is 0x00AD or 0x034F or 0x061C or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x180E or (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E)
            or (>= 0x2060 and <= 0x2064) or (>= 0x2066 and <= 0x206F) or 0xFEFF or (>= 0xE0000 and <= 0xE007F) or (>= 0xFE00 and <= 0xFE0F);

    public static Normal Of(string text)
    {
        var chars = new List<char>(text.Length);
        var map = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var at = i;
            int cp;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else
            {
                cp = text[i];
            }
            if (Hidden(cp))
            {
                continue;
            }
            var plain = char.ConvertFromUtf32(cp).Normalize(NormalizationForm.FormKC);
            foreach (var c0 in plain)
            {
                var c = char.ToLowerInvariant(c0);
                c = c switch
                {
                    >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                    >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                    _ => LookAlike.GetValueOrDefault(c, c),
                };
                chars.Add(c);
                map.Add(at);
            }
        }
        // Digits and signs inside a word, or starting one, as letters ("1gn0re"); a number on its own stays a number.
        for (var i = 0; i < chars.Count - 1; i++)
        {
            var before = i == 0 ? ' ' : chars[i - 1];
            if (Leet.TryGetValue(chars[i], out var letter) && (char.IsLetter(before) || !char.IsLetterOrDigit(before)) && char.IsLetter(chars[i + 1])
                || Leet.TryGetValue(chars[i], out letter) && char.IsLetter(before) && Leet.ContainsKey(chars[i + 1]))
            {
                chars[i] = letter;
            }
        }
        // Letters spaced out one by one (four or more: "i g n o r e", "i.g.n.o.r.e") joined again.
        static bool Sep(char c) => c is ' ' or '.' or '-' or '_' or '*';
        var outChars = new StringBuilder(chars.Count);
        var outMap = new List<int>(chars.Count);
        var n = chars.Count;
        for (var i = 0; i < n; i++)
        {
            if (char.IsLetter(chars[i]) && (i == 0 || !char.IsLetter(chars[i - 1])))
            {
                var j = i;
                var count = 1;
                while (j + 2 < n && Sep(chars[j + 1]) && char.IsLetter(chars[j + 2]) && (j + 3 >= n || !char.IsLetter(chars[j + 3])))
                {
                    j += 2;
                    count++;
                }
                if (count >= 4)
                {
                    for (var k = i; k <= j; k += 2)
                    {
                        outChars.Append(chars[k]);
                        outMap.Add(map[k]);
                    }
                    i = j;
                    continue;
                }
            }
            outChars.Append(chars[i]);
            outMap.Add(map[i]);
        }
        return new Normal(outChars.ToString(), [.. outMap]);
    }

    /// <summary>Persian and Arabic digits as 0-9, one for one (places unchanged): personal data written in them is found.</summary>
    public static string LatinDigits(string text)
    {
        if (!text.Any(c => c is >= '۰' and <= '۹' or >= '٠' and <= '٩'))
        {
            return text;
        }
        var sb = new StringBuilder(text);
        for (var i = 0; i < sb.Length; i++)
        {
            sb[i] = sb[i] switch
            {
                >= '۰' and <= '۹' => (char)('0' + (sb[i] - '۰')),
                >= '٠' and <= '٩' => (char)('0' + (sb[i] - '٠')),
                var c => c,
            };
        }
        return sb.ToString();
    }
}
