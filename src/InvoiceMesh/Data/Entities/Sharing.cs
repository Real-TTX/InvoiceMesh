namespace InvoiceMesh.Data.Entities;

/// <summary>
/// The anonymous role: a link that shows one document read-only (its data, the result of the validation, the files the link allows), for a limited time,
/// optionally behind a password. The visitor has no account and sees nothing but this document.
/// </summary>
public class ShareLink : AuditedEntity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long InvoiceId { get; set; }

    public InvoiceDocument? Invoice { get; set; }

    public string Title { get; set; } = "";

    /// <summary>A message to the visitor, shown on the page.</summary>
    public string? Message { get; set; }

    public DateTime ExpiresDate { get; set; }

    public string? PasswordHash { get; set; }

    public string? RecipientName { get; set; }

    /// <summary>The visitor may download the original file.</summary>
    public bool AllowOriginal { get; set; } = true;

    /// <summary>The visitor may download the generated files (XRechnung, PDF, validation report).</summary>
    public bool AllowGenerated { get; set; } = true;

    public DateTime? RevokedDate { get; set; }

    public int ViewCount { get; set; }

    public DateTime? LastViewDate { get; set; }

    public bool IsActive(DateTime nowUtc) => RevokedDate is null && ExpiresDate > nowUtc;
}
