using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceMesh.Core.Extraction;
using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Semantics;

namespace InvoiceMesh.Core.Validation;

/// <summary>What the validation of the semantic model is to check.</summary>
/// <param name="Rules">The rule sets of the invoice's specification (EN 16931, XRechnung, Peppol).</param>
/// <param name="Structured">
/// True for UBL and CII: the full rules apply. False for the text of a PDF or a scan: only the rules about the content of an invoice apply (number, date,
/// parties, sums, taxes); a missing code that is never printed on paper (a unit code, a country code) is no error there.
/// </param>
/// <param name="Tolerance">How far a computed sum may differ from the stated one (rounding).</param>
public sealed record ValidationOptions(RuleSets Rules, bool Structured, DateOnly Today, decimal Tolerance = 0.01m);

/// <summary>
/// The business rules of EN 16931 (the BR-, BR-CO-, BR-CL- and BR-DEC- rules InvoiceMesh implements), of the German CIUS XRechnung (BR-DE-) and of
/// Peppol BIS Billing 3.0 (PEPPOL-EN16931-), plus checks of plausibility, all on the semantic model. They do not replace the official validators (the
/// KoSIT validator for XRechnung): the report says so, and lists exactly which rules ran.
/// </summary>
public static partial class SemanticValidator
{
    [GeneratedRegex(@"^urn:fdc:peppol\.eu:2017:poacc:billing:\d{2}:1\.0$")]
    private static partial Regex PeppolProcessRegex();

    public static (List<Finding> Findings, List<CheckRun> Checks) Validate(SemanticDocument doc, ValidationOptions options)
    {
        var findings = new List<Finding>();
        var checks = new List<CheckRun>();
        var full = options.Structured && (options.Rules.HasFlag(RuleSets.En16931) || options.Rules == RuleSets.None && doc.Get("BT-24") is null);

        var core = new Checker(doc, options, full ? CheckScope.En16931 : CheckScope.Content, full, findings);
        core.Run();
        checks.Add(new CheckRun(core.Scope, full ? "Geschäftsregeln EN 16931 (Pflichtangaben, Summen, Steuern, Codelisten)" : "Inhalt der Rechnung (Regeln nach EN 16931, soweit eine Papierrechnung sie erfüllen kann)", core.Count, true));

        if (options.Rules.HasFlag(RuleSets.XRechnung))
        {
            var x = new Checker(doc, options, CheckScope.XRechnung, true, findings);
            x.RunXRechnung();
            checks.Add(new CheckRun(CheckScope.XRechnung, "XRechnung 3.0 (BR-DE-Regeln der CIUS)", x.Count, true));
        }
        else
        {
            checks.Add(new CheckRun(CheckScope.XRechnung, "XRechnung 3.0 (BR-DE-Regeln der CIUS)", 0, false, "Die Rechnung ist keine XRechnung."));
        }

        if (options.Rules.HasFlag(RuleSets.Peppol))
        {
            var p = new Checker(doc, options, CheckScope.Peppol, true, findings);
            p.RunPeppol();
            checks.Add(new CheckRun(CheckScope.Peppol, "Peppol BIS Billing 3.0", p.Count, true));
        }
        else
        {
            checks.Add(new CheckRun(CheckScope.Peppol, "Peppol BIS Billing 3.0", 0, false, "Die Rechnung ist keine Peppol-Rechnung."));
        }

        var plausibility = new Checker(doc, options, CheckScope.Plausibility, full, findings);
        plausibility.RunPlausibility();
        checks.Add(new CheckRun(CheckScope.Plausibility, "Plausibilität (IBAN-Prüfziffer, Daten, Positionsbeträge, unsicher erkannte Werte)", plausibility.Count, true));
        return (findings, checks);
    }

    /// <summary>
    /// Whether an XRechnung can be made from the model: the rules of EN 16931 and of XRechnung in full, as for a structured invoice. Returns what is
    /// missing or wrong; empty means ready.
    /// </summary>
    public static List<Finding> XRechnungReadiness(SemanticDocument doc, DateOnly today, decimal tolerance = 0.01m)
    {
        var findings = new List<Finding>();
        var options = new ValidationOptions(RuleSets.En16931 | RuleSets.XRechnung, true, today, tolerance);
        var core = new Checker(doc, options, CheckScope.En16931, true, findings) { IgnoreSpecification = true };
        core.Run();
        var x = new Checker(doc, options, CheckScope.XRechnung, true, findings) { IgnoreSpecification = true };
        x.RunXRechnung();
        return [.. findings.Where(f => f.Severity == Severity.Error)];
    }

