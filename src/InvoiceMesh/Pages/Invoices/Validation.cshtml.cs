using InvoiceMesh.Core.Validation;
using InvoiceMesh.Services;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceMesh.Pages.Invoices;

/// <summary>The validation report of a document: the result, the checks that ran (and those that did not, with the reason), the findings, and what an XRechnung still needs.</summary>
public sealed class ValidationModel(InvoiceService invoices) : InvoicePage(invoices)
{
    protected override string TabKey => "validation";

    [BindProperty(SupportsGet = true, Name = "stufe")]
    public string? SeverityFilter { get; set; }

    public List<Finding> Findings { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var all = Ctx.ValidationReport?.Findings ?? [];
        Findings = [.. all.Where(f => SeverityFilter switch
        {
            "fehler" => f.Severity == Severity.Error,
            "warnungen" => f.Severity == Severity.Warning,
            "hinweise" => f.Severity == Severity.Info,
            _ => true,
        }).OrderByDescending(f => f.Severity).ThenBy(f => f.Scope)];
        return Page();
    }
}
