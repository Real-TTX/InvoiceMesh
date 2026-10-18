using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceMesh.Core.Semantics;

namespace InvoiceMesh.Core.Extraction;

/// <summary>
/// Reads the business terms out of the text of an invoice (the text layer of a PDF, or what text recognition made of a scan) in German, English, French,
/// Spanish, Italian and Dutch. It only takes what it finds: a value is written with the line it came from and a confidence; nothing is computed or filled in
/// to make the invoice look complete (the one exception is the number of a line, BT-126, which is counted and marked as such). Values found right after a label are <see cref="Confidence.Medium"/>, values guessed from their shape or place are
/// <see cref="Confidence.Low"/>; an IBAN whose check digits are right is <see cref="Confidence.High"/>. Every "Label: Wert" line also becomes a raw field
/// (<c>text:Label</c>) that a mapping can use.
/// </summary>
public static partial class TextInvoiceExtractor
{
    public const string RawPrefix = "text:";

    private sealed record Line(int Number, string Text);

    private static readonly string[] InvoiceNumberLabels =
    [
        @"rechnungs?\s*-?\s*(?:nummer|nr\.?|no\.?)", @"rechnung\s+nr\.?", @"beleg\s*-?\s*(?:nummer|nr\.?)", @"invoice\s*(?:number|no\.?|nr\.?|#|id)", @"inv\.?\s*no\.?",
        @"facture\s*(?:n[°ºo]\.?|num[ée]ro)", @"n[°ºo]\.?\s*(?:de\s+)?facture", @"n[úu]mero\s+de\s+factura", @"factura\s*(?:n[°ºo]\.?|n[úu]m\.?|n[úu]mero)",
        @"fattura\s*(?:n[°º.]?|numero|nr\.?)", @"numero\s+(?:di\s+)?fattura", @"factuur\s*(?:nummer|nr\.?)", @"credit\s*note\s*(?:number|no\.?)", @"gutschrift\s*(?:nummer|nr\.?)",
    ];

    private static readonly string[] InvoiceDateLabels =
    [
        @"rechnungsdatum", @"datum\s+der\s+rechnung", @"invoice\s+date", @"date\s+of\s+invoice", @"date\s+(?:de\s+(?:la\s+)?)?facture", @"fecha\s+(?:de\s+(?:la\s+)?)?factura",
        @"fecha\s+de\s+emisi[óo]n", @"data\s+(?:della\s+)?fattura", @"factuurdatum", @"belegdatum", @"ausstellungsdatum", @"issue\s+date", @"date\s+d['’]?[ée]mission",
    ];

    private static readonly string[] GenericDateLabels = [@"datum", @"date", @"fecha", @"data"];

    private static readonly string[] DueDateLabels =
    [
        @"f[äa]llig(?:keit)?(?:sdatum)?(?:\s+am)?", @"zahlbar\s+bis(?:\s+zum)?", @"zahlungsziel", @"due\s+date", @"payment\s+due(?:\s+date)?", @"due\s+by",
        @"date\s+d['’]?[ée]ch[ée]ance", @"[ée]ch[ée]ance", @"fecha\s+de\s+vencimiento", @"vencimiento", @"data\s+di\s+scadenza", @"scadenza", @"vervaldatum", @"te\s+betalen\s+voor",
    ];

    private static readonly string[] OrderLabels =
    [
        @"bestell(?:nummer|nr\.?|ung\s*(?:nr\.?)?)", @"ihre\s+bestellung", @"auftrags?(?:nummer|nr\.?)", @"purchase\s+order(?:\s+(?:number|no\.?))?", @"p\.?o\.?\s*(?:number|no\.?|#)",
        @"order\s*(?:number|no\.?|ref(?:erence)?)", @"bon\s+de\s+commande", @"n[°º]\s*(?:de\s+)?commande", @"(?:n[úu]mero\s+de\s+)?pedido", @"ordine\s*(?:n[°.]?)?", @"bestelnummer",
    ];

    private static readonly string[] BuyerReferenceLabels = [@"leitweg\s*-?\s*id", @"buyer\s+reference", @"k[äa]uferreferenz", @"ihre\s+referenz", @"your\s+reference", @"votre\s+r[ée]f[ée]rence"];

    private static readonly string[] VatIdLabels =
    [
        @"ust\.?\s*-?\s*id(?:ent)?\.?\s*-?\s*(?:nr\.?|nummer)?", @"umsatzsteuer\s*-?\s*id(?:entifikationsnummer)?", @"vat\s*(?:reg(?:istration)?\.?\s*)?(?:no\.?|number|id)",
        @"vat", @"n[°º]?\s*(?:de\s+)?tva(?:\s+intracom(?:munautaire)?)?", @"tva\s+intracom(?:munautaire)?", @"nif", @"cif", @"n\.?\s*i\.?\s*f\.?", @"p\.?\s*iva", @"partita\s+iva",
        @"btw(?:\s*-?\s*(?:nummer|nr\.?|id))?", @"uid(?:\s*-?\s*nr\.?)?", @"mwst\s*-?\s*nr\.?",
    ];

