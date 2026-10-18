using InvoiceMesh.Core.Schemas;

namespace InvoiceMesh.Core.Mapping;

/// <summary>
/// The schema and the mapping a new installation starts with: the usual fields of an invoice, filled from the business terms of EN 16931. Because every
/// reader (UBL, CII, PDF text, scans) writes those terms, this one mapping works for every format; a user changes both freely, or adds mappings for a
/// format, a profile or a version that should be read differently.
/// </summary>
public static class Defaults
{
    public static SchemaDefinition Schema() => new()
    {
        Groups =
        [
            Group("invoice", "Rechnung", "Kopfdaten der Rechnung.", false,
                F("number", "Rechnungsnummer", FieldType.Text, true),
                F("issueDate", "Rechnungsdatum", FieldType.Date, true),
                F("typeCode", "Rechnungsart", FieldType.Code, description: "380 Rechnung, 381 Gutschrift, 384 Korrekturrechnung ..."),
                F("currency", "Währung", FieldType.Code, true),
                F("dueDate", "Fällig am", FieldType.Date),
                F("buyerReference", "Käuferreferenz / Leitweg-ID", FieldType.Text),
                F("orderReference", "Bestellnummer", FieldType.Text),
                F("deliveryDate", "Lieferdatum", FieldType.Date),
                F("periodStart", "Leistungszeitraum von", FieldType.Date),
                F("periodEnd", "Leistungszeitraum bis", FieldType.Date),
                F("paymentTerms", "Zahlungsbedingungen", FieldType.Text),
                F("note", "Bemerkung", FieldType.Text)),
            Group("seller", "Lieferant", "Der Verkäufer, der die Rechnung stellt.", false,
                F("name", "Name", FieldType.Text, true),
                F("vatId", "USt-IdNr.", FieldType.Text),
                F("taxNumber", "Steuernummer", FieldType.Text),
                F("street", "Straße", FieldType.Text),
                F("postcode", "PLZ", FieldType.Text),
                F("city", "Ort", FieldType.Text),
                F("country", "Land", FieldType.Code),
                F("contact", "Ansprechpartner", FieldType.Text),
                F("phone", "Telefon", FieldType.Text),
                F("email", "E-Mail", FieldType.Email),
                F("iban", "IBAN", FieldType.Text),
                F("bic", "BIC", FieldType.Text)),
            Group("buyer", "Kunde", "Der Käufer, an den die Rechnung geht.", false,
                F("name", "Name", FieldType.Text, true),
                F("vatId", "USt-IdNr.", FieldType.Text),
                F("street", "Straße", FieldType.Text),
                F("postcode", "PLZ", FieldType.Text),
                F("city", "Ort", FieldType.Text),
                F("country", "Land", FieldType.Code)),
            Group("lines", "Positionen", "Die Rechnungspositionen.", true,
                F("position", "Pos.", FieldType.Text),
                F("name", "Bezeichnung", FieldType.Text, true),
                F("description", "Beschreibung", FieldType.Text),
                F("articleNumber", "Artikelnummer", FieldType.Text),
                F("quantity", "Menge", FieldType.Decimal),
                F("unit", "Einheit", FieldType.Code),
                F("unitPrice", "Einzelpreis", FieldType.Money),
                F("netAmount", "Nettobetrag", FieldType.Money, true),
                F("taxCategory", "Steuerkategorie", FieldType.Code),
                F("taxRate", "Steuersatz", FieldType.Percent)),
            Group("taxes", "Steuern", "Die Aufschlüsselung der Umsatzsteuer.", true,
                F("category", "Kategorie", FieldType.Code),
                F("rate", "Satz", FieldType.Percent),
                F("base", "Bemessungsgrundlage", FieldType.Money),
                F("amount", "Steuerbetrag", FieldType.Money),
                F("exemptionReason", "Befreiungsgrund", FieldType.Text)),
            Group("totals", "Summen", "Die Gesamtbeträge.", false,
                F("lineTotal", "Summe Positionen", FieldType.Money),
                F("allowances", "Nachlässe", FieldType.Money),
                F("charges", "Zuschläge", FieldType.Money),
                F("netTotal", "Netto", FieldType.Money, true),
                F("taxTotal", "Umsatzsteuer", FieldType.Money),
                F("grossTotal", "Brutto", FieldType.Money, true),
                F("prepaid", "Bereits gezahlt", FieldType.Money),
                F("amountDue", "Zahlbetrag", FieldType.Money)),
            Group("source", "Herkunft", "Woher die Daten stammen.", false,
                F("fileName", "Datei", FieldType.Text),
                F("format", "Format", FieldType.Text),
                F("syntax", "Syntax", FieldType.Code),
                F("standard", "Standard", FieldType.Text),
                F("profile", "Profil", FieldType.Text),
                F("version", "Version", FieldType.Text),
                F("method", "Verfahren", FieldType.Code)),
        ],
    };

