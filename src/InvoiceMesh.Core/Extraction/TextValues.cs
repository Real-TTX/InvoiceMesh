using System.Globalization;
using System.Text.RegularExpressions;

namespace InvoiceMesh.Core.Extraction;

/// <summary>
/// Numbers, dates and identifiers as people write them on invoices in several languages: <c>1.234,56</c>, <c>1,234.56</c>, <c>1 234,56 €</c>,
/// <c>18.10.2026</c>, <c>18 octobre 2026</c>, <c>October 18, 2026</c>, IBANs with spaces. Each parser says whether it was sure.
/// </summary>
public static partial class TextValues
{
    /// <summary>A money amount or a number with decimals as it appears in text.</summary>
    public static readonly Regex AmountPattern = AmountRegex();

    // Thousands are grouped by a dot, a comma, an apostrophe (Switzerland) or a non-breaking space (France); a plain space is not taken, it would join
    // a quantity and a price ("1 100,00").
    [GeneratedRegex(@"(?<![\w.,])[-−]?\(?(?:\d{1,3}(?:[.,'’  ]\d{3})+|\d+)[.,]\d{2}\)?-?(?![\d])")]
    private static partial Regex AmountRegex();

    [GeneratedRegex(@"\b(\d{4})-(\d{1,2})-(\d{1,2})\b")]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"\b(\d{1,2})\s?([./-])\s?(\d{1,2})\s?\2\s?(\d{4}|\d{2})\b")]
    private static partial Regex NumericDateRegex();

    [GeneratedRegex(@"\b(\d{1,2})\.?\s+([A-Za-zÄÖÜäöüéûèàçñ]{3,10})\.?\s+(\d{4})\b")]
    private static partial Regex DayMonthNameRegex();

    [GeneratedRegex(@"\b([A-Za-z]{3,9})\.?\s+(\d{1,2}),?\s+(\d{4})\b")]
    private static partial Regex MonthNameDayRegex();

    private static readonly Dictionary<string, int> MonthNames = BuildMonths();

    private static Dictionary<string, int> BuildMonths()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string[][] names =
        [
            ["januar", "jänner", "january", "janvier", "enero", "gennaio", "januari", "jan"],
            ["februar", "february", "février", "fevrier", "febrero", "febbraio", "februari", "feb", "febr"],
            ["märz", "maerz", "march", "mars", "marzo", "maart", "mär", "mar", "mrz"],
            ["april", "avril", "abril", "aprile", "apr", "avr"],
            ["mai", "may", "mayo", "maggio", "mei"],
            ["juni", "june", "juin", "junio", "giugno", "jun"],
            ["juli", "july", "juillet", "julio", "luglio", "jul", "juil"],
            ["august", "août", "aout", "agosto", "augustus", "aug"],
            ["september", "septembre", "septiembre", "settembre", "sep", "sept"],
            ["oktober", "october", "octobre", "octubre", "ottobre", "okt", "oct"],
            ["november", "novembre", "noviembre", "nov"],
            ["dezember", "december", "décembre", "decembre", "diciembre", "dicembre", "dez", "dec", "déc"],
        ];
        for (var m = 0; m < names.Length; m++)
        {
            foreach (var n in names[m])
            {
                map[n] = m + 1;
            }
        }

        return map;
    }

    /// <summary>
    /// Reads an amount: the last separator followed by exactly two digits is the decimal one. "(12,00)" and "12,00-" are negative.
    /// </summary>
    public static bool TryParseAmount(string text, out decimal value)
    {
        value = 0;
        var t = text.Trim().Replace('−', '-');
        var negative = t.StartsWith('-') || t.EndsWith('-') || (t.StartsWith('(') && t.EndsWith(')'));
        t = Regex.Replace(t, @"[^\d.,]", string.Empty);
        if (t.Length == 0)
        {
            return false;
        }

        var lastDot = t.LastIndexOf('.');
        var lastComma = t.LastIndexOf(',');
        var decimalAt = Math.Max(lastDot, lastComma);
        string integerPart, fraction;
        if (decimalAt >= 0 && t.Length - decimalAt - 1 is 1 or 2)
        {
            integerPart = t[..decimalAt];
            fraction = t[(decimalAt + 1)..];
        }
        else if (decimalAt >= 0 && t.Length - decimalAt - 1 == 3 && (lastDot < 0 || lastComma < 0) && t.Count(c => c == t[decimalAt]) == 1 && decimalAt <= 3)
        {
            // "1.234" or "1,234": a thousands separator in a money context (amounts with three decimals are rare on invoices).
            integerPart = t;
            fraction = string.Empty;
        }
        else if (decimalAt >= 0 && t.Length - decimalAt - 1 > 3)
        {
            return false;
        }
        else
        {
            integerPart = t;
            fraction = string.Empty;
        }

        integerPart = integerPart.Replace(".", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal);
        if (integerPart.Length == 0)
        {
            integerPart = "0";
        }

        if (!decimal.TryParse(integerPart + (fraction.Length > 0 ? "." + fraction : string.Empty), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (negative)
        {
            value = -value;
        }

        return true;
    }

    /// <summary>A plain number (a quantity, a percentage): "2", "1,5", "0.25".</summary>
    public static bool TryParseNumber(string text, out decimal value)
    {
        var t = text.Trim().TrimEnd('%').Trim();
        if (Regex.IsMatch(t, @"^-?\d+$"))
        {
            return decimal.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        if (Regex.IsMatch(t, @"^-?\d+[.,]\d{1,4}$"))
        {
            return decimal.TryParse(t.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
        }

        return TryParseAmount(t, out value);
    }

    /// <summary>
    /// Finds the first date in a text. Numeric dates are read day first (Europe); with <paramref name="monthFirst"/> (American text) a date whose
    /// first number could be a month is read month first, which is reported as less sure.
    /// </summary>
    public static bool TryFindDate(string text, bool monthFirst, out DateOnly date, out bool ambiguous)
    {
        ambiguous = false;
        date = default;
        var candidates = new List<(int Index, DateOnly Date, bool Ambiguous)>();

        foreach (Match m in IsoDateRegex().Matches(text))
        {
            if (TryDate(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), out var d))
            {
                candidates.Add((m.Index, d, false));
            }
        }

        foreach (Match m in NumericDateRegex().Matches(text))
        {
            var a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var b = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var y = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            if (y < 100)
            {
                y += 2000;
            }

            var bothPossible = a <= 12 && b <= 12 && a != b;
            var useMonthFirst = monthFirst && m.Groups[2].Value == "/" && a <= 12;
            var (day, month) = useMonthFirst ? (b, a) : (a, b);
            if (!TryDate(y, month, day, out var d))
            {
                // 10/25/2026 cannot be read day first: the other order is the only one possible, so it is not ambiguous.
                if (!TryDate(y, day, month, out d))
                {
                    continue;
                }

                bothPossible = false;
            }

            candidates.Add((m.Index, d, bothPossible && m.Groups[2].Value == "/"));
        }

        foreach (Match m in DayMonthNameRegex().Matches(text))
        {
            if (MonthNames.TryGetValue(m.Groups[2].Value.TrimEnd('.'), out var month)
                && TryDate(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), month, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), out var d))
            {
                candidates.Add((m.Index, d, false));
            }
        }

        foreach (Match m in MonthNameDayRegex().Matches(text))
        {
            if (MonthNames.TryGetValue(m.Groups[1].Value.TrimEnd('.'), out var month)
                && TryDate(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), month, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), out var d))
            {
                candidates.Add((m.Index, d, false));
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        var first = candidates.OrderBy(c => c.Index).First();
        date = first.Date;
        ambiguous = first.Ambiguous;
        return true;
    }

    private static bool TryDate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year is < 1990 or > 2100 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    /// <summary>Checks the check digits of an IBAN (ISO 13616, modulo 97).</summary>
    public static bool IsValidIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban))
        {
            return false;
        }

        var s = iban.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (s.Length is < 15 or > 34 || !Regex.IsMatch(s, "^[A-Z]{2}[0-9]{2}[A-Z0-9]+$"))
        {
            return false;
        }

        var rearranged = s[4..] + s[..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            var digits = char.IsLetter(c) ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString();
            foreach (var d in digits)
            {
                remainder = (remainder * 10 + (d - '0')) % 97;
            }
        }

        return remainder == 1;
    }

    private static readonly Regex[] VatPatterns =
    [
        new(@"^ATU\d{8}$"), new(@"^BE[01]\d{9}$"), new(@"^BG\d{9,10}$"), new(@"^CY\d{8}[A-Z]$"), new(@"^CZ\d{8,10}$"), new(@"^DE\d{9}$"),
        new(@"^DK\d{8}$"), new(@"^EE\d{9}$"), new(@"^EL\d{9}$"), new(@"^ES[A-Z0-9]\d{7}[A-Z0-9]$"), new(@"^FI\d{8}$"), new(@"^FR[A-HJ-NP-Z0-9]{2}\d{9}$"),
        new(@"^HR\d{11}$"), new(@"^HU\d{8}$"), new(@"^IE\d[A-Z0-9+*]\d{5}[A-Z]{1,2}$"), new(@"^IT\d{11}$"), new(@"^LT(\d{9}|\d{12})$"), new(@"^LU\d{8}$"),
        new(@"^LV\d{11}$"), new(@"^MT\d{8}$"), new(@"^NL\d{9}B\d{2}$"), new(@"^PL\d{10}$"), new(@"^PT\d{9}$"), new(@"^RO\d{2,10}$"), new(@"^SE\d{12}$"),
        new(@"^SI\d{8}$"), new(@"^SK\d{10}$"), new(@"^GB(\d{9}|\d{12})$"), new(@"^XI\d{9}$"), new(@"^CHE\d{9}(MWST|TVA|IVA)?$"), new(@"^NO\d{9}(MVA)?$"),
    ];

    /// <summary>Whether a text is a VAT identification number in the form of its country (not the check digits, which differ per country).</summary>
    public static bool LooksLikeVatId(string candidate) => VatPatterns.Any(p => p.IsMatch(candidate));

    /// <summary>A VAT id without the spaces, dots and dashes people write into it.</summary>
    public static string CompactId(string text) => Regex.Replace(text.ToUpperInvariant(), @"[\s.\-]", string.Empty);
}