    private static readonly string[] TaxNumberLabels = [@"steuer\s*-?\s*(?:nummer|nr\.?)", @"st\.?\s*-?\s*nr\.?", @"tax\s*(?:id|number|no\.?)"];

    private static readonly string[] GrossLabels =
    [
        @"gesamtbetrag(?:\s+brutto)?", @"rechnungsbetrag", @"endbetrag", @"bruttobetrag", @"gesamtsumme(?:\s+brutto)?", @"summe\s+brutto", @"zu\s+zahlen(?:der\s+betrag)?",
        @"zahlbetrag", @"total\s+(?:amount\s+)?due", @"amount\s+due", @"balance\s+due", @"grand\s+total", @"total\s+(?:incl\.?|including)\s+(?:vat|tax)", @"invoice\s+total",
        @"total\s+ttc", @"montant\s+ttc", @"net\s+[àa]\s+payer", @"total\s+(?:de\s+la\s+)?factura", @"importe\s+total", @"total\s+a\s+pagar", @"totale\s+(?:fattura|documento|da\s+pagare)",
        @"importo\s+totale", @"totaal\s*(?:bedrag)?(?:\s+incl\.?(?:\s+btw)?)?", @"te\s+betalen", @"brutto", @"total",
    ];

    private static readonly string[] NetLabels =
    [
        @"nettobetrag", @"netto\s*(?:summe|betrag)?", @"summe\s+netto", @"gesamt\s+netto", @"zwischensumme", @"warenwert", @"sub\s*-?\s*total", @"net\s+(?:amount|total)",
        @"total\s+(?:net|excl\.?|excluding)(?:\s+(?:vat|tax))?", @"total\s+ht", @"montant\s+ht", @"base\s+imponible", @"importe\s+neto", @"(?:totale\s+)?imponibile",
        @"subtotaal", @"totaal\s+excl\.?(?:\s+btw)?",
    ];

    private static readonly string[] TaxLabels =
    [
        @"(?:zzgl\.?\s*|inkl\.?\s*|enthaltene\s+)?(?:mwst\.?|ust\.?|umsatzsteuer|mehrwertsteuer)", @"vat", @"(?:sales\s+)?tax", @"tva", @"iva", @"i\.v\.a\.", @"btw", @"mva", @"moms",
    ];

    private static readonly string[] BuyerBlockLabels =
    [
        @"rechnungsempf[äa]nger", @"rechnungsadresse", @"rechnung\s+an", @"kunde", @"bill\s+to", @"billed\s+to", @"invoice\s+to", @"sold\s+to", @"customer", @"client",
        @"factur[ée]\s+[àa]", @"adresse\s+de\s+facturation", @"facturar\s+a", @"cliente", @"destinatario", @"klant", @"factuuradres",
    ];

    private static readonly string[] PaymentTermsLabels =
    [
        @"zahlbar", @"zahlungsbedingung(?:en)?", @"zahlungsziel", @"skonto", @"payable", @"payment\s+terms", @"terms\s+of\s+payment", @"conditions\s+de\s+(?:paiement|r[èe]glement)",
        @"condiciones\s+de\s+pago", @"forma\s+de\s+pago", @"termini\s+di\s+pagamento", @"condizioni\s+di\s+pagamento", @"betalingsvoorwaarden", @"betalingstermijn",
    ];

    private static readonly string[] DeliveryDateLabels = [@"liefer\s*datum", @"leistungsdatum", @"delivery\s+date", @"date\s+de\s+livraison", @"fecha\s+de\s+entrega", @"data\s+di\s+consegna", @"leverdatum"];

    private static readonly string[] PeriodLabels = [@"leistungszeitraum", @"abrechnungszeitraum", @"service\s+period", @"billing\s+period", @"p[ée]riode", @"periodo"];

    private static readonly string[] LineHeaderWords =
    [
        "pos", "menge", "anzahl", "beschreibung", "bezeichnung", "artikel", "leistung", "einzelpreis", "e-preis", "preis", "betrag", "gesamt", "einheit",
        "qty", "quantity", "description", "item", "unit", "price", "amount", "total", "quantité", "désignation", "prix", "montant", "cantidad", "descripción",
        "precio", "importe", "quantità", "descrizione", "prezzo", "importo", "aantal", "omschrijving", "prijs", "bedrag",
    ];

