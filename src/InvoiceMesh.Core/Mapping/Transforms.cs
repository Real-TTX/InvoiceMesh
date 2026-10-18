using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceMesh.Core.Extraction;

namespace InvoiceMesh.Core.Mapping;

/// <summary>An operation of the transformation, for the pages: its name, what it does and what its arguments mean.</summary>
public sealed record TransformInfo(string Op, string Label, string Description, string? ArgLabel = null, string? Arg2Label = null);

/// <summary>
/// The transformations a rule can apply to a value, in order. They are data (an operation and up to two arguments), so a new rule needs no program code;
/// a new kind of operation is one entry here. Regular expressions run with a time limit.
/// </summary>
public static class Transforms
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    public static readonly IReadOnlyList<TransformInfo> All =
    [
        new("trim", "Leerzeichen entfernen", "Entfernt Leerzeichen am Anfang und am Ende."),
        new("collapse", "Leerraum vereinheitlichen", "Fasst Zeilenumbrüche und mehrere Leerzeichen zu einem Leerzeichen zusammen."),
        new("upper", "Großbuchstaben", "Schreibt alles groß (z. B. Ländercodes)."),
        new("lower", "Kleinbuchstaben", "Schreibt alles klein (z. B. E-Mail-Adressen)."),
        new("firstline", "Erste Zeile", "Nimmt nur die erste Zeile eines mehrzeiligen Werts."),
        new("digits", "Nur Ziffern", "Entfernt alles außer Ziffern (z. B. Telefonnummern, Kundennummern)."),
        new("compact", "Kennung kompakt", "Entfernt Leerzeichen, Punkte und Bindestriche und schreibt groß (IBAN, USt-IdNr.)."),
        new("replace", "Ersetzen", "Ersetzt einen regulären Ausdruck durch einen Text.", "Muster (regulärer Ausdruck)", "Ersetzung"),
        new("prefix", "Voranstellen", "Stellt einen Text vor den Wert.", "Text"),
        new("suffix", "Anhängen", "Hängt einen Text an den Wert an.", "Text"),
        new("substring", "Ausschnitt", "Nimmt einen Teil des Werts.", "Start (ab 0)", "Länge (leer: bis zum Ende)"),
        new("split", "Aufteilen", "Teilt den Wert an einem Trennzeichen und nimmt einen Teil (Felder aufteilen).", "Trennzeichen", "Teil (ab 0, -1 = letzter)"),
        new("date", "Datum vereinheitlichen", "Liest ein Datum in den angegebenen Formaten und schreibt es als JJJJ-MM-TT.", "Formate, getrennt durch ; (z. B. dd.MM.yyyy;d.M.yy)"),
        new("number", "Zahl vereinheitlichen", "Liest eine Zahl mit Komma oder Punkt und Tausendertrennern und schreibt sie einheitlich.", "Dezimaltrennzeichen (, oder .; leer: erkennen)"),
        new("round", "Runden", "Rundet eine Zahl kaufmännisch.", "Nachkommastellen"),
        new("multiply", "Multiplizieren", "Multipliziert eine Zahl (z. B. -1 für Gutschriften, 0.01 für Prozent).", "Faktor"),
        new("map", "Werte übersetzen", "Übersetzt Werte nach einer Tabelle (z. B. 380=Rechnung;381=Gutschrift).", "Tabelle: alt=neu;alt=neu", "Sonst (leer: Wert behalten)"),
        new("default", "Ersatzwert", "Setzt einen Wert, wenn bis hierher keiner da ist.", "Wert"),
    ];

    public static TransformInfo? Info(string op) => All.FirstOrDefault(t => string.Equals(t.Op, op, StringComparison.OrdinalIgnoreCase));

    /// <summary>Applies one step. An error (a date in no given format) leaves the value as it was and says why.</summary>
    public static string? Apply(string? value, TransformStep step, out string? error)
    {
        error = null;
        var op = step.Op.ToLowerInvariant();
        if (op == "default")
        {
            return string.IsNullOrEmpty(value) ? step.Arg : value;
        }

        if (value is null)
        {
            return null;
        }

        try
        {
            switch (op)
            {
                case "trim":
                    return value.Trim();
                case "collapse":
                    return Regex.Replace(value, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
                case "upper":
                    return value.ToUpperInvariant();
                case "lower":
                    return value.ToLowerInvariant();
                case "firstline":
                    return value.Split('\n')[0].Trim();
                case "digits":
                    return new string(value.Where(char.IsDigit).ToArray());
                case "compact":
                    return TextValues.CompactId(value);
                case "replace":
                    return Regex.Replace(value, step.Arg ?? string.Empty, step.Arg2 ?? string.Empty, RegexOptions.None, RegexTimeout);
                case "prefix":
                    return (step.Arg ?? string.Empty) + value;
                case "suffix":
                    return value + (step.Arg ?? string.Empty);
                case "substring":
                    {
                        var start = Math.Clamp(Int(step.Arg) ?? 0, 0, value.Length);
                        var length = Int(step.Arg2);
                        return length is { } n ? value.Substring(start, Math.Clamp(n, 0, value.Length - start)) : value[start..];
                    }

                case "split":
                    {
                        var separator = string.IsNullOrEmpty(step.Arg) ? " " : step.Arg.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\t", "\t", StringComparison.Ordinal);
                        var parts = value.Split(separator, StringSplitOptions.TrimEntries);
                        var index = Int(step.Arg2) ?? 0;
                        if (index < 0)
                        {
                            index = parts.Length + index;
                        }

                        return index >= 0 && index < parts.Length ? parts[index] : null;
                    }

                case "date":
                    return Date(value, step.Arg, out error);
                case "number":
                    return Number(value, step.Arg, out error);
                case "round":
                    if (!TryDecimal(value, out var r))
                    {
                        error = $"„{value}“ ist keine Zahl zum Runden.";
                        return value;
                    }

                    return Math.Round(r, Math.Clamp(Int(step.Arg) ?? 2, 0, 10), MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
                case "multiply":
                    if (!TryDecimal(value, out var m) || !TryDecimal(step.Arg ?? "", out var factor))
                    {
                        error = $"„{value}“ oder der Faktor „{step.Arg}“ ist keine Zahl.";
                        return value;
                    }

                    return (m * factor).ToString("0.############", CultureInfo.InvariantCulture);
                case "map":
                    {
                        foreach (var pair in (step.Arg ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var eq = pair.IndexOf('=', StringComparison.Ordinal);
                            if (eq > 0 && string.Equals(pair[..eq].Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                return pair[(eq + 1)..].Trim();
                            }
                        }

                        return string.IsNullOrEmpty(step.Arg2) ? value : step.Arg2;
                    }

                default:
                    error = $"Unbekannte Transformation „{step.Op}“.";
                    return value;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            error = "Der reguläre Ausdruck brauchte zu lange und wurde abgebrochen.";
            return value;
        }
        catch (ArgumentException ex)
        {
            error = $"Ungültiger regulärer Ausdruck: {ex.Message}";
            return value;
        }
    }

    private static int? Int(string? text) => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static bool TryDecimal(string text, out decimal value)
        => decimal.TryParse(text.Trim(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out value) || TextValues.TryParseNumber(text, out value);

    private static string Date(string value, string? formats, out string? error)
    {
        error = null;
        var t = value.Trim();
        if (DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            return iso.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var list = (formats ?? "dd.MM.yyyy;d.M.yyyy;yyyyMMdd;dd/MM/yyyy").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("de-DE"), CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("fr-FR") })
        {
            if (DateTime.TryParseExact(t, list, culture, DateTimeStyles.AllowWhiteSpaces, out var d))
            {
                return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        error = $"„{t}“ passt zu keinem der Datumsformate ({string.Join(", ", list)}).";
        return value;
    }

    private static string Number(string value, string? decimalSeparator, out string? error)
    {
        error = null;
        var t = value.Trim();
        decimal result;
        if (decimalSeparator is "," or ".")
        {
            var other = decimalSeparator == "," ? "." : ",";
            var cleaned = Regex.Replace(t, @"[^\d,.\-]", string.Empty).Replace(other, string.Empty, StringComparison.Ordinal).Replace(decimalSeparator, ".", StringComparison.Ordinal);
            if (!decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result))
            {
                error = $"„{t}“ ist keine Zahl.";
                return value;
            }
        }
        else if (!TextValues.TryParseNumber(t, out result) && !TextValues.TryParseAmount(t, out result))
        {
            error = $"„{t}“ ist keine Zahl.";
            return value;
        }

        return result.ToString("0.############", CultureInfo.InvariantCulture);
    }
}