    public static MappingDefinition Mapping() => new()
    {
        Collections =
        [
            new CollectionBinding { Group = "lines", Source = "bg:BG-25" },
            new CollectionBinding { Group = "taxes", Source = "bg:BG-23" },
        ],
        Rules =
        [
            R("invoice.number", "bt:BT-1"),
            R("invoice.issueDate", "bt:BT-2"),
            R("invoice.typeCode", "bt:BT-3"),
            R("invoice.currency", "bt:BT-5") with { Transforms = [T("upper")] },
            R("invoice.dueDate", "bt:BT-9"),
            R("invoice.buyerReference", "bt:BT-10"),
            R("invoice.orderReference", "bt:BT-13"),
            R("invoice.deliveryDate", "bt:BT-72"),
            R("invoice.periodStart", "bt:BT-73"),
            R("invoice.periodEnd", "bt:BT-74"),
            R("invoice.paymentTerms", "bt:BT-20"),
            R("invoice.note", "bt:BT-22"),

            R("seller.name", "bt:BT-27", "bt:BT-28") with { IgnoreDifferences = true, Note = "Rechtlicher Name, sonst Handelsname." },
            R("seller.vatId", "bt:BT-31") with { Transforms = [T("compact")] },
            R("seller.taxNumber", "bt:BT-32"),
            R("seller.street", "bt:BT-35"),
            R("seller.postcode", "bt:BT-38"),
            R("seller.city", "bt:BT-37"),
            R("seller.country", "bt:BT-40") with { Transforms = [T("upper")] },
            R("seller.contact", "bt:BT-41"),
            R("seller.phone", "bt:BT-42"),
            R("seller.email", "bt:BT-43") with { Transforms = [T("trim"), T("lower")] },
            R("seller.iban", "bt:BT-84") with { Transforms = [T("compact")] },
            R("seller.bic", "bt:BT-86") with { Transforms = [T("compact")] },

            R("buyer.name", "bt:BT-44", "bt:BT-45") with { IgnoreDifferences = true },
            R("buyer.vatId", "bt:BT-48") with { Transforms = [T("compact")] },
            R("buyer.street", "bt:BT-50"),
            R("buyer.postcode", "bt:BT-53"),
            R("buyer.city", "bt:BT-52"),
            R("buyer.country", "bt:BT-55") with { Transforms = [T("upper")] },

            R("lines.position", "bt:BT-126"),
            R("lines.name", "bt:BT-153"),
            R("lines.description", "bt:BT-154"),
            R("lines.articleNumber", "bt:BT-155"),
            R("lines.quantity", "bt:BT-129"),
            R("lines.unit", "bt:BT-130"),
            R("lines.unitPrice", "bt:BT-146"),
            R("lines.netAmount", "bt:BT-131"),
            R("lines.taxCategory", "bt:BT-151"),
            R("lines.taxRate", "bt:BT-152"),

            R("taxes.category", "bt:BT-118"),
            R("taxes.rate", "bt:BT-119"),
            R("taxes.base", "bt:BT-116"),
            R("taxes.amount", "bt:BT-117"),
            R("taxes.exemptionReason", "bt:BT-120"),

            R("totals.lineTotal", "bt:BT-106"),
            R("totals.allowances", "bt:BT-107"),
            R("totals.charges", "bt:BT-108"),
            R("totals.netTotal", "bt:BT-109"),
            R("totals.taxTotal", "bt:BT-110"),
            R("totals.grossTotal", "bt:BT-112"),
            R("totals.prepaid", "bt:BT-113"),
            R("totals.amountDue", "bt:BT-115"),

            R("source.fileName", "meta:fileName"),
            R("source.format", "meta:format"),
            R("source.syntax", "meta:syntax"),
            R("source.standard", "meta:standard"),
            R("source.profile", "meta:profile"),
            R("source.version", "meta:version"),
            R("source.method", "meta:method"),
        ],
    };

    private static SchemaGroup Group(string key, string label, string description, bool repeating, params SchemaField[] fields)
        => new() { Key = key, Label = label, Description = description, Repeating = repeating, Fields = [.. fields] };

    private static SchemaField F(string key, string label, FieldType type, bool required = false, string? description = null)
        => new() { Key = key, Label = label, Type = type, Required = required, Description = description };

    private static RuleBuilder R(string target, params string[] sources) => new(target, [.. sources]);

    private static TransformStep T(string op, string? arg = null) => new() { Op = op, Arg = arg };

    /// <summary>A rule in the table above; turned into a <see cref="MappingRule"/> when the list is built.</summary>
    private sealed record RuleBuilder(string Target, List<string> Sources)
    {
        public List<TransformStep> Transforms { get; init; } = [];

        public bool IgnoreDifferences { get; init; }

        public string? Note { get; init; }

        public static implicit operator MappingRule(RuleBuilder b)
            => new() { Target = b.Target, Sources = b.Sources, Transforms = b.Transforms, IgnoreDifferences = b.IgnoreDifferences, Note = b.Note };
    }
}
