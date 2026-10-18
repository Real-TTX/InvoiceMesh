using System.Text.Json.Nodes;
using InvoiceMesh.Configuration;
using InvoiceMesh.Core;
using InvoiceMesh.Core.Extraction;
using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Mapping;
using InvoiceMesh.Core.Schemas;
using InvoiceMesh.Core.Semantics;
using InvoiceMesh.Core.Validation;
using InvoiceMesh.Data.Entities;

namespace InvoiceMesh.Services;

/// <summary>The specification table of this installation: the built-in identifiers plus those the administrator added.</summary>
public sealed class SpecificationProvider(ConfigStore config)
{
    public SpecificationRegistry Registry => new(config.Current.Formats.Custom.Where(c => !string.IsNullOrWhiteSpace(c.Identifier)).Select(c => c.ToSpecification()));
}

/// <summary>What the mapping wrote down, stored with a revision (with the names and numbers of the schema and the mapping it used).</summary>
public sealed class RevisionReport
{
    public string SchemaName { get; set; } = "";

    public int SchemaVersion { get; set; }

    public string MappingName { get; set; } = "";

    public int MappingVersion { get; set; }

    public long MappingSetId { get; set; }

    public long TargetSchemaId { get; set; }

    public List<FieldOutcome> Fields { get; set; } = [];

    public List<UnusedSource> Unused { get; set; } = [];

    public List<string> DefinitionProblems { get; set; } = [];

    public static RevisionReport Of(MappingReport report, TargetSchema schema, TargetSchemaVersion schemaVersion, MappingSet set, MappingSetVersion mappingVersion) => new()
    {
        SchemaName = schema.Name,
        SchemaVersion = schemaVersion.Number,
        TargetSchemaId = schema.Id,
        MappingName = set.Name,
        MappingVersion = mappingVersion.Number,
        MappingSetId = set.Id,
        Fields = report.Fields,
        Unused = report.Unused,
        DefinitionProblems = report.DefinitionProblems,
    };
}

/// <summary>
/// Everything about a document, read and deserialized once for a page: the document, its current extraction (the raw fields, the semantic model as it was
/// read and as it is after the corrections), the corrections, the current revision with its data and report, the current validation and the files.
/// </summary>
public sealed class InvoiceContext
{
    public required InvoiceDocument Doc { get; init; }

    public InvoiceExtraction? Extraction { get; init; }

    public DetectedFormat? Format { get; init; }

    public List<SourceField> Raw { get; init; } = [];

    public SemanticDocument Extracted { get; init; } = new();

    public SemanticDocument Corrected { get; init; } = new();

    public List<ExtractionMessage> Messages { get; init; } = [];

    public List<InvoiceCorrection> Corrections { get; init; } = [];

    public InvoiceRevision? Revision { get; init; }

    public RevisionReport? Report { get; init; }

    public JsonObject? Data { get; init; }

    public SchemaDefinition? Schema { get; init; }

    public InvoiceValidation? Validation { get; init; }

    public ValidationReport? ValidationReport { get; init; }

    public List<Finding> XRechnungMissing { get; init; } = [];

    public List<GeneratedFile> Files { get; init; } = [];

    /// <summary>The corrections that count: the newest per key, unless it was taken back.</summary>
    public IEnumerable<InvoiceCorrection> ActiveCorrections(CorrectionScope scope, long? schemaId = null)
        => Corrections.Where(c => c.Scope == scope && (scope == CorrectionScope.Source || c.TargetSchemaId == schemaId))
            .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Id).First())
            .Where(c => !c.Reverted);

    public static ExtractionResultParts Parse(InvoiceExtraction e) => new(
        CoreJson.Deserialize<DetectedFormat>(e.FormatJson),
        CoreJson.Deserialize<List<SourceField>>(e.SourceFieldsJson),
        CoreJson.Deserialize<SemanticDocument>(e.SemanticJson),
        CoreJson.Deserialize<List<ExtractionMessage>>(e.MessagesJson));

    /// <summary>The semantic model with the corrections of the source laid over it. A corrected value is certain: a person set it.</summary>
    public static SemanticDocument Apply(SemanticDocument extracted, IEnumerable<InvoiceCorrection> sourceCorrections)
    {
        var doc = extracted.Clone();
        foreach (var c in sourceCorrections)
        {
            doc.Put(c.Key, c.Value, Confidence.High, $"Korrektur von {c.UserName ?? "unbekannt"}" + (string.IsNullOrWhiteSpace(c.Reason) ? "" : $": {c.Reason}"));
        }

        return doc;
    }
}

public sealed record ExtractionResultParts(DetectedFormat Format, List<SourceField> Raw, SemanticDocument Semantic, List<ExtractionMessage> Messages);