    private sealed class Checker(SemanticDocument d, ValidationOptions o, CheckScope scope, bool full, List<Finding> findings)
    {
        public CheckScope Scope => scope;

        public int Count { get; private set; }

        /// <summary>The specification identifier is written by the generator itself: its rules are not asked of the source.</summary>
        public bool IgnoreSpecification { get; init; }

        private IReadOnlyList<SemanticItem> Lines => d.Items(SemanticCatalog.Lines);

        private IReadOnlyList<SemanticItem> Vat => d.Items(SemanticCatalog.VatBreakdown);

        private IReadOnlyList<SemanticItem> Allowances => d.Items(SemanticCatalog.Allowances);

        private IReadOnlyList<SemanticItem> Charges => d.Items(SemanticCatalog.Charges);

        private void Add(string id, Severity severity, string message, string? location = null, string? value = null)
            => findings.Add(new Finding(id, severity, scope, message, location, value));

        /// <summary>A rule about a value that must be there. <paramref name="paper"/> is its weight for a paper invoice (null: not checked there).</summary>
        private void Require(string id, string bt, string message, Severity? paper = Severity.Error)
        {
            var severity = full ? Severity.Error : paper;
            if (severity is null)
            {
                return;
            }

            Count++;
            if (!d.Has(bt))
            {
                Add(id, severity.Value, message, bt);
            }
        }

        private bool Close(decimal a, decimal b) => Math.Abs(a - b) <= o.Tolerance;

