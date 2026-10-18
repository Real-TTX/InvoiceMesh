using InvoiceMesh.Core.Validation;

namespace InvoiceMesh.Data.Entities;

/// <summary>Where a document came from.</summary>
public enum InvoiceSource
{
    Upload,
    Demo,
}

/// <summary>The state of the processing of a document, kept apart from the result of its validation (<see cref="ValidationStatus"/>).</summary>
public enum ProcessingStatus
{
    Queued,
    Processing,
    Processed,
    Failed,
}

/// <summary>
/// A document that came in. The original file (<see cref="OriginalFile"/>) never changes; everything that was made of it is kept beside it and can be
/// traced: the extractions (<see cref="InvoiceExtraction"/>), the corrections, the normalized revisions, the validations, the generated files and the events.
/// The columns of the list (number, date, parties, total) are a copy of the current state, for searching and sorting.
/// </summary>
public class InvoiceDocument : AuditedEntity, ISoftDeletable
{
    public string FileName { get; set; } = "";

    public string ContentType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = "";

    public long OriginalFileId { get; set; }

    public StoredFile? OriginalFile { get; set; }

    /// <summary>The XML of a hybrid PDF, stored next to the original as it was found inside.</summary>
    public long? EmbeddedXmlFileId { get; set; }

    public StoredFile? EmbeddedXmlFile { get; set; }

    public InvoiceSource Source { get; set; } = InvoiceSource.Upload;

    public DateTime ReceivedDate { get; set; }

    public ProcessingStatus ProcessingStatus { get; set; } = ProcessingStatus.Queued;

    public string? ProcessingError { get; set; }

    public DateTime? ProcessedDate { get; set; }

    public ValidationStatus ValidationStatus { get; set; } = ValidationStatus.NotValidated;

    public int ErrorCount { get; set; }

    public int WarningCount { get; set; }

    /// <summary>Values were read from text or a scan, or the validation found errors: a person should look.</summary>
    public bool NeedsReview { get; set; }

    /// <summary>A person looked at it and confirmed the data (cleared by a new processing).</summary>
    public DateTime? ReviewedDate { get; set; }

    public long? ReviewedUserId { get; set; }

    public bool XRechnungReady { get; set; }

    // ------------------------------------------------------------------------------------------- the format, as detected

    public string FileKind { get; set; } = "";

    public string? Syntax { get; set; }

    public string? Standard { get; set; }

    public string? Profile { get; set; }

    public string? FormatVersion { get; set; }

    /// <summary>"UBL", "CII", "PDF+CII", "PDF", "Scan", "Bild", "XML".</summary>
    public string FormatCode { get; set; } = "";

    public string? FormatSummary { get; set; }

    public string? Method { get; set; }

    // ------------------------------------------------------------------------------------------- a copy of the current data, for the list

    public string? InvoiceNumber { get; set; }

    public DateOnly? InvoiceDate { get; set; }

    public DateOnly? DueDate { get; set; }

    public string? SellerName { get; set; }

    public string? BuyerName { get; set; }

    public string? Currency { get; set; }

    public decimal? GrossTotal { get; set; }

    public string? TypeCode { get; set; }

    public long? CurrentExtractionId { get; set; }

    public long? CurrentRevisionId { get; set; }

    public long? CurrentValidationId { get; set; }

    /// <summary>The schema version of the current revision: compared with the active version to show "Schema veraltet".</summary>
    public long? SchemaVersionId { get; set; }

    public long? MappingVersionId { get; set; }

    public string? Note { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    public string DisplayName => InvoiceNumber ?? FileName;
}

/// <summary>One run of the reader over the original: what it found (the format, the raw fields, the semantic model, the text), as JSON.</summary>
public class InvoiceExtraction : AuditedEntity
{
    public long InvoiceId { get; set; }

    public InvoiceDocument? Invoice { get; set; }

    public int Number { get; set; }

    public string Method { get; set; } = "";

    public string FormatJson { get; set; } = "{}";

    public string SourceFieldsJson { get; set; } = "[]";

    public string SemanticJson { get; set; } = "{}";

