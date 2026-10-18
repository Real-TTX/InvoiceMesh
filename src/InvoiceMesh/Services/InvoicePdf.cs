using System.Globalization;
using InvoiceMesh.Core.Semantics;
using InvoiceMesh.Core.Validation;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace InvoiceMesh.Services;

/// <summary>
/// The PDFs InvoiceMesh draws itself (QuestPDF): the readable view of a document that has no PDF of its own (XML, an image), drawn from the semantic model
/// after the corrections, and the validation report. Both say plainly that they are made by InvoiceMesh and are not the original.
/// </summary>
public sealed class InvoicePdf
{
    private const string Ink = "#1b2a3d";
    private const string Muted = "#5b6b7f";
    private const string Rule = "#d9e2ec";
    private const string Soft = "#eef5fc";
    private const string Accent = "#1565c0";

    public byte[] Invoice(InvoiceContext c)
    {
        var d = c.Corrected;
        var currency = d.Get("BT-5") ?? "";
        string Money(string id) => Amount(d.Get(id), currency);
        var lines = d.Items(SemanticCatalog.Lines);
        var vat = d.Items(SemanticCatalog.VatBreakdown);

        return Document.Create(doc => doc.Page(page =>
        {
            Setup(page);
            page.Header().Column(head =>
            {
                head.Item().Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text(d.Get("BT-27") ?? d.Get("BT-28") ?? "Verkäufer unbekannt").FontSize(14).SemiBold();
                        col.Item().Text(Address(d, 35, 36, 38, 37, 40)).FontColor(Muted);
                        Optional(col, "USt-IdNr.", d.Get("BT-31"));
                        Optional(col, "Steuernummer", d.Get("BT-32"));
                    });
                    row.ConstantItem(70, Unit.Millimetre).AlignRight().Column(col =>
                    {
                        col.Item().AlignRight().Text(TypeLabel(d.Get("BT-3"))).FontSize(18).SemiBold().FontColor(Accent);
                        col.Item().AlignRight().Text(d.Get("BT-1") ?? "ohne Nummer").FontSize(11);
                    });
                });
                head.Item().PaddingTop(4).LineHorizontal(0.75f).LineColor(Rule);
            });

            page.Content().PaddingTop(8).Column(col =>
            {
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(buyer =>
                    {
                        buyer.Item().Text("Rechnungsempfänger").FontSize(8).FontColor(Muted);
                        buyer.Item().Text(d.Get("BT-44") ?? d.Get("BT-45") ?? "–").SemiBold();
                        buyer.Item().Text(Address(d, 50, 51, 53, 52, 55));
                        Optional(buyer, "USt-IdNr.", d.Get("BT-48"));
                    });
                    row.ConstantItem(70, Unit.Millimetre).Column(facts =>
                    {
                        Fact(facts, "Rechnungsdatum", Date(d.Get("BT-2")));
                        Fact(facts, "Fällig am", Date(d.Get("BT-9")));
                        Fact(facts, "Lieferdatum", Date(d.Get("BT-72")));
                        if (d.Get("BT-73") is not null || d.Get("BT-74") is not null)
                        {
                            Fact(facts, "Leistungszeitraum", $"{Date(d.Get("BT-73"))} – {Date(d.Get("BT-74"))}");
                        }

                        Fact(facts, "Käuferreferenz", d.Get("BT-10"));
                        Fact(facts, "Bestellnummer", d.Get("BT-13"));
                        Fact(facts, "Währung", currency);
                    });
                });