        private static string Money(decimal v) => v.ToString("0.00##", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------------------------------------ EN 16931

        public void Run()
        {
            if (!IgnoreSpecification)
            {
                Require("BR-01", "BT-24", "Die Rechnung muss eine Spezifikationskennung (BT-24) enthalten.", paper: null);
            }

            Require("BR-02", "BT-1", "Die Rechnung muss eine Rechnungsnummer (BT-1) enthalten.");
            Require("BR-03", "BT-2", "Die Rechnung muss ein Rechnungsdatum (BT-2) enthalten.");
            Require("BR-04", "BT-3", "Die Rechnung muss einen Code für die Rechnungsart (BT-3) enthalten.", paper: Severity.Info);
            Require("BR-05", "BT-5", "Die Rechnung muss einen Währungscode (BT-5) enthalten.", paper: Severity.Warning);
            Require("BR-06", "BT-27", "Die Rechnung muss den Namen des Verkäufers (BT-27) enthalten.");
            Require("BR-07", "BT-44", "Die Rechnung muss den Namen des Käufers (BT-44) enthalten.", paper: Severity.Warning);
            Require("BR-09", "BT-40", "Die Anschrift des Verkäufers muss einen Ländercode (BT-40) enthalten.", paper: null);
            Require("BR-11", "BT-55", "Die Anschrift des Käufers muss einen Ländercode (BT-55) enthalten.", paper: null);
            Require("BR-12", "BT-106", "Die Rechnung muss die Summe der Nettobeträge der Positionen (BT-106) enthalten.", paper: null);
            Require("BR-13", "BT-109", "Die Rechnung muss den Gesamtbetrag ohne Umsatzsteuer (BT-109) enthalten.", paper: Severity.Warning);
            Require("BR-14", "BT-112", "Die Rechnung muss den Gesamtbetrag mit Umsatzsteuer (BT-112) enthalten.");
            Require("BR-15", "BT-115", "Die Rechnung muss den fälligen Zahlbetrag (BT-115) enthalten.", paper: null);

            if (full)
            {
                Count += 2;
                foreach (var (id, bt) in new[] { ("BR-62", "BT-34"), ("BR-63", "BT-49") })
                {
                    if (d.Get(bt) is { } address && d.Get(bt + "-scheme") is null && !address.Contains('@', StringComparison.Ordinal))
                    {
                        Add(id, Severity.Error, $"{SemanticCatalog.Describe(bt)}: Das Schema der elektronischen Adresse (z. B. EM für E-Mail, 0204 für die Leitweg-ID) fehlt.", bt + "-scheme", address);
                    }
                }
            }

            Count++;
            if (Lines.Count == 0)
            {
                Add("BR-16", full ? Severity.Error : Severity.Warning, "Die Rechnung muss mindestens eine Position (BG-25) enthalten.", SemanticCatalog.Lines);
            }

            CheckLines();
            CheckVatBreakdown();
            CheckTotals();
            CheckCodes();
            CheckDecimals();
        }

        private void CheckLines()
        {
            Count += full ? 8 : 3;
            for (var i = 0; i < Lines.Count; i++)
            {
                var line = Lines[i];
                string Key(string bt) => SemanticDocument.ItemKey(SemanticCatalog.Lines, i, bt);
                if (full && line.Get("BT-126") is null)
                {
                    Add("BR-21", Severity.Error, $"Position {i + 1}: Positionsnummer (BT-126) fehlt.", Key("BT-126"));
                }

                if (full && line.Get("BT-129") is null)
                {
                    Add("BR-22", Severity.Error, $"Position {i + 1}: Menge (BT-129) fehlt.", Key("BT-129"));
                }

                if (full && line.Get("BT-130") is null)
                {
                    Add("BR-23", Severity.Error, $"Position {i + 1}: Mengeneinheit (BT-130) fehlt.", Key("BT-130"));
                }

                if (line.Get("BT-131") is null)
                {
                    Add("BR-24", full ? Severity.Error : Severity.Warning, $"Position {i + 1}: Nettobetrag (BT-131) fehlt.", Key("BT-131"));
                }

                if (line.Get("BT-153") is null)
                {
                    Add("BR-25", full ? Severity.Error : Severity.Warning, $"Position {i + 1}: Artikelname (BT-153) fehlt.", Key("BT-153"));
                }

                if (full && line.Get("BT-146") is null)
                {
                    Add("BR-26", Severity.Error, $"Position {i + 1}: Nettopreis (BT-146) fehlt.", Key("BT-146"));
                }

                if (line.Decimal("BT-146") is < 0)
                {
                    Add("BR-27", Severity.Error, $"Position {i + 1}: Der Nettopreis (BT-146) darf nicht negativ sein.", Key("BT-146"), line.Get("BT-146"));
                }

                if (line.Decimal("BT-148") is < 0)
                {
                    Add("BR-28", Severity.Error, $"Position {i + 1}: Der Bruttopreis (BT-148) darf nicht negativ sein.", Key("BT-148"), line.Get("BT-148"));
                }

                if (full && line.Get("BT-157") is not null && line.Get("BT-157-scheme") is null)
                {
                    Add("BR-64", Severity.Error, $"Position {i + 1}: Zur Artikelkennung (BT-157) fehlt das Schema (z. B. 0160 für GTIN).", Key("BT-157-scheme"));
                }

                if (full && line.Get("BT-151") is null)
                {
                    Add("BR-CO-04", Severity.Error, $"Position {i + 1}: Die Umsatzsteuerkategorie (BT-151) fehlt.", Key("BT-151"));
                }

                CheckCategoryRate(line.Get("BT-151"), line.Decimal("BT-152"), $"Position {i + 1}", Key("BT-152"));
            }
        }

        private void CheckCategoryRate(string? category, decimal? rate, string where, string location)
        {
            if (category is null)
            {
                return;
            }

            var c = category.ToUpperInvariant();
            if (c == "S" && rate is not > 0)
            {
                Add("BR-S-05", Severity.Error, $"{where}: Bei Kategorie S (Normalsatz) muss der Steuersatz größer als 0 sein.", location, rate?.ToString(CultureInfo.InvariantCulture));
            }
            else if (c is "Z" or "E" or "AE" or "K" or "G" && rate is not null && rate != 0)
            {
                Add($"BR-{c}-05", Severity.Error, $"{where}: Bei Kategorie {c} muss der Steuersatz 0 sein.", location, rate.Value.ToString(CultureInfo.InvariantCulture));
            }
            else if (c == "O" && rate is not null)
            {
                Add("BR-O-05", Severity.Error, $"{where}: Bei Kategorie O (nicht steuerbar) darf kein Steuersatz angegeben sein.", location, rate.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void CheckVatBreakdown()
        {
            Count += 7;
            if (Vat.Count == 0)
            {
                Add("BR-CO-18", full ? Severity.Error : Severity.Warning, "Die Rechnung muss mindestens eine Umsatzsteueraufschlüsselung (BG-23) enthalten.", SemanticCatalog.VatBreakdown);
                return;
            }

            for (var i = 0; i < Vat.Count; i++)
            {
                var row = Vat[i];
                string Key(string bt) => SemanticDocument.ItemKey(SemanticCatalog.VatBreakdown, i, bt);
                var where = $"Steueraufschlüsselung {i + 1}";
                var category = row.Get("BT-118")?.ToUpperInvariant();
                if (row.Decimal("BT-116") is null)
                {
                    Add("BR-45", full ? Severity.Error : Severity.Warning, $"{where}: Die Bemessungsgrundlage (BT-116) fehlt.", Key("BT-116"));
                }

                if (row.Decimal("BT-117") is null)
                {
                    Add("BR-46", full ? Severity.Error : Severity.Warning, $"{where}: Der Steuerbetrag (BT-117) fehlt.", Key("BT-117"));
                }

                if (category is null && full)
                {
                    Add("BR-47", Severity.Error, $"{where}: Die Steuerkategorie (BT-118) fehlt.", Key("BT-118"));
                }

                if (category != "O" && row.Decimal("BT-119") is null)
                {
                    Add("BR-48", full ? Severity.Error : Severity.Warning, $"{where}: Der Steuersatz (BT-119) fehlt.", Key("BT-119"));
                }

                if (row.Decimal("BT-116") is { } basis && row.Decimal("BT-119") is { } rate && row.Decimal("BT-117") is { } tax)
                {
                    var expected = Math.Round(basis * rate / 100m, 2, MidpointRounding.AwayFromZero);
                    if (!Close(expected, tax))
                    {
                        Add("BR-CO-17", Severity.Error, $"{where}: Steuerbetrag {Money(tax)} ≠ Bemessungsgrundlage × Satz = {Money(expected)}.", Key("BT-117"), Money(tax));
                    }
                }

                if (category is "E" or "AE" or "K" or "G" or "O" && row.Get("BT-120") is null && row.Get("BT-121") is null)
                {
                    Add($"BR-{category}-10", full ? Severity.Error : Severity.Warning, $"{where}: Bei Kategorie {category} muss ein Befreiungsgrund (BT-120 oder BT-121) angegeben sein.", Key("BT-120"));
                }

                CheckCategoryRate(category, row.Decimal("BT-119"), where, Key("BT-119"));

                // BR-x-08: the basis of a category and rate is the sum of the lines, minus allowances, plus charges of that category and rate.
                if (category is not null && Lines.Count > 0 && row.Decimal("BT-116") is { } stated && Lines.All(l => l.Get("BT-151") is not null))
                {
                    var rate119 = row.Decimal("BT-119") ?? 0;
                    bool Same(SemanticItem item, string cat, string rateId) => string.Equals(item.Get(cat), category, StringComparison.OrdinalIgnoreCase) && (item.Decimal(rateId) ?? 0) == rate119;
                    var sum = Lines.Where(l => Same(l, "BT-151", "BT-152")).Sum(l => l.Decimal("BT-131") ?? 0)
                              - Allowances.Where(a => Same(a, "BT-95", "BT-96")).Sum(a => a.Decimal("BT-92") ?? 0)
                              + Charges.Where(c => Same(c, "BT-102", "BT-103")).Sum(c => c.Decimal("BT-99") ?? 0);
                    if (!Close(sum, stated))
                    {
                        Add($"BR-{category}-08", Severity.Error, $"{where}: Bemessungsgrundlage {Money(stated)} ≠ Summe der Positionen, Nachlässe und Zuschläge dieser Kategorie und dieses Satzes = {Money(sum)}.", Key("BT-116"), Money(stated));
                    }
                }
            }

            var categories = Lines.Select(l => l.Get("BT-151")?.ToUpperInvariant()).Where(c => c is not null).ToHashSet();
            Count += 2;
            if (categories.Contains("AE") || categories.Contains("K"))
            {
                var code = categories.Contains("AE") ? "AE" : "K";
                if (d.Get("BT-31") is null && d.Get("BT-32") is null)
                {
                    Add($"BR-{code}-02", Severity.Error, $"Bei Kategorie {code} muss die USt-IdNr. des Verkäufers (BT-31) angegeben sein.", "BT-31");
                }

                if (d.Get("BT-48") is null && (code == "K" || d.Get("BT-47") is null))
                {
                    Add($"BR-{code}-02", Severity.Error, $"Bei Kategorie {code} muss die USt-IdNr. des Käufers (BT-48) angegeben sein.", "BT-48");
                }
            }
        }

        private void CheckTotals()
        {
            Count += 7;
            var b106 = d.Decimal("BT-106");
            var b107 = d.Decimal("BT-107") ?? 0;
            var b108 = d.Decimal("BT-108") ?? 0;
            var b109 = d.Decimal("BT-109");
            var b110 = d.Decimal("BT-110");
            var b112 = d.Decimal("BT-112");
            var b113 = d.Decimal("BT-113") ?? 0;
            var b114 = d.Decimal("BT-114") ?? 0;
            var b115 = d.Decimal("BT-115");

            if (b106 is { } sumLines && Lines.Count > 0 && Lines.All(l => l.Decimal("BT-131") is not null))
            {
                var computed = Lines.Sum(l => l.Decimal("BT-131")!.Value);
                if (!Close(computed, sumLines))
                {
                    Add("BR-CO-10", full ? Severity.Error : Severity.Warning, $"Summe der Positionen (BT-106) {Money(sumLines)} ≠ Summe der Nettobeträge der Positionen {Money(computed)}.", "BT-106", Money(sumLines));
                }
            }
            else if (!full && b109 is { } net && Lines.Count > 0 && Lines.All(l => l.Decimal("BT-131") is not null) && Allowances.Count == 0 && Charges.Count == 0)
            {
                // A paper invoice rarely states BT-106: the lines are compared with the net total instead, as a warning.
                var computed = Lines.Sum(l => l.Decimal("BT-131")!.Value);
                if (!Close(computed, net))
                {
                    Add("BR-CO-10", Severity.Warning, $"Die erkannten Positionen ergeben {Money(computed)}, der Nettobetrag der Rechnung ist {Money(net)}. Wahrscheinlich wurden Positionen nicht oder falsch erkannt.", SemanticCatalog.Lines);
                }
            }

            if (d.Decimal("BT-107") is { } allowances && Allowances.Count > 0 && !Close(allowances, Allowances.Sum(a => a.Decimal("BT-92") ?? 0)))
            {
                Add("BR-CO-11", Severity.Error, $"Summe der Nachlässe (BT-107) {Money(allowances)} ≠ Summe der Nachlässe auf Dokumentebene.", "BT-107");
            }

            if (d.Decimal("BT-108") is { } charges && Charges.Count > 0 && !Close(charges, Charges.Sum(c => c.Decimal("BT-99") ?? 0)))
            {
                Add("BR-CO-12", Severity.Error, $"Summe der Zuschläge (BT-108) {Money(charges)} ≠ Summe der Zuschläge auf Dokumentebene.", "BT-108");
            }

            if (b106 is { } l106 && b109 is { } l109 && !Close(l106 - b107 + b108, l109))
            {
                Add("BR-CO-13", Severity.Error, $"Gesamtbetrag ohne USt (BT-109) {Money(l109)} ≠ BT-106 − BT-107 + BT-108 = {Money(l106 - b107 + b108)}.", "BT-109", Money(l109));
            }

            if (b110 is { } tax && Vat.Count > 0 && Vat.All(v => v.Decimal("BT-117") is not null))
            {
                var sum = Vat.Sum(v => v.Decimal("BT-117")!.Value);
                if (!Close(sum, tax))
                {
                    Add("BR-CO-14", full ? Severity.Error : Severity.Warning, $"Summe Umsatzsteuer (BT-110) {Money(tax)} ≠ Summe der Steuerbeträge der Aufschlüsselung {Money(sum)}.", "BT-110", Money(tax));
                }
            }

            if (b109 is { } n109 && b112 is { } g112 && !Close(n109 + (b110 ?? 0), g112))
            {
                Add("BR-CO-15", Severity.Error, $"Gesamtbetrag mit USt (BT-112) {Money(g112)} ≠ BT-109 + BT-110 = {Money(n109 + (b110 ?? 0))}.", "BT-112", Money(g112));
            }

            if (b112 is { } t112 && b115 is { } due && !Close(t112 - b113 + b114, due))
            {
                Add("BR-CO-16", Severity.Error, $"Zahlbetrag (BT-115) {Money(due)} ≠ BT-112 − BT-113 + BT-114 = {Money(t112 - b113 + b114)}.", "BT-115", Money(due));
            }

            if (full && b115 is > 0 && d.Get("BT-9") is null && d.Get("BT-20") is null)
            {
                Add("BR-CO-25", Severity.Error, "Ist ein Betrag zu zahlen, muss ein Fälligkeitsdatum (BT-9) oder eine Zahlungsbedingung (BT-20) angegeben sein.", "BT-9");
            }
        }

        private void CheckCodes()
        {
            Count += 7;
            if (d.Get("BT-3") is { } type && !CodeLists.InvoiceTypes.Contains(type))
            {
                Add("BR-CL-01", full ? Severity.Error : Severity.Warning, $"Der Code der Rechnungsart (BT-3) „{type}“ ist nicht zulässig (UNCL 1001).", "BT-3", type);
            }

            foreach (var id in new[] { "BT-5", "BT-6" })
            {
                if (d.Get(id) is { } currency && !CodeLists.Currencies.Contains(currency))
                {
                    Add("BR-CL-03", Severity.Error, $"„{currency}“ ist kein Währungscode nach ISO 4217 ({SemanticCatalog.Describe(id)}).", id, currency);
                }
            }

            foreach (var id in new[] { "BT-40", "BT-55" })
            {
                if (d.Get(id) is { } country && !CodeLists.Countries.Contains(country))
                {
                    Add("BR-CL-14", Severity.Error, $"„{country}“ ist kein Ländercode nach ISO 3166-1 ({SemanticCatalog.Describe(id)}).", id, country);
                }
            }

            if (d.Get("BT-81") is { } means && !CodeLists.PaymentMeans.Contains(means))
            {
                Add("BR-CL-16", Severity.Error, $"„{means}“ ist kein Code für die Zahlungsart (UNCL 4461).", "BT-81", means);
            }

            foreach (var id in new[] { "BT-31", "BT-48" })
            {
                if (d.Get(id) is { } vat && !CodeLists.HasCountryPrefix(vat))
                {
                    Add("BR-CO-09", full ? Severity.Error : Severity.Warning, $"Die USt-IdNr. „{vat}“ ({SemanticCatalog.Describe(id)}) muss mit dem Ländercode beginnen.", id, vat);
                }
            }

            var categoryFields = Lines.Select((l, i) => (l.Get("BT-151"), SemanticDocument.ItemKey(SemanticCatalog.Lines, i, "BT-151")))
                .Concat(Vat.Select((v, i) => (v.Get("BT-118"), SemanticDocument.ItemKey(SemanticCatalog.VatBreakdown, i, "BT-118"))));
            foreach (var (category, key) in categoryFields)
            {
                if (category is not null && !CodeLists.VatCategories.Contains(category))
                {
                    Add("BR-CL-17", Severity.Error, $"„{category}“ ist keine Umsatzsteuerkategorie (UNCL 5305).", key, category);
                }
            }

            for (var i = 0; i < Lines.Count; i++)
            {
                if (Lines[i].Get("BT-130") is { } unit && !CodeLists.CommonUnits.Contains(unit))
                {
                    Add("BR-CL-23", Severity.Info, $"Position {i + 1}: Die Einheit „{unit}“ ist kein gängiger Code der UN/ECE-Empfehlung 20/21; bitte prüfen.", SemanticDocument.ItemKey(SemanticCatalog.Lines, i, "BT-130"), unit);
                }
            }
        }

        private void CheckDecimals()
        {
            if (!full)
            {
                return;
            }

            Count++;
            foreach (var id in new[] { "BT-106", "BT-107", "BT-108", "BT-109", "BT-110", "BT-112", "BT-113", "BT-114", "BT-115" })
            {
                if (d.Decimal(id) is { } v && decimal.Round(v, 2) != v)
                {
                    Add("BR-DEC", Severity.Error, $"{SemanticCatalog.Describe(id)} darf höchstens zwei Nachkommastellen haben.", id, d.Get(id));
                }
            }

            for (var i = 0; i < Lines.Count; i++)
            {
                if (Lines[i].Decimal("BT-131") is { } v && decimal.Round(v, 2) != v)
                {
                    Add("BR-DEC-23", Severity.Error, $"Position {i + 1}: Der Nettobetrag (BT-131) darf höchstens zwei Nachkommastellen haben.", SemanticDocument.ItemKey(SemanticCatalog.Lines, i, "BT-131"), Lines[i].Get("BT-131"));
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ XRechnung

        public void RunXRechnung()
        {
            void Need(string id, string bt, string text)
            {
                Count++;
                if (!d.Has(bt))
                {
                    Add(id, Severity.Error, text, bt);
                }
            }

            Need("BR-DE-1", "BT-81", "Eine XRechnung muss Zahlungsanweisungen (BG-16) mit dem Code der Zahlungsart (BT-81) enthalten.");
            Need("BR-DE-2", "BT-41", "Eine XRechnung muss den Kontakt des Verkäufers (BG-6) enthalten.");
            Need("BR-DE-3", "BT-37", "Eine XRechnung muss den Ort des Verkäufers (BT-37) enthalten.");
            Need("BR-DE-4", "BT-38", "Eine XRechnung muss die Postleitzahl des Verkäufers (BT-38) enthalten.");
            Need("BR-DE-5", "BT-41", "Eine XRechnung muss die Kontaktstelle des Verkäufers (BT-41) enthalten.");
            Need("BR-DE-6", "BT-42", "Eine XRechnung muss die Telefonnummer des Verkäuferkontakts (BT-42) enthalten.");
            Need("BR-DE-7", "BT-43", "Eine XRechnung muss die E-Mail-Adresse des Verkäuferkontakts (BT-43) enthalten.");
            Need("BR-DE-8", "BT-52", "Eine XRechnung muss den Ort des Käufers (BT-52) enthalten.");
            Need("BR-DE-9", "BT-53", "Eine XRechnung muss die Postleitzahl des Käufers (BT-53) enthalten.");
            Need("BR-DE-15", "BT-10", "Eine XRechnung muss eine Käuferreferenz (BT-10), etwa die Leitweg-ID, enthalten.");
            Need("XR-BT-34", "BT-34", "Eine XRechnung 3.0 muss die elektronische Adresse des Verkäufers (BT-34) enthalten.");
            Need("XR-BT-49", "BT-49", "Eine XRechnung 3.0 muss die elektronische Adresse des Käufers (BT-49) enthalten.");

            Count++;
            var categories = Lines.Select(l => l.Get("BT-151")).Concat(Vat.Select(v => v.Get("BT-118"))).Where(c => c is not null).Select(c => c!.ToUpperInvariant()).ToHashSet();
            if (categories.Overlaps(["S", "Z", "E", "AE", "K", "G", "L", "M"]) && d.Get("BT-31") is null && d.Get("BT-32") is null)
            {
                Add("BR-DE-16", Severity.Error, "Bei steuerbaren Umsätzen muss die USt-IdNr. (BT-31) oder die Steuernummer (BT-32) des Verkäufers angegeben sein.", "BT-31");
            }

            Count++;
            if (d.Get("BT-3") is { } type && !CodeLists.XRechnungInvoiceTypes.Contains(type))
            {
                Add("BR-DE-17", Severity.Error, $"Die Rechnungsart „{type}“ ist in XRechnung nicht erlaubt (erlaubt: 326, 380, 381, 384, 389, 875, 876, 877).", "BT-3", type);
            }

            Count++;
            if (d.Get("BT-81") is "58" && d.Get("BT-84") is { } iban && !TextValues.IsValidIban(iban))
            {
                Add("BR-DE-19", Severity.Error, $"Bei SEPA-Überweisung muss BT-84 eine gültige IBAN sein: „{iban}“.", "BT-84", iban);
            }

            Count++;
            if (!IgnoreSpecification && d.Get("BT-24") is { } spec && !spec.StartsWith(Formats.SpecificationRegistry.XRechnung30, StringComparison.OrdinalIgnoreCase))
            {
                Add("BR-DE-21", Severity.Warning, $"Die Spezifikationskennung „{spec}“ ist nicht die der XRechnung 3.0.", "BT-24", spec);
            }

            Count++;
            if (d.Get("BT-81") is "30" or "58" && d.Get("BT-84") is null)
            {
                Add("BR-DE-23-a", Severity.Error, "Bei Zahlung per Überweisung (Code 30 oder 58) muss die Kontoverbindung (BG-17, BT-84) angegeben sein.", "BT-84");
            }

            Count++;
            if (d.Get("BT-81") is "59" && (d.Get("BT-89") is null || d.Get("BT-91") is null))
            {
                Add("BR-DE-24", Severity.Error, "Bei Lastschrift (Code 59) müssen Mandatsreferenz (BT-89) und belastetes Konto (BT-91) angegeben sein.", "BT-89");
            }

            Count++;
            if (d.Get("BT-3") is "384" && d.Get("BT-25") is null)
            {
                Add("BR-DE-26", Severity.Warning, "Eine Korrekturrechnung (384) sollte die vorausgegangene Rechnung (BT-25) nennen.", "BT-25");
            }

            Count++;
            if (d.Get("BT-42") is { } phone && phone.Count(char.IsDigit) < 3)
            {
                Add("BR-DE-27", Severity.Warning, $"Die Telefonnummer „{phone}“ (BT-42) sollte mindestens drei Ziffern enthalten.", "BT-42", phone);
            }

            Count++;
            if (d.Get("BT-43") is { } mail && (mail.Count(c => c == '@') != 1 || mail.StartsWith('@') || mail.EndsWith('@')))
            {
                Add("BR-DE-28", Severity.Warning, $"Die E-Mail-Adresse „{mail}“ (BT-43) sollte genau ein @ enthalten, nicht am Anfang oder Ende.", "BT-43", mail);
            }
        }

        // ------------------------------------------------------------------------------------------------ Peppol

        public void RunPeppol()
        {
            Count += 7;
            var process = d.Get("BT-23");
            if (process is null)
            {
                Add("PEPPOL-EN16931-R001", Severity.Error, "Der Geschäftsprozess (BT-23) muss angegeben sein.", "BT-23");
            }
            else if (!PeppolProcessRegex().IsMatch(process))
            {
                Add("PEPPOL-EN16931-R007", Severity.Error, $"Der Geschäftsprozess „{process}“ hat nicht die Form urn:fdc:peppol.eu:2017:poacc:billing:NN:1.0.", "BT-23", process);
            }

            if (d.Get("BT-10") is null && d.Get("BT-13") is null)
            {
                Add("PEPPOL-EN16931-R003", Severity.Error, "Eine Käuferreferenz (BT-10) oder eine Bestellnummer (BT-13) muss angegeben sein.", "BT-10");
            }

            if (d.Get("BT-49") is null)
            {
                Add("PEPPOL-EN16931-R010", Severity.Error, "Die elektronische Adresse des Käufers (BT-49) muss angegeben sein.", "BT-49");
            }

            if (d.Get("BT-34") is null)
            {
                Add("PEPPOL-EN16931-R020", Severity.Error, "Die elektronische Adresse des Verkäufers (BT-34) muss angegeben sein.", "BT-34");
            }

            for (var i = 0; i < Lines.Count; i++)
            {
                var line = Lines[i];
                if (line.Decimal("BT-149") is <= 0)
                {
                    Add("PEPPOL-EN16931-R121", Severity.Error, $"Position {i + 1}: Die Basismenge (BT-149) muss größer als 0 sein.", SemanticDocument.ItemKey(SemanticCatalog.Lines, i, "BT-149"));
                }

                if (line.Decimal("BT-129") is { } q && line.Decimal("BT-146") is { } p && line.Decimal("BT-131") is { } net)
                {
                    var expected = Math.Round(q * p / (line.Decimal("BT-149") is { } bq && bq > 0 ? bq : 1m), 2, MidpointRounding.AwayFromZero);
                    if (!Close(expected, net))
                    {
                        Add("PEPPOL-EN16931-R120", Severity.Error, $"Position {i + 1}: Nettobetrag {Money(net)} ≠ Menge × Preis / Basismenge = {Money(expected)}.", SemanticDocument.ItemKey(SemanticCatalog.Lines, i, "BT-131"), Money(net));
                    }
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ plausibility

        public void RunPlausibility()
        {
            Count += 6;
            if (d.Get("BT-84") is { } iban && System.Text.RegularExpressions.Regex.IsMatch(iban.Replace(" ", "", StringComparison.Ordinal), "^[A-Za-z]{2}[0-9]{2}") && !TextValues.IsValidIban(iban))
            {
                Add("PLAUS-IBAN", Severity.Error, $"Die Prüfziffer der IBAN „{iban}“ stimmt nicht.", "BT-84", iban);
            }

            if (d.Date("BT-2") is { } issued && d.Date("BT-9") is { } due && due < issued)
            {
                Add("PLAUS-DUE", Severity.Warning, $"Das Fälligkeitsdatum ({due:yyyy-MM-dd}) liegt vor dem Rechnungsdatum ({issued:yyyy-MM-dd}).", "BT-9", d.Get("BT-9"));
            }

            if (d.Date("BT-2") is { } date && date > o.Today.AddDays(1))
            {
                Add("PLAUS-FUTURE", Severity.Warning, $"Das Rechnungsdatum ({date:yyyy-MM-dd}) liegt in der Zukunft.", "BT-2", d.Get("BT-2"));
            }

            if (d.Date("BT-73") is { } from && d.Date("BT-74") is { } to && to < from)
            {
                Add("PLAUS-PERIOD", Severity.Error, "Das Ende des Leistungszeitraums (BT-74) liegt vor seinem Beginn (BT-73).", "BT-74");
            }

            if (!o.Rules.HasFlag(RuleSets.Peppol))
            {
                for (var i = 0; i < Lines.Count; i++)
                {
                    var line = Lines[i];
                    if (line.Decimal("BT-129") is { } q && line.Decimal("BT-146") is { } p && line.Decimal("BT-131") is { } net)
                    {
                        var basis = line.Decimal("BT-149") is { } bq && bq > 0 ? bq : 1m;
                        var expected = Math.Round(q * p / basis, 2, MidpointRounding.AwayFromZero);
                        if (!Close(expected, net))
                        {
                            Add("PLAUS-LINE", Severity.Warning, $"Position {i + 1}: Nettobetrag {Money(net)} ≠ Menge × Preis = {Money(expected)} (Nachlässe auf Positionsebene können das erklären).", SemanticDocument.ItemKey(SemanticCatalog.Lines, i, "BT-131"), Money(net));
                        }
                    }
                }
            }

            foreach (var (key, value) in d.Uncertain())
            {
                Add("PLAUS-UNSURE", Severity.Warning, $"{SemanticCatalog.LabelOfKey(key)}: unsicher erkannt („{value.Value}“, {value.Source}). Bitte prüfen.", key, value.Value);
            }
        }
    }
}
