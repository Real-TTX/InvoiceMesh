namespace InvoiceMesh.Data.Entities;

/// <summary>The life of a version of a schema or a mapping: worked on, in force, or replaced. Only a draft may change.</summary>
public enum VersionState
{
    Draft,
    Active,
    Retired,
}

/// <summary>A target schema of the user: name and description; its fields live in its versions.</summary>
public class TargetSchema : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>The schema new documents are normalized into.</summary>
    public bool IsDefault { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    public List<TargetSchemaVersion> Versions { get; set; } = [];
}

/// <summary>A version of a schema: the definition (groups and fields) as JSON. An active version never changes; a change makes a new draft.</summary>
public class TargetSchemaVersion : AuditedEntity
{
    public long TargetSchemaId { get; set; }

    public TargetSchema? TargetSchema { get; set; }

    public int Number { get; set; }

    public VersionState State { get; set; } = VersionState.Draft;

    public string DefinitionJson { get; set; } = "{}";

    public string? ChangeNote { get; set; }

    public DateTime? ActivatedDate { get; set; }

    public string? ActivatedBy { get; set; }
}

/// <summary>
/// A mapping from the sources to a schema. It applies to the documents its selector matches (syntax, standard, profile, version; empty means any);
/// when several match, the most specific wins, then the higher priority.
/// </summary>
public class MappingSet : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public long TargetSchemaId { get; set; }

    public TargetSchema? TargetSchema { get; set; }

    /// <summary>"Ubl", "Cii", "None" (text and scans), or null for any.</summary>
    public string? Syntax { get; set; }

    /// <summary>A part of the standard ("XRechnung"), or null for any.</summary>
    public string? Standard { get; set; }

    public string? Profile { get; set; }

    public string? FormatVersion { get; set; }

    public int Priority { get; set; }

    public bool IsEnabled { get; set; } = true;

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    public List<MappingSetVersion> Versions { get; set; } = [];

    public int Specificity => (Syntax is null ? 0 : 1) + (Standard is null ? 0 : 1) + (Profile is null ? 0 : 1) + (FormatVersion is null ? 0 : 1);
}

/// <summary>A version of a mapping: the rules as JSON, for one version of its schema.</summary>
public class MappingSetVersion : AuditedEntity
{
    public long MappingSetId { get; set; }

    public MappingSet? MappingSet { get; set; }

    public int Number { get; set; }

    public VersionState State { get; set; } = VersionState.Draft;

    public string DefinitionJson { get; set; } = "{}";

    public string? ChangeNote { get; set; }

    public DateTime? ActivatedDate { get; set; }

    public string? ActivatedBy { get; set; }

    /// <summary>The document the preview of this version uses (the user's choice).</summary>
    public long? SampleInvoiceId { get; set; }
}

/// <summary>A configured export: a schema, a format and its options (JSON).</summary>
public class ExportProfile : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public long TargetSchemaId { get; set; }

    public TargetSchema? TargetSchema { get; set; }

    public string OptionsJson { get; set; } = "{}";

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}