    private static readonly string[] LegalForms =
    [
        "GmbH", "AG", "UG", "KG", "OHG", "e.K.", "GbR", "mbH", "SE", "Ltd", "Limited", "LLC", "Inc", "Corp", "PLC", "S.A.", "SA", "S.L.", "SL", "SARL", "S.A.R.L.", "SAS", "S.p.A.", "SpA",
        "S.r.l.", "Srl", "B.V.", "BV", "N.V.", "NV", "AB", "A/S", "ApS", "Oy", "AS", "sp. z o.o.", "s.r.o.", "Kft",
    ];

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,4})?\b")]
    private static partial Regex IbanRegex();

    [GeneratedRegex(@"\b([A-Z]{4}[A-Z]{2}[A-Z0-9]{2}(?:[A-Z0-9]{3})?)\b")]
    private static partial Regex BicRegex();

    [GeneratedRegex(@"\b(?:ATU|CHE|[A-Z]{2})[\s.\-]?[0-9A-Z][0-9A-Z\s.\-]{6,18}[0-9A-Z]\b")]
    private static partial Regex VatCandidateRegex();

    [GeneratedRegex(@"[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?:tel(?:efon|ephone|éfono|efono)?\.?|phone|fon|t)\s*[:.]?\s*(\+?[\d][\d\s/()\-.]{5,}\d)", RegexOptions.IgnoreCase)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^\s*([A-Za-zÄÖÜäöüßéèàçñ][\wÄÖÜäöüßéèàçñ .\-/()°º#]{1,40}?)\s*[:：]\s*(\S.*?)\s*$")]
    private static partial Regex LabelValueRegex();

    [GeneratedRegex(@"(\d{1,2}(?:[.,]\d{1,2})?)\s?%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"\b(\d{4,5})\s+([A-ZÄÖÜ][\wäöüßéèàç\-]+(?:\s[A-ZÄÖÜ][\wäöüßéèàç\-]+)?)")]
    private static partial Regex PostcodeCityRegex();

    public sealed record Result(SemanticDocument Semantic, List<SourceField> RawFields, List<ExtractionMessage> Messages);

    /// <summary>Reads a text. <paramref name="ocrConfidence"/> (0 to 100) of a poor scan lowers every value to <see cref="Confidence.Low"/>.</summary>
    public static Result Extract(string text, double? ocrConfidence = null)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select((t, i) => new Line(i + 1, t.TrimEnd())).Where(l => l.Text.Trim().Length > 0).ToList();
        var doc = new SemanticDocument();
        var raw = new List<SourceField>();
        var messages = new List<ExtractionMessage>();
        var english = IsMostlyEnglish(text);

        ReadRawLabels(lines, raw);
        ReadDocumentType(lines, doc);
        ReadIdentifier(lines, InvoiceNumberLabels, "BT-1", doc, requireDigit: true);
        ReadDate(lines, InvoiceDateLabels, "BT-2", doc, english, messages);
        if (!doc.Has("BT-2"))
        {
            ReadDate(lines, GenericDateLabels, "BT-2", doc, english, messages, Confidence.Low);
        }

        ReadDate(lines, DueDateLabels, "BT-9", doc, english, messages);
        ReadDate(lines, DeliveryDateLabels, "BT-72", doc, english, messages);
        ReadPeriod(lines, doc, english);
        ReadIdentifier(lines, OrderLabels, "BT-13", doc, requireDigit: true);
        ReadIdentifier(lines, BuyerReferenceLabels, "BT-10", doc, requireDigit: false);
        ReadCurrency(text, doc);
        ReadParties(lines, doc);
        ReadVatIds(lines, doc);
        ReadIdentifier(lines, TaxNumberLabels, "BT-32", doc, requireDigit: true);
        ReadBank(lines, doc);
        ReadContact(lines, doc);
        ReadPaymentTerms(lines, doc);
        ReadTotals(lines, doc);
        ReadVatBreakdown(lines, doc);
        ReadLines(lines, doc, messages);

        if (ocrConfidence is { } c && c < 70)
        {
            messages.Add(new ExtractionMessage(MessageSeverity.Warning, "OCR-QUALITY", $"Die Texterkennung war unsicher (mittlere Konfidenz {c.ToString("0", CultureInfo.InvariantCulture)} %). Alle Werte sind als unsicher markiert."));
            Lower(doc);
        }

        if (doc.Fields.Count == 0)
        {
            messages.Add(new ExtractionMessage(MessageSeverity.Error, "TEXT-NOTHING", "Im Text wurden keine Rechnungsangaben gefunden."));
        }
        else
        {
            messages.Add(new ExtractionMessage(MessageSeverity.Info, "TEXT-HEURISTIC",
                "Die Werte wurden aus dem Text gelesen, nicht aus strukturierten Daten. Unsicher erkannte Werte sind markiert und sollten geprüft werden."));
        }

        return new Result(doc, raw, messages);
    }

    // ------------------------------------------------------------------------------------------------------------ labels

    private static Regex LabelRegex(string label) => new($@"(?<![\p{{L}}])(?:{label})(?![\p{{L}}])\s*[:#.]?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The text after the first of the labels on a line, or on the next line when the label stands alone (a table head over a value).</summary>
    private static IEnumerable<(Line Line, string After, string Label)> AfterLabels(IReadOnlyList<Line> lines, IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            var regex = LabelRegex(label);
            for (var i = 0; i < lines.Count; i++)
            {
                var m = regex.Match(lines[i].Text);
                if (!m.Success)
                {
                    continue;
                }

                var after = lines[i].Text[(m.Index + m.Length)..].Trim();
                if (after.Length == 0 && i + 1 < lines.Count)
                {
                    yield return (lines[i + 1], lines[i + 1].Text.Trim(), m.Value.Trim());
                }
                else
                {
                    yield return (lines[i], after, m.Value.Trim());
                }
            }
        }
    }

    private static string Where(Line line) => $"Text Zeile {line.Number}: „{Shorten(line.Text.Trim())}“";

    private static string Shorten(string s) => s.Length > 80 ? s[..77] + "..." : s;

    private static void ReadRawLabels(IReadOnlyList<Line> lines, List<SourceField> raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var m = LabelValueRegex().Match(line.Text);
            if (!m.Success)
            {
                continue;
            }

            var label = Regex.Replace(m.Groups[1].Value.Trim(), @"\s+", " ");
            var key = RawPrefix + label;
            if (label.Length < 2 || !seen.Add(key))
            {
                continue;
            }

            raw.Add(new SourceField(key, m.Groups[2].Value.Trim(), Confidence.Medium));
        }
    }

    // ------------------------------------------------------------------------------------------------------------ head

    private static void ReadDocumentType(IReadOnlyList<Line> lines, SemanticDocument doc)
    {
        foreach (var line in lines.Take(25))
        {
            var t = line.Text;
            if (Regex.IsMatch(t, @"\b(gutschrift|credit\s*note|avoir|nota\s+de\s+cr[ée]dito|nota\s+di\s+credito|creditnota|creditfactuur)\b", RegexOptions.IgnoreCase))
            {
                doc.Set("BT-3", "381", Confidence.Medium, Where(line));
                return;
            }

            if (Regex.IsMatch(t, @"\b(korrekturrechnung|rechnungskorrektur|corrected\s+invoice|facture\s+rectificative|factura\s+rectificativa)\b", RegexOptions.IgnoreCase))
            {
                doc.Set("BT-3", "384", Confidence.Medium, Where(line));
                return;
            }
        }

        foreach (var line in lines.Take(25))
        {
            if (Regex.IsMatch(line.Text, @"\b(rechnung|invoice|facture|factura|fattura|factuur|faktura)\b", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(line.Text, @"pro\s*-?\s*forma", RegexOptions.IgnoreCase))
            {
                doc.Set("BT-3", "380", Confidence.Medium, Where(line));
                return;
            }
        }
    }

    private static void ReadIdentifier(IReadOnlyList<Line> lines, IEnumerable<string> labels, string id, SemanticDocument doc, bool requireDigit)
    {
        foreach (var (line, after, _) in AfterLabels(lines, labels))
        {
            var m = Regex.Match(after, @"^([A-Za-z0-9][A-Za-z0-9\-/_.]{0,34}[A-Za-z0-9]|[A-Za-z0-9])");
            if (!m.Success)
            {
                continue;
            }

            var value = m.Groups[1].Value;
            if (requireDigit && !value.Any(char.IsDigit))
            {
                continue;
            }

            doc.Set(id, value, Confidence.Medium, Where(line));
            return;
        }
    }

    private static void ReadDate(IReadOnlyList<Line> lines, IEnumerable<string> labels, string id, SemanticDocument doc, bool english, List<ExtractionMessage> messages, Confidence confidence = Confidence.Medium)
    {
        foreach (var (line, after, _) in AfterLabels(lines, labels))
        {
            if (!TextValues.TryFindDate(after, english, out var date, out var ambiguous))
            {
                continue;
            }

            var level = ambiguous ? Confidence.Low : confidence;
            doc.Set(id, SemanticDocument.Format(date), level, Where(line));
            if (ambiguous)
            {
                messages.Add(new ExtractionMessage(MessageSeverity.Warning, "DATE-AMBIGUOUS", $"{SemanticCatalog.Describe(id)}: Tag und Monat sind nicht eindeutig ({Where(line)})."));
            }

            return;
        }
    }

    private static void ReadPeriod(IReadOnlyList<Line> lines, SemanticDocument doc, bool english)
    {
        foreach (var (line, after, _) in AfterLabels(lines, PeriodLabels))
        {
            var parts = Regex.Split(after, @"\s*(?:-|–|bis|to|au|al|t/m)\s+", RegexOptions.IgnoreCase);
            if (parts.Length >= 2 && TextValues.TryFindDate(parts[0], english, out var from, out _) && TextValues.TryFindDate(parts[1], english, out var to, out _) && from <= to)
            {
                doc.Set("BT-73", SemanticDocument.Format(from), Confidence.Medium, Where(line));
                doc.Set("BT-74", SemanticDocument.Format(to), Confidence.Medium, Where(line));
                return;
            }
        }
    }

    private static void ReadCurrency(string text, SemanticDocument doc)
    {
        var codes = Regex.Matches(text, @"\b(EUR|USD|GBP|CHF|SEK|NOK|DKK|PLN|CZK|HUF|RON|BGN|JPY|CNY|CAD|AUD)\b")
            .GroupBy(m => m.Value).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (codes is not null)
        {
            doc.Set("BT-5", codes.Key, Confidence.Medium, $"Text: Währungscode „{codes.Key}“ ({codes.Count()}×)");
            return;
        }

        var euro = text.Count(c => c == '€');
        var pound = text.Count(c => c == '£');
        if (euro > 0 && euro >= pound)
        {
            doc.Set("BT-5", "EUR", Confidence.Medium, $"Text: Zeichen € ({euro}×)");
        }
        else if (pound > 0)
        {
            doc.Set("BT-5", "GBP", Confidence.Medium, $"Text: Zeichen £ ({pound}×)");
        }
        else if (text.Contains('$', StringComparison.Ordinal))
        {
            // $ is the sign of many currencies.
            doc.Set("BT-5", "USD", Confidence.Low, "Text: Zeichen $ (mehrdeutig)");
        }
    }

    // ------------------------------------------------------------------------------------------------------------ parties

    private static bool HasLegalForm(string line)
        => LegalForms.Any(f => Regex.IsMatch(line, $@"(?<![\p{{L}}]){Regex.Escape(f)}(?![\p{{L}}])"));

    private static void ReadParties(IReadOnlyList<Line> lines, SemanticDocument doc)
    {
        // The buyer: the block below a label such as "Rechnungsempfänger" or "Bill to".
        var buyerLineNumbers = new HashSet<int>();
        foreach (var (line, after, _) in AfterLabels(lines, BuyerBlockLabels))
        {
            var start = IndexOf(lines, line);
            var block = new List<Line>();

            // "Kunde: Muster GmbH" carries the name on the label line; "Rechnungsempfänger" alone has the block on the lines below it.
            var labelAlone = string.Equals(line.Text.Trim(), after, StringComparison.Ordinal);
            block.Add(labelAlone ? line : new Line(line.Number, after));
            for (var i = start + 1; i < lines.Count && block.Count < 4; i++)
            {
                var t = lines[i].Text.Trim();
                if (TextValues.AmountPattern.IsMatch(t) || LineHeaderWords.Count(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)) >= 2)
                {
                    break;
                }

                block.Add(lines[i]);
            }

            if (!Regex.IsMatch(block[0].Text, @"^\s*\p{L}.*\p{L}") || Regex.IsMatch(block[0].Text, @"^\s*(no|nr|n°|number|nummer)\b", RegexOptions.IgnoreCase))
            {
                continue;
            }

            doc.Set("BT-44", block[0].Text.Trim(), Confidence.Low, Where(block[0]));
            foreach (var l in block)
            {
                buyerLineNumbers.Add(l.Number);
            }

            ReadAddress(block.Skip(1).ToList(), doc, "BT-50", "BT-52", "BT-53");
            break;
        }

        // The seller: a line with a legal form near the top or in the footer, else the first line of the page.
        var seller = lines.Take(12).FirstOrDefault(l => HasLegalForm(l.Text) && !buyerLineNumbers.Contains(l.Number) && l.Text.Trim().Length < 70)
                     ?? lines.Skip(Math.Max(0, lines.Count - 12)).FirstOrDefault(l => HasLegalForm(l.Text) && !buyerLineNumbers.Contains(l.Number) && l.Text.Trim().Length < 70);
        if (seller is not null)
        {
            var name = seller.Text.Trim();
            var cut = name.IndexOfAny(['·', '|', '•']);
            doc.Set("BT-27", cut > 3 ? name[..cut].Trim() : name, Confidence.Medium, Where(seller));
            var index = IndexOf(lines, seller);
            ReadAddress(lines.Skip(index + 1).Take(3).Where(l => !buyerLineNumbers.Contains(l.Number)).ToList(), doc, "BT-35", "BT-37", "BT-38");
        }
        else
        {
            var first = lines.FirstOrDefault(l => !Regex.IsMatch(l.Text, @"^\s*(rechnung|invoice|facture|factura|fattura|factuur|gutschrift|credit)", RegexOptions.IgnoreCase) && l.Text.Trim().Length > 2);
            if (first is not null && !buyerLineNumbers.Contains(first.Number))
            {
                doc.Set("BT-27", first.Text.Trim(), Confidence.Low, Where(first));
            }
        }
    }

    private static void ReadAddress(IReadOnlyList<Line> block, SemanticDocument doc, string street, string city, string postcode)
    {
        for (var i = 0; i < block.Count; i++)
        {
            var m = PostcodeCityRegex().Match(block[i].Text);
            if (!m.Success)
            {
                continue;
            }

            doc.Set(postcode, m.Groups[1].Value, Confidence.Low, Where(block[i]));
            doc.Set(city, m.Groups[2].Value.Trim(), Confidence.Low, Where(block[i]));
            var before = block[i].Text[..m.Index].Trim().TrimEnd(',', '·', '|').Trim();
            if (before.Length > 3 && Regex.IsMatch(before, @"\d"))
            {
                doc.Set(street, before, Confidence.Low, Where(block[i]));
            }
            else if (i > 0 && Regex.IsMatch(block[i - 1].Text, @"\d") && !TextValues.AmountPattern.IsMatch(block[i - 1].Text))
            {
                doc.Set(street, block[i - 1].Text.Trim(), Confidence.Low, Where(block[i - 1]));
            }

            return;
        }
    }

    private static void ReadVatIds(IReadOnlyList<Line> lines, SemanticDocument doc)
    {
        var found = new List<(Line Line, string Id, bool Labelled, bool Buyer)>();
        var labelRegexes = VatIdLabels.Select(LabelRegex).ToList();
        foreach (var line in lines)
        {
            foreach (Match m in VatCandidateRegex().Matches(line.Text))
            {
                var id = TextValues.CompactId(m.Value);
                if (!TextValues.LooksLikeVatId(id) || TextValues.IsValidIban(id) || found.Any(f => f.Id == id))
                {
                    continue;
                }

                var before = line.Text[..m.Index];
                var labelled = labelRegexes.Any(r => r.IsMatch(before));
                var buyer = Regex.IsMatch(before, @"(kunde|customer|client|ihre|your|votre|su\s|cliente|klant|empf[äa]nger)", RegexOptions.IgnoreCase);
                found.Add((line, id, labelled, buyer));
            }
        }

        var seller = found.FirstOrDefault(f => !f.Buyer);
        if (seller.Id is not null)
        {
            doc.Set("BT-31", seller.Id, seller.Labelled ? Confidence.Medium : Confidence.Low, Where(seller.Line));
        }

        var buyerId = found.FirstOrDefault(f => f.Buyer && f.Id != seller.Id);
        if (buyerId.Id is not null)
        {
            doc.Set("BT-48", buyerId.Id, Confidence.Low, Where(buyerId.Line));
        }
    }

    private static void ReadBank(IReadOnlyList<Line> lines, SemanticDocument doc)
    {
        foreach (var line in lines)
        {
            foreach (Match m in IbanRegex().Matches(line.Text.ToUpperInvariant()))
            {
                var iban = TextValues.CompactId(m.Value);
                if (TextValues.IsValidIban(iban))
                {
                    doc.Set("BT-84", iban, Confidence.High, Where(line) + " (Prüfziffer gültig)");
                    break;
                }
            }

            if (doc.Has("BT-84"))
            {
                break;
            }
        }

        foreach (var (line, after, _) in AfterLabels(lines, [@"bic", @"swift(?:\s*-?\s*code)?", @"bic\s*/\s*swift"]))
        {
            var m = BicRegex().Match(after.ToUpperInvariant());
            if (m.Success)
            {
                doc.Set("BT-86", m.Groups[1].Value, Confidence.Medium, Where(line));
                break;
            }
        }

        // The payment means code (BT-81) is not written on a paper invoice. It is not guessed from the IBAN: a person sets it as a correction when needed.
    }

    private static void ReadContact(IReadOnlyList<Line> lines, SemanticDocument doc)
    {
        var email = lines.Select(l => (Line: l, Match: EmailRegex().Match(l.Text))).FirstOrDefault(x => x.Match.Success);
        if (email.Line is not null)
        {
            doc.Set("BT-43", email.Match.Value, Confidence.Low, Where(email.Line));
        }

        var phone = lines.Select(l => (Line: l, Match: PhoneRegex().Match(l.Text))).FirstOrDefault(x => x.Match.Success);
        if (phone.Line is not null)
        {
            doc.Set("BT-42", phone.Match.Groups[1].Value.Trim(), Confidence.Low, Where(phone.Line));
        }
    }

    private static void ReadPaymentTerms(IReadOnlyList<Line> lines, SemanticDocument doc)
    {
        var regexes = PaymentTermsLabels.Select(l => new Regex($@"(?<![\p{{L}}])(?:{l})", RegexOptions.IgnoreCase)).ToList();
        var line = lines.FirstOrDefault(l => regexes.Any(r => r.IsMatch(l.Text)) && l.Text.Trim().Length > 10);
        if (line is not null)
        {
            doc.Set("BT-20", line.Text.Trim(), Confidence.Low, Where(line));
        }
    }

    // ------------------------------------------------------------------------------------------------------------ amounts

    private static decimal? LastAmount(string text)
    {
        var matches = TextValues.AmountPattern.Matches(text);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            if (TextValues.TryParseAmount(matches[i].Value, out var v))
            {
                return v;
            }
        }

        return null;
    }

    private static void ReadTotals(IReadOnlyList<Line> lines, SemanticDocument doc)
    {
        // The totals are at the end: labels are looked for from the bottom up, so that "Total" in a table head does not win.
        var bottomUp = lines.Reverse().ToList();
        foreach (var label in GrossLabels)
        {
            var regex = LabelRegex(label);
            var line = bottomUp.FirstOrDefault(l => regex.IsMatch(l.Text) && !NetLabels.Any(n => Regex.IsMatch(l.Text, $@"(?<![\p{{L}}]){n}", RegexOptions.IgnoreCase)) && LastAmount(l.Text) is not null);
            if (line is null)
            {
                continue;
            }

            var amount = SemanticDocument.Format(LastAmount(line.Text)!.Value);
            var confidence = label is @"total" or @"brutto" ? Confidence.Low : Confidence.Medium;
            doc.Set("BT-112", amount, confidence, Where(line));
            doc.Set("BT-115", amount, confidence, Where(line));
            break;
        }

        foreach (var label in NetLabels)
        {
            var regex = LabelRegex(label);
            var line = bottomUp.FirstOrDefault(l => regex.IsMatch(l.Text) && LastAmount(l.Text) is not null);
            if (line is null)
            {
                continue;
            }

            var amount = SemanticDocument.Format(LastAmount(line.Text)!.Value);
            doc.Set("BT-109", amount, Confidence.Medium, Where(line));
            break;
        }

        var taxRegexes = TaxLabels.Select(l => new Regex($@"(?<![\p{{L}}])(?:{l})(?![\p{{L}}])", RegexOptions.IgnoreCase)).ToList();
        var taxLines = bottomUp.Where(l => taxRegexes.Any(r => r.IsMatch(l.Text)) && LastAmount(l.Text) is not null
                                           && !VatIdLabels.Take(3).Any(v => Regex.IsMatch(l.Text, v, RegexOptions.IgnoreCase))).ToList();
        if (taxLines.Count == 1)
        {
            doc.Set("BT-110", SemanticDocument.Format(LastAmount(taxLines[0].Text)!.Value), Confidence.Medium, Where(taxLines[0]));
        }
        else if (taxLines.Count > 1)
        {
            // Several tax lines (one per rate): their sum is what the text says in total, read line by line.
            var sum = taxLines.Sum(l => LastAmount(l.Text)!.Value);
            doc.Set("BT-110", SemanticDocument.Format(sum), Confidence.Low, $"Summe aus {taxLines.Count} Steuerzeilen ({string.Join(", ", taxLines.Select(l => "Zeile " + l.Number))})");
        }
    }

    private static void ReadVatBreakdown(IReadOnlyList<Line> lines, SemanticDocument doc)
    {
        var taxRegexes = TaxLabels.Select(l => new Regex($@"(?<![\p{{L}}])(?:{l})(?![\p{{L}}])", RegexOptions.IgnoreCase)).ToList();
        var items = new List<SemanticItem>();
        foreach (var line in lines)
        {
            if (!taxRegexes.Any(r => r.IsMatch(line.Text)))
            {
                continue;
            }

            var percent = PercentRegex().Match(line.Text);
            var amounts = TextValues.AmountPattern.Matches(line.Text).Select(m => TextValues.TryParseAmount(m.Value, out var v) ? v : (decimal?)null).Where(v => v is not null).Select(v => v!.Value).ToList();
            if (!percent.Success || amounts.Count == 0 || !TextValues.TryParseNumber(percent.Groups[1].Value, out var rate))
            {
                continue;
            }

            var item = new SemanticItem();
            item.Set("BT-119", SemanticDocument.Format(rate), Confidence.Low, Where(line));
            item.Set("BT-117", SemanticDocument.Format(amounts[^1]), Confidence.Low, Where(line));
            if (amounts.Count >= 2)
            {
                item.Set("BT-116", SemanticDocument.Format(amounts[0]), Confidence.Low, Where(line));
            }

            // The category (BT-118) is not on the paper; it is left to a correction rather than guessed from the rate.
            items.Add(item);
        }

        if (items.Count > 0)
        {
            doc.Groups[SemanticCatalog.VatBreakdown] = items;
        }
    }

    // ------------------------------------------------------------------------------------------------------------ lines

    private static void ReadLines(IReadOnlyList<Line> lines, SemanticDocument doc, List<ExtractionMessage> messages)
    {
        var headerIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var words = Regex.Split(lines[i].Text.ToLowerInvariant(), @"[^\p{L}\-.]+").Where(w => w.Length > 1).ToHashSet();
            if (LineHeaderWords.Count(w => words.Contains(w)) >= 2)
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0)
        {
            messages.Add(new ExtractionMessage(MessageSeverity.Warning, "TEXT-NO-LINES", "Im Text wurde keine Positionstabelle erkannt; die Positionen fehlen."));
            return;
        }

        var stop = new Regex($@"(?<![\p{{L}}])(?:{string.Join("|", NetLabels.Concat(GrossLabels.Where(g => g != "total")).Concat(TaxLabels.Take(1)))})(?![\p{{L}}])", RegexOptions.IgnoreCase);
        var items = new List<SemanticItem>();
        for (var i = headerIndex + 1; i < lines.Count; i++)
        {
            var text = lines[i].Text;
            if (stop.IsMatch(text))
            {
                break;
            }

            var amounts = TextValues.AmountPattern.Matches(text).ToList();
            if (amounts.Count == 0)
            {
                continue;
            }

            var descriptionEnd = amounts[0].Index;
            var head = text[..descriptionEnd];

            // The quantity is the last plain number before the prices ("2 Stk", "2,5 h"); a leading position number is skipped.
            var leading = Regex.Match(head, @"^\s*(\d{1,3})[.)]?\s+");
            var body = leading.Success ? head[leading.Length..] : head;
            var qty = Regex.Match(body, @"(?<![\p{L}\d.,])(\d+(?:[.,]\d{1,3})?)\s*(stk\.?|st\.?|stück|pcs?\.?|pieces?|h|std\.?|stunden?|hours?|tage?|days?|kg|m|psch\.?|pauschal|x)?\s*$", RegexOptions.IgnoreCase);
            var description = (qty.Success ? body[..qty.Index] : body).Trim(' ', '-', '|', '\t');
            if (description.Length < 2 || !Regex.IsMatch(description, @"\p{L}{2}"))
            {
                continue;
            }

            var item = new SemanticItem();
            var where = Where(lines[i]);
            item.Set("BT-126", (items.Count + 1).ToString(CultureInfo.InvariantCulture), Confidence.Low, "Abgeleitet: Reihenfolge im Text");
            item.Set("BT-153", description, Confidence.Low, where);
            if (qty.Success && TextValues.TryParseNumber(qty.Groups[1].Value, out var q))
            {
                item.Set("BT-129", SemanticDocument.Format(q), Confidence.Low, where);
                var unit = UnitCode(qty.Groups[2].Value);
                if (unit is not null)
                {
                    item.Set("BT-130", unit, Confidence.Low, where);
                }
            }

            if (TextValues.TryParseAmount(amounts[^1].Value, out var net))
            {
                item.Set("BT-131", SemanticDocument.Format(net), Confidence.Low, where);
            }

            if (amounts.Count >= 2 && TextValues.TryParseAmount(amounts[^2].Value, out var price))
            {
                item.Set("BT-146", SemanticDocument.Format(price), Confidence.Low, where);
            }

            var percent = PercentRegex().Match(text);
            if (percent.Success && TextValues.TryParseNumber(percent.Groups[1].Value, out var rate))
            {
                item.Set("BT-152", SemanticDocument.Format(rate), Confidence.Low, where);
            }

            items.Add(item);
        }

        if (items.Count > 0)
        {
            doc.Groups[SemanticCatalog.Lines] = items;
            messages.Add(new ExtractionMessage(MessageSeverity.Warning, "TEXT-LINES", $"{items.Count} Positionen wurden aus der Tabelle im Text gelesen. Bitte Mengen, Preise und Beträge prüfen."));
        }
        else
        {
            messages.Add(new ExtractionMessage(MessageSeverity.Warning, "TEXT-NO-LINES", "Unter dem Kopf der Positionstabelle wurden keine Positionen erkannt."));
        }
    }

    /// <summary>The code of UN/ECE Recommendation 20 for a unit written in words; null when the word is not clear.</summary>
    public static string? UnitCode(string? unit) => (unit ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant() switch
    {
        "stk" or "st" or "stück" or "pc" or "pcs" or "piece" or "pieces" or "x" => "H87",
        "h" or "std" or "stunde" or "stunden" or "hour" or "hours" => "HUR",
        "tag" or "tage" or "day" or "days" => "DAY",
        "kg" => "KGM",
        "m" => "MTR",
        "psch" or "pauschal" => "LS",
        _ => null,
    };

    // ------------------------------------------------------------------------------------------------------------ helpers

    private static int IndexOf(IReadOnlyList<Line> lines, Line line)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Number == line.Number)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsMostlyEnglish(string text)
    {
        var lower = text.ToLowerInvariant();
        var en = Regex.Matches(lower, @"\b(invoice|amount|total|due|date|price|quantity|bill to|payment)\b").Count;
        var other = Regex.Matches(lower, @"\b(rechnung|betrag|datum|menge|facture|montant|factura|importe|fattura|importo|factuur|bedrag)\b").Count;
        return en > other && lower.Contains('$', StringComparison.Ordinal);
    }

    private static void Lower(SemanticDocument doc)
    {
        foreach (var key in doc.Fields.Keys.ToList())
        {
            doc.Fields[key] = doc.Fields[key] with { Confidence = Confidence.Low };
        }

        foreach (var item in doc.Groups.Values.SelectMany(g => g))
        {
            foreach (var key in item.Fields.Keys.ToList())
            {
                item.Fields[key] = item.Fields[key] with { Confidence = Confidence.Low };
            }
        }
    }
}