    public string MessagesJson { get; set; } = "[]";

    public string? Text { get; set; }

    public double? OcrConfidence { get; set; }

    public string? OcrEngine { get; set; }

    public int DurationMs { get; set; }
}

public enum CorrectionScope
{
    /// <summary>A business term of the semantic model ("BT-1", "BG-25[2]/BT-129"): affects the normalized data, the validation and the XRechnung.</summary>
    Source,

    /// <summary>A field of a user's schema ("seller.costCenter", "lines[2].account"): affects only the normalized data of that schema.</summary>
    Target,
}

/// <summary>
/// A manual correction. It never changes the extraction: it is laid over it when the data are normalized, so the original value, the corrected value,
/// who and why stay visible. The newest correction of a key counts; <see cref="Reverted"/> takes a correction back.
/// </summary>
public class InvoiceCorrection : AuditedEntity
{
    public long InvoiceId { get; set; }

    public InvoiceDocument? Invoice { get; set; }

    public CorrectionScope Scope { get; set; }

    /// <summary>For <see cref="CorrectionScope.Target"/>: the schema the field belongs to.</summary>
    public long? TargetSchemaId { get; set; }

    public string Key { get; set; } = "";

    public string? Value { get; set; }

    public string? PreviousValue { get; set; }

    public string? Reason { get; set; }

    public bool Reverted { get; set; }

    public string? UserName { get; set; }
}

/// <summary>The normalized data of a document in one schema version, made by one mapping version: never changed, a new one is made instead.</summary>
public class InvoiceRevision : AuditedEntity
{
    public long InvoiceId { get; set; }

    public InvoiceDocument? Invoice { get; set; }

    public int Number { get; set; }

    public long ExtractionId { get; set; }

    public long SchemaVersionId { get; set; }

    public TargetSchemaVersion? SchemaVersion { get; set; }

    public long MappingVersionId { get; set; }

    public MappingSetVersion? MappingVersion { get; set; }

    /// <summary>Why it was made: "Verarbeitung", "Korrektur", "Neu normalisiert".</summary>
    public string Reason { get; set; } = "";

    public string DataJson { get; set; } = "{}";

    public string ReportJson { get; set; } = "{}";

    public int CorrectionCount { get; set; }
}

/// <summary>One validation of a document: the findings and the checks that ran, as JSON, and the counts.</summary>
public class InvoiceValidation : AuditedEntity
{
    public long InvoiceId { get; set; }

    public InvoiceDocument? Invoice { get; set; }

    public long? RevisionId { get; set; }

    public long ExtractionId { get; set; }

    public ValidationStatus Status { get; set; }

    public int Errors { get; set; }

    public int Warnings { get; set; }

    public int Infos { get; set; }

    public string ReportJson { get; set; } = "{}";

    public bool XRechnungReady { get; set; }

    public string XRechnungMissingJson { get; set; } = "[]";
}

public enum GeneratedKind
{
    XRechnungUbl,
    XRechnungCii,
    Pdf,
    ValidationReportPdf,
    Json,
}

/// <summary>A file InvoiceMesh made of a document. Kept with what it was made from, so that it can be traced.</summary>
public class GeneratedFile : AuditedEntity
{
    public long InvoiceId { get; set; }

    public InvoiceDocument? Invoice { get; set; }

    public GeneratedKind Kind { get; set; }

    public long StoredFileId { get; set; }

    public StoredFile? StoredFile { get; set; }

    public long? ExtractionId { get; set; }

    public long? RevisionId { get; set; }

    public long? ValidationId { get; set; }

    /// <summary>A check of the file after it was made (an XRechnung read back and validated): "geprüft: 0 Fehler".</summary>
    public string? Check { get; set; }
}

/// <summary>The history of a document: received, processed, corrected, generated, shared.</summary>
public class InvoiceEvent : AuditedEntity
{
    public long InvoiceId { get; set; }

    public InvoiceDocument? Invoice { get; set; }

    public DateTime OccurredDate { get; set; }

    public string Kind { get; set; } = "";

    public string Message { get; set; } = "";

    public string? UserName { get; set; }
}
