using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InvoiceMesh.Core.Extraction;

namespace InvoiceMesh.Core.Schemas;

/// <summary>Turns the text a mapping produced into the typed value of a field of a schema, and says why when it cannot.</summary>
public static class FieldValues
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "yyyyMMdd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ"];

    /// <summary>
    /// The typed value: a JSON number for numbers, <c>yyyy-MM-dd</c> for dates, true or false. Numbers in the invariant form are taken as they are; a number
    /// written the German or English way ("1.234,56") is read as well.
    /// </summary>
    public static bool TryConvert(SchemaField field, string text, out JsonNode? value, out string? message)
    {
        value = null;
        message = null;
        var t = text.Trim();
        switch (field.Type)
        {
            case FieldType.Integer:
                if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                {
                    value = JsonValue.Create(l);
                    return true;
                }

                if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var whole) && whole == Math.Truncate(whole))
                {
                    value = JsonValue.Create((long)whole);
                    return true;
                }

                message = $"„{t}“ ist keine ganze Zahl.";
                return false;

            case FieldType.Decimal or FieldType.Money or FieldType.Percent:
                if (decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var d)
                    || TextValues.TryParseNumber(t, out d))
                {
                    value = JsonValue.Create(d);
                    return true;
                }

                message = $"„{t}“ ist keine Zahl.";
                return false;

            case FieldType.Date:
                if (DateTime.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt))
                {
                    value = JsonValue.Create(dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    return true;
                }

                message = $"„{t}“ ist kein Datum (erwartet z. B. 2026-10-18 oder 18.10.2026).";
                return false;

            case FieldType.Boolean:
                switch (t.ToLowerInvariant())
                {
                    case "true" or "ja" or "yes" or "1" or "x" or "wahr":
                        value = JsonValue.Create(true);
                        return true;
                    case "false" or "nein" or "no" or "0" or "falsch":
                        value = JsonValue.Create(false);
                        return true;
                    default:
                        message = $"„{t}“ ist kein Wahrheitswert (ja/nein).";
                        return false;
                }

            case FieldType.Email:
                if (!Regex.IsMatch(t, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                {
                    message = $"„{t}“ ist keine E-Mail-Adresse.";
                    return false;
                }

                break;
        }

        if (field.AllowedValues.Count > 0 && !field.AllowedValues.Contains(t, StringComparer.OrdinalIgnoreCase))
        {
            message = $"„{t}“ ist nicht erlaubt (erlaubt: {string.Join(", ", field.AllowedValues)}).";
            return false;
        }

        value = JsonValue.Create(t);
        return true;
    }

    /// <summary>A typed value as text again, for tables, CSV and XML: numbers invariant, dates as yyyy-MM-dd.</summary>
    public static string? AsText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<decimal>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    public static string TypeLabel(FieldType type) => type switch
    {
        FieldType.Text => "Text",
        FieldType.Integer => "Ganzzahl",
        FieldType.Decimal => "Dezimalzahl",
        FieldType.Money => "Geldbetrag",
        FieldType.Percent => "Prozent",
        FieldType.Date => "Datum",
        FieldType.Boolean => "Ja/Nein",
        FieldType.Code => "Code",
        _ => "E-Mail",
    };
}