                col.Item().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        cols.ConstantColumn(22);
                        cols.RelativeColumn(5);
                        cols.RelativeColumn(1.4f);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(1.2f);
                        cols.RelativeColumn(2);
                    });
                    table.Header(h =>
                    {
                        foreach (var (text, right) in new[] { ("Pos.", false), ("Bezeichnung", false), ("Menge", true), ("Einzelpreis", true), ("USt", true), ("Netto", true) })
                        {
                            var cell = h.Cell().Background(Soft).Padding(4);
                            (right ? cell.AlignRight() : cell).Text(text).SemiBold().FontSize(8.5f);
                        }
                    });

                    if (lines.Count == 0)
                    {
                        table.Cell().ColumnSpan(6).Padding(6).Text("Keine Positionen erkannt.").FontColor(Muted).Italic();
                    }

                    for (var i = 0; i < lines.Count; i++)
                    {
                        var l = lines[i];
                        table.Cell().BorderBottom(0.5f).BorderColor(Rule).Padding(4).Text(l.Get("BT-126") ?? (i + 1).ToString(CultureInfo.InvariantCulture));
                        table.Cell().BorderBottom(0.5f).BorderColor(Rule).Padding(4).Column(name =>
                        {
                            name.Item().Text(l.Get("BT-153") ?? "–");
                            if (l.Get("BT-154") is { } description)
                            {
                                name.Item().Text(description).FontSize(8).FontColor(Muted);
                            }
                        });
                        table.Cell().BorderBottom(0.5f).BorderColor(Rule).Padding(4).AlignRight().Text($"{Num(l.Get("BT-129"))} {l.Get("BT-130")}".Trim());
                        table.Cell().BorderBottom(0.5f).BorderColor(Rule).Padding(4).AlignRight().Text(Amount(l.Get("BT-146"), currency, 4));
                        table.Cell().BorderBottom(0.5f).BorderColor(Rule).Padding(4).AlignRight().Text(l.Get("BT-152") is { } r ? Num(r) + " %" : "");
                        table.Cell().BorderBottom(0.5f).BorderColor(Rule).Padding(4).AlignRight().Text(Amount(l.Get("BT-131"), currency));
                    }
                });

                col.Item().PaddingTop(6).AlignRight().Width(85, Unit.Millimetre).Column(totals =>
                {
                    Total(totals, "Summe Positionen", Money("BT-106"));
                    if (d.Get("BT-107") is not null)
                    {
                        Total(totals, "Nachlässe", "− " + Money("BT-107"));
                    }

                    if (d.Get("BT-108") is not null)
                    {
                        Total(totals, "Zuschläge", Money("BT-108"));
                    }

                    Total(totals, "Netto", Money("BT-109"));
                    foreach (var v in vat)
                    {
                        Total(totals, $"USt {Num(v.Get("BT-119"))} % ({v.Get("BT-118")}) auf {Amount(v.Get("BT-116"), currency)}", Amount(v.Get("BT-117"), currency));
                    }

                    Total(totals, "Brutto", Money("BT-112"), bold: true);
                    if (d.Get("BT-113") is not null)
                    {
                        Total(totals, "Bereits gezahlt", Money("BT-113"));
                    }

                    Total(totals, "Zahlbetrag", Money("BT-115"), bold: true);
                });

                if (d.Get("BT-20") is not null || d.Get("BT-84") is not null)
                {
                    col.Item().PaddingTop(10).Background(Soft).Padding(8).Column(pay =>
                    {
                        pay.Item().Text("Zahlung").SemiBold();
                        Optional(pay, "Bedingungen", d.Get("BT-20"));
                        Optional(pay, "IBAN", d.Get("BT-84"));
                        Optional(pay, "BIC", d.Get("BT-86"));
                        Optional(pay, "Verwendungszweck", d.Get("BT-83"));
                    });
                }

                if (d.Get("BT-22") is { } note)
                {
                    col.Item().PaddingTop(8).Text(note).FontColor(Muted);
                }

                var uncertain = d.Uncertain().Count();
                col.Item().PaddingTop(12).Text(text =>
                {
                    text.Span("Hinweis: ").SemiBold();
                    text.Span($"Diese Darstellung hat InvoiceMesh aus „{c.Doc.FileName}“ ({c.Format?.Summary}) erzeugt. Sie ist nicht das Original.");
                    if (uncertain > 0)
                    {
                        text.Span($" {uncertain} Wert(e) wurden aus Text gelesen und sind unsicher.");
                    }

                    if (c.Validation is { } v)
                    {
                        text.Span($" Validierung: {InvoiceService.ValidationLabel(v.Status)} ({v.Errors} Fehler, {v.Warnings} Warnungen).");
                    }
                });
            });

            Footer(page);
        })).GeneratePdf();
    }

    public byte[] ValidationReport(InvoiceContext c)
    {
        var report = c.ValidationReport ?? new ValidationReport();
        return Document.Create(doc => doc.Page(page =>
        {
            Setup(page);
            page.Header().Column(head =>
            {
                head.Item().Text("Validierungsbericht").FontSize(18).SemiBold().FontColor(Accent);
                head.Item().Text($"{c.Doc.DisplayName} · {c.Doc.FileName}").FontColor(Muted);
                head.Item().PaddingTop(4).LineHorizontal(0.75f).LineColor(Rule);
            });

            page.Content().PaddingTop(8).Column(col =>
            {
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(facts =>
                    {
                        Fact(facts, "Format", c.Format?.Summary);
                        Fact(facts, "Verarbeitung", c.Doc.ProcessingStatus.ToString());
                        Fact(facts, "Ergebnis", InvoiceService.ValidationLabel(report.Status));
                        Fact(facts, "Geprüft am", c.Validation is null ? null : Fmt.Moment(c.Validation.CreateDate));
                    });
                    row.RelativeItem().Column(facts =>
                    {
                        Fact(facts, "Fehler", report.Errors.ToString(CultureInfo.InvariantCulture));
                        Fact(facts, "Warnungen", report.Warnings.ToString(CultureInfo.InvariantCulture));
                        Fact(facts, "Hinweise", report.Infos.ToString(CultureInfo.InvariantCulture));
                        Fact(facts, "XRechnung möglich", c.Validation?.XRechnungReady == true ? "ja" : "nein");
                    });
                });

                col.Item().PaddingTop(10).Text("Durchgeführte Prüfungen").SemiBold().FontSize(11);
                foreach (var check in report.Checks)
                {
                    col.Item().PaddingTop(2).Text(t =>
                    {
                        t.Span(check.Performed ? "✓ " : "– ").FontColor(check.Performed ? "#1b7f3b" : Muted);
                        t.Span($"{Core.Validation.ValidationReport.ScopeLabel(check.Scope)}: {check.Title}");
                        t.Span(check.Performed ? $" ({check.RuleCount} Regeln)" : $" – nicht geprüft: {check.Reason}").FontColor(Muted);
                    });
                }

                col.Item().PaddingTop(10).Text("Befunde").SemiBold().FontSize(11);
                if (report.Findings.Count == 0)
                {
                    col.Item().Text("Keine Befunde.").FontColor(Muted);
                }

                foreach (var f in report.Findings.OrderByDescending(f => f.Severity))
                {
                    col.Item().PaddingTop(3).Row(row =>
                    {
                        row.ConstantItem(22, Unit.Millimetre).Text(f.Severity switch { Severity.Error => "Fehler", Severity.Warning => "Warnung", _ => "Hinweis" })
                            .FontColor(f.Severity switch { Severity.Error => "#b3261e", Severity.Warning => "#8a5300", _ => Muted }).SemiBold();
                        row.ConstantItem(34, Unit.Millimetre).Text(f.RuleId).FontSize(8).FontColor(Muted);
                        row.RelativeItem().Text(f.Message);
                    });
                }

                if (c.XRechnungMissing.Count > 0)
                {
                    col.Item().PaddingTop(10).Text("Für eine XRechnung fehlt").SemiBold().FontSize(11);
                    foreach (var f in c.XRechnungMissing)
                    {
                        col.Item().PaddingTop(2).Text($"{f.RuleId}: {f.Message}");
                    }
                }

                col.Item().PaddingTop(12).Text("InvoiceMesh prüft die Geschäftsregeln von EN 16931, XRechnung (BR-DE) und Peppol BIS, die es kennt. Die offizielle Prüfung einer XRechnung macht der Validator der KoSIT; dieser Bericht ersetzt sie nicht.")
                    .FontSize(8).FontColor(Muted);
            });

            Footer(page);
        })).GeneratePdf();
    }

    // ------------------------------------------------------------------------------------------------------------ helpers

    private static void Setup(PageDescriptor page)
    {
        page.Size(PageSizes.A4);
        page.Margin(18, Unit.Millimetre);
        page.PageColor(Colors.White);
        page.DefaultTextStyle(x => x.FontSize(9.5f).FontColor(Ink).LineHeight(1.3f));
    }

    private static void Footer(PageDescriptor page)
        => page.Footer().AlignCenter().Text(t =>
        {
            t.Span("Erzeugt von InvoiceMesh · Seite ").FontSize(7.5f).FontColor(Muted);
            t.CurrentPageNumber().FontSize(7.5f).FontColor(Muted);
            t.Span(" von ").FontSize(7.5f).FontColor(Muted);
            t.TotalPages().FontSize(7.5f).FontColor(Muted);
        });

    private static void Optional(ColumnDescriptor col, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            col.Item().Text($"{label}: {value}").FontSize(8.5f);
        }
    }

    private static void Fact(ColumnDescriptor col, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        col.Item().Row(row =>
        {
            row.ConstantItem(32, Unit.Millimetre).Text(label).FontColor(Muted).FontSize(8.5f);
            row.RelativeItem().Text(value);
        });
    }

    private static void Total(ColumnDescriptor col, string label, string value, bool bold = false)
        => col.Item().PaddingVertical(1).Row(row =>
        {
            var l = row.RelativeItem().Text(label);
            var v = row.ConstantItem(32, Unit.Millimetre).AlignRight().Text(value);
            if (bold)
            {
                l.SemiBold();
                v.SemiBold();
            }
        });

    private static string Address(SemanticDocument d, int street, int line2, int postcode, int city, int country)
    {
        string? V(int bt) => d.Get("BT-" + bt);
        return string.Join("\n", new[] { V(street), V(line2), string.Join(" ", new[] { V(postcode), V(city) }.Where(s => s is not null)), V(country) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static string Date(string? iso) => SemanticDocument.ParseDate(iso) is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : iso ?? "";

    private static string Num(string? value) => SemanticDocument.ParseDecimal(value) is { } d ? d.ToString("0.####", Fmt.Culture) : value ?? "";

    private static string Amount(string? value, string currency, int decimals = 2)
        => SemanticDocument.ParseDecimal(value) is { } d ? d.ToString(decimals == 2 ? "N2" : "#,##0.00##", Fmt.Culture) + (currency.Length > 0 ? " " + currency : "") : "–";

    public static string TypeLabel(string? code) => code switch
    {
        "381" => "Gutschrift",
        "384" => "Korrekturrechnung",
        "326" => "Teilrechnung",
        "875" or "876" or "877" => "Abschlagsrechnung",
        "389" => "Eigenrechnung",
        _ => "Rechnung",
    };
}
