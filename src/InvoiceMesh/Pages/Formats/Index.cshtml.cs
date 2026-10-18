using InvoiceMesh.Core.Formats;
using InvoiceMesh.Data;
using InvoiceMesh.Services;
using InvoiceMesh.Services.Processing;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Formats;

/// <summary>What InvoiceMesh reads: the kinds of files, and the standards, profiles and versions it recognises by their specification identifier, with the rules it applies to each.</summary>
public sealed class IndexModel(SpecificationProvider specs, TesseractOcr ocr, ImDbContext db, ICurrentUser user) : PageModelBase
{
    public IReadOnlyList<Specification> All { get; private set; } = [];

    public Dictionary<string, int> Seen { get; private set; } = [];

    public bool OcrAvailable => ocr.IsAvailable;

    public string? OcrVersion => ocr.EngineVersion;

    public bool IsAdmin => user.IsAdmin;

    public async Task OnGetAsync()
    {
        Head("Formate", "Welche Dateien und Rechnungsstandards InvoiceMesh erkennt. Erkannt wird am Inhalt, nie an der Dateiendung.", ("Formate", null));
        All = specs.Registry.All;
        Seen = await db.Invoice.Where(i => i.Standard != null).GroupBy(i => i.Standard!).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public static string Rules(RuleSets r) => r == RuleSets.None ? "nur allgemeine Prüfungen"
        : string.Join(", ", new[] { r.HasFlag(RuleSets.En16931) ? "EN 16931" : null, r.HasFlag(RuleSets.XRechnung) ? "XRechnung (BR-DE)" : null, r.HasFlag(RuleSets.Peppol) ? "Peppol BIS" : null }.Where(s => s is not null));

    public static string Syntaxes(IReadOnlyList<InvoiceSyntax> s) => s.Count == 0 ? "beliebig" : string.Join(", ", s.Select(x => x switch { InvoiceSyntax.Ubl => "UBL", InvoiceSyntax.Cii => "CII", InvoiceSyntax.CiiZugferdV1 => "CII (D13B)", _ => x.ToString() }));
}
