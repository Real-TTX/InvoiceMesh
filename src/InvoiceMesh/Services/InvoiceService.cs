using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using InvoiceMesh.Configuration;
using InvoiceMesh.Core;
using InvoiceMesh.Core.Extraction;
using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Mapping;
using InvoiceMesh.Core.Output;
using InvoiceMesh.Core.Semantics;
using InvoiceMesh.Core.Validation;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services.Processing;
using InvoiceMesh.Storage;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Services;

public sealed class InvoiceException(string message) : Exception(message);

/// <summary>
/// The life of a document: it comes in (the original is stored and never touched again), it is read (format, raw fields, semantic model), corrections are
/// laid over it, it is normalized into a schema by a mapping, validated, and files are made of it. Every step leaves a record (extraction, revision,
/// validation, generated file, event), so that what InvoiceMesh shows can always be traced back to the original and to the versions of the definitions.
/// The processing result and the validation result are kept apart: a document can be processed perfectly and still be invalid.
/// </summary>
public sealed class InvoiceService(
    ImDbContext db,
    FileStoreService files,
    DefinitionService definitions,
    SpecificationProvider specs,
    IOcrEngine ocr,
    ProcessingQueue queue,
    ConfigStore config,
    InvoicePdf pdf,
    ICurrentUser user,
    TimeProvider clock,
    ILogger<InvoiceService> logger)
{
    private DateOnly Today => Fmt.Today(clock);

    // ------------------------------------------------------------------------------------------------------------ coming in

    public sealed record AddResult(InvoiceDocument Document, InvoiceDocument? DuplicateOf);

    /// <summary>Stores the original as it came and queues the document for processing.</summary>
    public async Task<AddResult> AddAsync(byte[] bytes, string fileName, InvoiceSource source = InvoiceSource.Upload, CancellationToken ct = default)
    {
        var max = config.Current.Processing.MaxUploadMb * 1024L * 1024L;
        if (bytes.LongLength == 0)
        {
            throw new InvoiceException($"„{fileName}“ ist leer.");
        }

        if (bytes.LongLength > max)
        {
            throw new InvoiceException($"„{fileName}“ ist größer als {config.Current.Processing.MaxUploadMb} MB.");
        }

        var kind = InvoiceReader.Sniff(bytes, out var mediaType, out _);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var duplicate = await db.Invoice.AsNoTracking().FirstOrDefaultAsync(i => i.Sha256 == sha, ct);
        var safeName = FileStoreService.SafeFileName(fileName);
        var original = await files.SaveAsync(bytes, safeName, mediaType, ct);
        var doc = new InvoiceDocument
        {
            FileName = safeName,
            ContentType = mediaType,
            SizeBytes = bytes.LongLength,
            Sha256 = sha,
            OriginalFile = original,
            Source = source,
            ReceivedDate = clock.GetUtcNow().UtcDateTime,
            FileKind = kind.ToString(),
            FormatCode = kind switch { FileKind.Pdf => "PDF", FileKind.Image => "Bild", FileKind.Xml => "XML", _ => "?" },
        };
        db.Invoice.Add(doc);
        await db.SaveChangesAsync(ct);
        await EventAsync(doc, "Eingang", duplicate is null
            ? $"„{safeName}“ ({Fmt.FileSize(bytes.LongLength)}) aufgenommen. SHA-256 {sha[..16]}…"
            : $"„{safeName}“ aufgenommen. Dieselbe Datei gibt es schon (Beleg #{duplicate.Id}).", ct);
        queue.Enqueue(doc.Id);
        return new AddResult(doc, duplicate);
    }

    /// <summary>Reads the original again from the start (a new extraction); corrections stay and are laid over the new one.</summary>
    public async Task ReprocessAsync(InvoiceDocument doc, CancellationToken ct = default)
    {
        doc.ProcessingStatus = ProcessingStatus.Queued;
        doc.ProcessingError = null;
        await db.SaveChangesAsync(ct);
        await EventAsync(doc, "Neu verarbeiten", "Die Originaldatei wird neu ausgelesen.", ct);
        queue.Enqueue(doc.Id);
    }

    // ------------------------------------------------------------------------------------------------------------ processing

    /// <summary>The whole processing of one document. A failure is written to the document, never thrown at the worker.</summary>
    public async Task ProcessAsync(long id, CancellationToken ct = default)
    {
        // The document is claimed in one statement: of two workers (or a worker and a test) only one processes it.
        var claimed = await db.Invoice.Where(i => i.Id == id && i.ProcessingStatus == ProcessingStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ProcessingStatus, ProcessingStatus.Processing), ct);
        if (claimed == 0)
        {
            return;
        }

        var doc = await db.Invoice.Include(i => i.OriginalFile).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (doc is null)
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        try
        {
            var bytes = await files.ReadAllAsync(doc.OriginalFile!, ct);
            var result = await new InvoiceReader(specs.Registry, ocr).ReadAsync(bytes, doc.FileName, ct);
            var extraction = await SaveExtractionAsync(doc, result, (int)watch.ElapsedMilliseconds, ct);

            if (result.Method == ExtractionMethod.None)
            {
                // Nothing could be read: the document stays, with the reason in words, and is validated only as far as its format goes.
                doc.ProcessingStatus = ProcessingStatus.Failed;
                doc.ProcessingError = result.Messages.FirstOrDefault(m => m.Severity == MessageSeverity.Error)?.Text ?? "Die Datei konnte nicht ausgelesen werden.";
                await ValidateAsync(doc, extraction, null, ct);
                await db.SaveChangesAsync(ct);
                await EventAsync(doc, "Verarbeitung fehlgeschlagen", doc.ProcessingError, ct);
                return;
            }

            var revision = await NormalizeAsync(doc, extraction, "Verarbeitung", null, ct);
            await ValidateAsync(doc, extraction, revision, ct);
            await MakeOutputsAsync(doc, ct);

            doc.ProcessingStatus = ProcessingStatus.Processed;
            doc.ProcessingError = null;
            doc.ProcessedDate = clock.GetUtcNow().UtcDateTime;
            doc.ReviewedDate = null;
            doc.ReviewedUserId = null;
            await db.SaveChangesAsync(ct);
            await EventAsync(doc, "Verarbeitet", $"{result.Format.Summary}, gelesen per {MethodLabel(result.Method)} in {watch.ElapsedMilliseconds} ms. Validierung: {ValidationLabel(doc.ValidationStatus)}.", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Document {Id} could not be processed.", id);
            db.ChangeTracker.Clear();
            var fresh = await db.Invoice.FirstAsync(i => i.Id == id, ct);
            fresh.ProcessingStatus = ProcessingStatus.Failed;
            fresh.ProcessingError = ex is InvoiceException or DefinitionException or StorageException ? ex.Message : "Ein unerwarteter Fehler ist aufgetreten: " + ex.Message;
            await db.SaveChangesAsync(ct);
            await EventAsync(fresh, "Verarbeitung fehlgeschlagen", fresh.ProcessingError, ct);
        }
    }

    private async Task<InvoiceExtraction> SaveExtractionAsync(InvoiceDocument doc, ExtractionResult result, int duration, CancellationToken ct)
    {
        var number = await db.InvoiceExtraction.Where(e => e.InvoiceId == doc.Id).MaxAsync(e => (int?)e.Number, ct) ?? 0;
        var extraction = new InvoiceExtraction
        {
            InvoiceId = doc.Id,
            Number = number + 1,
            Method = result.Method.ToString(),
            FormatJson = CoreJson.Serialize(result.Format),
            SourceFieldsJson = CoreJson.Serialize(result.SourceFields),
            SemanticJson = CoreJson.Serialize(result.Semantic),
            MessagesJson = CoreJson.Serialize(result.Messages),
            Text = result.Text,
            OcrConfidence = result.OcrConfidence,
            OcrEngine = result.OcrEngine,
            DurationMs = duration,
        };
        db.InvoiceExtraction.Add(extraction);

        // The XML inside a hybrid PDF is kept as a file of its own, next to the original PDF (both unchanged).
        if (result.EmbeddedXml is { } xml && doc.EmbeddedXmlFileId is null)
        {
            doc.EmbeddedXmlFile = await files.SaveAsync(xml, Path.GetFileNameWithoutExtension(doc.FileName) + "-" + (result.Format.EmbeddedFileName ?? "eingebettet.xml"), "application/xml", ct);
        }

        var f = result.Format;
        doc.FileKind = f.Kind.ToString();
        doc.Syntax = f.Syntax == InvoiceSyntax.None ? null : f.Syntax.ToString();
        doc.Standard = f.Standard;
        doc.Profile = f.Profile;
        doc.FormatVersion = f.Version;
        doc.FormatCode = f.ShortCode;
        doc.FormatSummary = f.Summary;
        doc.Method = result.Method.ToString();
        await db.SaveChangesAsync(ct);
        doc.CurrentExtractionId = extraction.Id;
        await db.SaveChangesAsync(ct);
        return extraction;
    }

    /// <summary>The input of a mapping for a document: the corrected semantic model, the raw fields and what is known about the file.</summary>
    public static MappingInput BuildInput(InvoiceDocument doc, ExtractionResultParts parts, SemanticDocument corrected) => new()
    {
        Semantic = corrected,
        Raw = parts.Raw,
        Meta = new Dictionary<string, string?>
        {
            ["fileName"] = doc.FileName,
            ["kind"] = parts.Format.Kind.ToString(),
            ["format"] = parts.Format.Summary,
            ["syntax"] = parts.Format.IsStructured ? parts.Format.Syntax.ToString() : "None",
            ["standard"] = parts.Format.Standard,
            ["profile"] = parts.Format.Profile,
            ["version"] = parts.Format.Version,
            ["method"] = doc.Method,
            ["receivedDate"] = doc.ReceivedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["sha256"] = doc.Sha256,
            ["documentId"] = doc.Id.ToString(CultureInfo.InvariantCulture),
        }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!),
    };

    /// <summary>
    /// A new revision of the normalized data: the schema of the document (or the given one, or the default) in its active version, the mapping that fits the
    /// format best, the extraction with the corrections. The columns of the list are taken from the corrected semantic model.
    /// </summary>
    public async Task<InvoiceRevision> NormalizeAsync(InvoiceDocument doc, InvoiceExtraction extraction, string reason, long? schemaId, CancellationToken ct = default)
    {
        var parts = InvoiceContext.Parse(extraction);
        var corrections = await db.InvoiceCorrection.AsNoTracking().Where(c => c.InvoiceId == doc.Id).ToListAsync(ct);
        var context = new InvoiceContext { Doc = doc, Corrections = corrections };
        var corrected = InvoiceContext.Apply(parts.Semantic, context.ActiveCorrections(CorrectionScope.Source));

        if (schemaId is null && doc.SchemaVersionId is { } current)
        {
            schemaId = await db.TargetSchemaVersion.Where(v => v.Id == current).Select(v => (long?)v.TargetSchemaId).FirstOrDefaultAsync(ct);
        }

        var schema = schemaId is { } sid ? await db.TargetSchema.FirstOrDefaultAsync(s => s.Id == sid, ct) ?? await definitions.DefaultSchemaAsync(ct) : await definitions.DefaultSchemaAsync(ct);
        var schemaVersion = await definitions.ActiveSchemaVersionAsync(schema.Id, ct) ?? throw new DefinitionException($"Das Zielschema „{schema.Name}“ hat keine aktive Version.");
        var selected = await definitions.SelectMappingAsync(schema.Id, parts.Format, ct)
                       ?? throw new DefinitionException($"Für das Zielschema „{schema.Name}“ passt kein aktives Mapping zum Format {parts.Format.Summary}. Lege ein Mapping für alle Formate an.");
        var overrides = context.ActiveCorrections(CorrectionScope.Target, schema.Id).ToDictionary(c => c.Key, c => c.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        var result = MappingEngine.Run(DefinitionService.Definition(schemaVersion), DefinitionService.Definition(selected.Version), BuildInput(doc, parts, corrected), overrides);
        var number = await db.InvoiceRevision.Where(r => r.InvoiceId == doc.Id).MaxAsync(r => (int?)r.Number, ct) ?? 0;
        var revision = new InvoiceRevision
        {
            InvoiceId = doc.Id,
            Number = number + 1,
            ExtractionId = extraction.Id,
            SchemaVersionId = schemaVersion.Id,
            MappingVersionId = selected.Version.Id,
            Reason = reason,
            DataJson = result.Data.ToJsonString(CoreJson.Options),
            ReportJson = CoreJson.Serialize(RevisionReport.Of(result.Report, schema, schemaVersion, selected.Set, selected.Version)),
            CorrectionCount = corrections.Count(c => !c.Reverted),
        };
        db.InvoiceRevision.Add(revision);
        await db.SaveChangesAsync(ct);

        doc.CurrentRevisionId = revision.Id;
        doc.SchemaVersionId = schemaVersion.Id;
        doc.MappingVersionId = selected.Version.Id;
        doc.InvoiceNumber = Cut(corrected.Get("BT-1"), 200);
        doc.InvoiceDate = corrected.Date("BT-2");
        doc.DueDate = corrected.Date("BT-9");
        doc.SellerName = Cut(corrected.Get("BT-27") ?? corrected.Get("BT-28"), 300);
        doc.BuyerName = Cut(corrected.Get("BT-44") ?? corrected.Get("BT-45"), 300);
        doc.Currency = Cut(corrected.Get("BT-5"), 10);
        doc.GrossTotal = corrected.Decimal("BT-112") ?? corrected.Decimal("BT-115");
        doc.TypeCode = Cut(corrected.Get("BT-3"), 10);
        await db.SaveChangesAsync(ct);
        return revision;
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;

    /// <summary>
    /// Validates a document: the format and the reading, the business rules of its standard on the corrected semantic model, the target schema (types,
    /// required fields) and the mapping (conflicts); and whether an XRechnung can be made.
    /// </summary>
    public async Task<InvoiceValidation> ValidateAsync(InvoiceDocument doc, InvoiceExtraction extraction, InvoiceRevision? revision, CancellationToken ct = default)
    {
        var parts = InvoiceContext.Parse(extraction);
        var corrections = await db.InvoiceCorrection.AsNoTracking().Where(c => c.InvoiceId == doc.Id).ToListAsync(ct);
        var corrected = InvoiceContext.Apply(parts.Semantic, new InvoiceContext { Doc = doc, Corrections = corrections }.ActiveCorrections(CorrectionScope.Source));
        var report = new ValidationReport();

        report.Checks.Add(new CheckRun(CheckScope.Format, "Dateityp, Syntax, Standard, Profil und Version erkennen", 1, true));
        foreach (var m in parts.Messages)
        {
            var scope = m.Code.StartsWith("FORMAT", StringComparison.Ordinal) || m.Code.StartsWith("XML", StringComparison.Ordinal) || m.Code.StartsWith("PDF", StringComparison.Ordinal) ? CheckScope.Format : CheckScope.Extraction;
            report.Findings.Add(new Finding(m.Code, m.Severity switch { MessageSeverity.Error => Severity.Error, MessageSeverity.Warning => Severity.Warning, _ => Severity.Info }, scope, m.Text));
        }

        var structured = parts.Format.IsStructured && doc.Method is nameof(ExtractionMethod.Xml) or nameof(ExtractionMethod.EmbeddedXml);
        var readable = doc.Method is not (null or nameof(ExtractionMethod.None));
        if (readable)
        {
            report.Checks.Add(new CheckRun(CheckScope.Extraction, structured ? "Strukturierte Daten auslesen" : $"Text auslesen ({MethodLabel(Enum.Parse<ExtractionMethod>(doc.Method!))})", 1, true));
            var (findings, checks) = SemanticValidator.Validate(corrected, new ValidationOptions(parts.Format.Rules, structured, Today, config.Current.Processing.Tolerance));
            report.Findings.AddRange(findings);
            report.Checks.AddRange(checks);
        }

        if (revision is not null)
        {
            var mapping = CoreJson.Deserialize<RevisionReport>(revision.ReportJson);
            foreach (var f in mapping.Fields)
            {
                if (f.Status == FieldStatus.Invalid)
                {
                    report.Findings.Add(new Finding("SCHEMA-TYPE", Severity.Error, CheckScope.Schema, $"{f.GroupLabel} · {f.FieldLabel}{ItemText(f)}: {f.Message}", f.Address, f.Value));
                }
                else if (f.Required && f.Value is null)
                {
                    report.Findings.Add(new Finding("SCHEMA-REQUIRED", Severity.Error, CheckScope.Schema, $"{f.GroupLabel} · {f.FieldLabel}{ItemText(f)} ist ein Pflichtfeld und hat keinen Wert. {f.Message}".Trim(), f.Address));
                }
                else if (f.Status == FieldStatus.Conflict)
                {
                    report.Findings.Add(new Finding("MAP-CONFLICT", Severity.Warning, CheckScope.Mapping, $"{f.GroupLabel} · {f.FieldLabel}{ItemText(f)}: {f.Message}", f.Address, f.Value));
                }
            }

            foreach (var p in mapping.DefinitionProblems)
            {
                report.Findings.Add(new Finding("MAP-DEFINITION", Severity.Warning, CheckScope.Mapping, p));
            }

            report.Checks.Add(new CheckRun(CheckScope.Schema, $"Zielschema „{mapping.SchemaName}“ v{mapping.SchemaVersion}: Datentypen, Pflichtfelder, erlaubte Werte", mapping.Fields.Count, true));
            report.Checks.Add(new CheckRun(CheckScope.Mapping, $"Mapping „{mapping.MappingName}“ v{mapping.MappingVersion}: Konflikte und Definition", mapping.Fields.Count, true));
        }

        var missing = readable ? SemanticValidator.XRechnungReadiness(corrected, Today, config.Current.Processing.Tolerance) : [];
        var validation = new InvoiceValidation
        {
            InvoiceId = doc.Id,
            RevisionId = revision?.Id,
            ExtractionId = extraction.Id,
            Status = report.Status,
            Errors = report.Errors,
            Warnings = report.Warnings,
            Infos = report.Infos,
            ReportJson = CoreJson.Serialize(report),
            XRechnungReady = readable && missing.Count == 0,
            XRechnungMissingJson = CoreJson.Serialize(missing),
        };
        db.InvoiceValidation.Add(validation);
        await db.SaveChangesAsync(ct);

        doc.CurrentValidationId = validation.Id;
        doc.ValidationStatus = validation.Status;
        doc.ErrorCount = validation.Errors;
        doc.WarningCount = validation.Warnings;
        doc.XRechnungReady = validation.XRechnungReady;
        doc.NeedsReview = validation.Errors > 0 || corrected.Uncertain().Any();
        await db.SaveChangesAsync(ct);
        return validation;
    }

    private static string ItemText(FieldOutcome f) => f.Item is { } i ? $" (Eintrag {i})" : "";

    private async Task MakeOutputsAsync(InvoiceDocument doc, CancellationToken ct)
    {
        var p = config.Current.Processing;
        if (p.AutoPdf && doc.FileKind != nameof(FileKind.Pdf))
        {
            await GeneratePdfAsync(doc, ct);
        }

        if (p.AutoXRechnung && doc.XRechnungReady)
        {
            await GenerateXRechnungAsync(doc, null, ct);
        }
    }

    // ------------------------------------------------------------------------------------------------------------ outputs

    /// <summary>
    /// Makes an XRechnung of the corrected semantic model, independently of the user's schema. It refuses when the model lacks what XRechnung requires;
    /// the file it makes is read back and validated at once, and the result is kept with it.
    /// </summary>
    public async Task<GeneratedFile> GenerateXRechnungAsync(InvoiceDocument doc, InvoiceSyntax? syntax, CancellationToken ct = default)
    {
        var context = await LoadAsync(doc.Id, ct) ?? throw new InvoiceException("Den Beleg gibt es nicht.");
        if (context.Extraction is null || context.Format is null)
        {
            throw new InvoiceException("Der Beleg wurde noch nicht ausgelesen.");
        }

        var missing = SemanticValidator.XRechnungReadiness(context.Corrected, Today, config.Current.Processing.Tolerance);
        if (missing.Count > 0)
        {
            throw new InvoiceException($"Für eine XRechnung fehlen Angaben oder sie widersprechen sich ({missing.Count}): {string.Join(" ", missing.Take(3).Select(m => m.Message))}");
        }

        var chosen = syntax ?? config.Current.Processing.XRechnungSyntax switch
        {
            XRechnungSyntaxChoice.Ubl => InvoiceSyntax.Ubl,
            XRechnungSyntaxChoice.Cii => InvoiceSyntax.Cii,
            _ => context.Format.Syntax == InvoiceSyntax.Cii ? InvoiceSyntax.Cii : InvoiceSyntax.Ubl,
        };
        var xml = XRechnungWriter.Write(context.Corrected, chosen);

        // The check after the writing: the file is read like any incoming invoice and validated against EN 16931 and XRechnung.
        var back = await new InvoiceReader(specs.Registry).ReadAsync(xml, "xrechnung.xml", ct);
        var (findings, _) = SemanticValidator.Validate(back.Semantic, new ValidationOptions(RuleSets.En16931 | RuleSets.XRechnung, true, Today, config.Current.Processing.Tolerance));
        var errors = findings.Count(f => f.Severity == Severity.Error) + back.Messages.Count(m => m.Severity == MessageSeverity.Error);
        var warnings = findings.Count(f => f.Severity == Severity.Warning);
        if (errors > 0)
        {
            throw new InvoiceException($"Die erzeugte XRechnung besteht die eigene Prüfung nicht ({errors} Fehler): {findings.FirstOrDefault(f => f.Severity == Severity.Error)?.Message}");
        }

        var name = $"{SafeStem(doc)}.xrechnung-{(chosen == InvoiceSyntax.Cii ? "cii" : "ubl")}.xml";
        var file = await AddFileAsync(doc, chosen == InvoiceSyntax.Cii ? GeneratedKind.XRechnungCii : GeneratedKind.XRechnungUbl, xml, name, "application/xml", context,
            $"XRechnung 3.0 ({(chosen == InvoiceSyntax.Cii ? "CII" : "UBL")}), gegengeprüft: 0 Fehler, {warnings} Warnungen", ct);
        await EventAsync(doc, "XRechnung erzeugt", $"{name} ({(chosen == InvoiceSyntax.Cii ? "CII" : "UBL")}).", ct);
        return file;
    }

    /// <summary>A readable PDF of the document (for XML and images, which have no PDF of their own).</summary>
    public async Task<GeneratedFile> GeneratePdfAsync(InvoiceDocument doc, CancellationToken ct = default)
    {
        var context = await LoadAsync(doc.Id, ct) ?? throw new InvoiceException("Den Beleg gibt es nicht.");
        if (context.Extraction is null)
        {
            throw new InvoiceException("Der Beleg wurde noch nicht ausgelesen.");
        }

        var bytes = pdf.Invoice(context);
        var name = $"{SafeStem(doc)}.ansicht.pdf";
        var file = await AddFileAsync(doc, GeneratedKind.Pdf, bytes, name, "application/pdf", context, "Menschenlesbare Darstellung der ausgelesenen Daten", ct);
        await EventAsync(doc, "PDF erzeugt", name, ct);
        return file;
    }

    /// <summary>The validation report as a PDF (the page has it as HTML and JSON as well).</summary>
    public async Task<byte[]> ValidationReportPdfAsync(InvoiceDocument doc, CancellationToken ct = default)
    {
        var context = await LoadAsync(doc.Id, ct) ?? throw new InvoiceException("Den Beleg gibt es nicht.");
        return pdf.ValidationReport(context);
    }

    private async Task<GeneratedFile> AddFileAsync(InvoiceDocument doc, GeneratedKind kind, byte[] bytes, string name, string contentType, InvoiceContext context, string check, CancellationToken ct)
    {
        var stored = await files.SaveAsync(bytes, name, contentType, ct);
        var generated = new GeneratedFile
        {
            InvoiceId = doc.Id,
            Kind = kind,
            StoredFile = stored,
            ExtractionId = context.Extraction?.Id,
            RevisionId = context.Revision?.Id,
            ValidationId = context.Validation?.Id,
            Check = check,
        };
        db.GeneratedFile.Add(generated);
        await db.SaveChangesAsync(ct);
        return generated;
    }

    public static string SafeStem(InvoiceDocument doc)
        => FileStoreService.SafeFileName((doc.InvoiceNumber ?? Path.GetFileNameWithoutExtension(doc.FileName)).Replace(' ', '_').Replace('/', '-'));

    // ------------------------------------------------------------------------------------------------------------ corrections and review

    /// <summary>
    /// Writes corrections and makes the data again: a new revision, a new validation and, when it is ready now, a new XRechnung. The extraction is not
    /// changed; the correction is laid over it.
    /// </summary>
    public async Task CorrectAsync(InvoiceDocument doc, CorrectionScope scope, IReadOnlyDictionary<string, string?> changes, string? reason, long? schemaId, CancellationToken ct = default)
    {
        var context = await LoadAsync(doc.Id, ct) ?? throw new InvoiceException("Den Beleg gibt es nicht.");
        if (context.Extraction is null)
        {
            throw new InvoiceException("Der Beleg wurde noch nicht ausgelesen.");
        }

        var count = 0;
        foreach (var (key, value) in changes)
        {
            var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            var before = scope == CorrectionScope.Source ? context.Corrected.Lookup(key)?.Value : FieldText(context.Data, key);
            if (string.Equals(before ?? "", clean ?? "", StringComparison.Ordinal))
            {
                continue;
            }

            db.InvoiceCorrection.Add(new InvoiceCorrection
            {
                InvoiceId = doc.Id,
                Scope = scope,
                TargetSchemaId = scope == CorrectionScope.Target ? schemaId : null,
                Key = key,
                Value = clean,
                PreviousValue = before,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                UserName = user.Name,
            });
            count++;
        }

        if (count == 0)
        {
            return;
        }

        await db.SaveChangesAsync(ct);
        await EventAsync(doc, "Korrektur", $"{count} Wert(e) korrigiert{(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason.Trim()}")}.", ct);
        await RefreshAsync(doc, context.Extraction, "Korrektur", null, ct);
    }

    /// <summary>Takes a correction back: the value of the extraction counts again.</summary>
    public async Task RevertCorrectionAsync(InvoiceDocument doc, long correctionId, CancellationToken ct = default)
    {
        var correction = await db.InvoiceCorrection.FirstOrDefaultAsync(c => c.Id == correctionId && c.InvoiceId == doc.Id, ct) ?? throw new InvoiceException("Die Korrektur gibt es nicht.");
        db.InvoiceCorrection.Add(new InvoiceCorrection
        {
            InvoiceId = doc.Id, Scope = correction.Scope, TargetSchemaId = correction.TargetSchemaId, Key = correction.Key, Value = null,
            PreviousValue = correction.Value, Reverted = true, Reason = "Korrektur zurückgenommen", UserName = user.Name,
        });
        await db.SaveChangesAsync(ct);
        await EventAsync(doc, "Korrektur zurückgenommen", $"{correction.Key}: „{correction.Value}“ gilt nicht mehr.", ct);
        var extraction = await db.InvoiceExtraction.FirstAsync(e => e.Id == doc.CurrentExtractionId, ct);
        await RefreshAsync(doc, extraction, "Korrektur zurückgenommen", null, ct);
    }

    /// <summary>Normalizes the document again with the active versions (after a change of the schema or the mapping, or into another schema).</summary>
    public async Task RenormalizeAsync(InvoiceDocument doc, long? schemaId, CancellationToken ct = default)
    {
        var extraction = await db.InvoiceExtraction.FirstOrDefaultAsync(e => e.Id == doc.CurrentExtractionId, ct) ?? throw new InvoiceException("Der Beleg wurde noch nicht ausgelesen.");
        await RefreshAsync(doc, extraction, "Neu normalisiert", schemaId, ct);
        await EventAsync(doc, "Neu normalisiert", "Die Daten wurden mit den aktiven Versionen von Schema und Mapping neu erzeugt.", ct);
    }

    private async Task RefreshAsync(InvoiceDocument doc, InvoiceExtraction extraction, string reason, long? schemaId, CancellationToken ct)
    {
        var revision = await NormalizeAsync(doc, extraction, reason, schemaId, ct);
        await ValidateAsync(doc, extraction, revision, ct);
        if (config.Current.Processing.AutoXRechnung && doc.XRechnungReady)
        {
            try
            {
                await GenerateXRechnungAsync(doc, null, ct);
            }
            catch (InvoiceException ex)
            {
                await EventAsync(doc, "XRechnung nicht erzeugt", ex.Message, ct);
            }
        }
    }

    public async Task MarkReviewedAsync(InvoiceDocument doc, CancellationToken ct = default)
    {
        doc.ReviewedDate = clock.GetUtcNow().UtcDateTime;
        doc.ReviewedUserId = user.UserId;
        doc.NeedsReview = doc.ErrorCount > 0;
        await db.SaveChangesAsync(ct);
        await EventAsync(doc, "Geprüft", "Die Daten wurden von einer Person geprüft und bestätigt.", ct);
    }

    /// <summary>
    /// Takes the document out of the lists. Its rows and files stay (soft delete): the original is never destroyed by the application.
    /// </summary>
    public async Task DeleteAsync(InvoiceDocument doc, CancellationToken ct = default)
    {
        db.Invoice.Remove(doc);
        await db.SaveChangesAsync(ct);
        await EventAsync(doc, "Gelöscht", "Der Beleg wurde aus den Listen entfernt; Original und Verlauf bleiben gespeichert.", ct);
    }

    // ------------------------------------------------------------------------------------------------------------ reading

    public async Task<InvoiceContext?> LoadAsync(long id, CancellationToken ct = default)
    {
        var doc = await db.Invoice.Include(i => i.OriginalFile).Include(i => i.EmbeddedXmlFile).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (doc is null)
        {
            return null;
        }

        var extraction = doc.CurrentExtractionId is { } e ? await db.InvoiceExtraction.AsNoTracking().FirstOrDefaultAsync(x => x.Id == e, ct) : null;
        var revision = doc.CurrentRevisionId is { } r ? await db.InvoiceRevision.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r, ct) : null;
        var validation = doc.CurrentValidationId is { } v ? await db.InvoiceValidation.AsNoTracking().FirstOrDefaultAsync(x => x.Id == v, ct) : null;
        var corrections = await db.InvoiceCorrection.AsNoTracking().Where(c => c.InvoiceId == id).OrderBy(c => c.Id).ToListAsync(ct);
        var generated = await db.GeneratedFile.AsNoTracking().Include(f => f.StoredFile).Where(f => f.InvoiceId == id).OrderByDescending(f => f.Id).ToListAsync(ct);
        var schemaVersion = revision is null ? null : await db.TargetSchemaVersion.AsNoTracking().FirstOrDefaultAsync(x => x.Id == revision.SchemaVersionId, ct);

        var parts = extraction is null ? null : InvoiceContext.Parse(extraction);
        var shell = new InvoiceContext { Doc = doc, Corrections = corrections };
        return new InvoiceContext
        {
            Doc = doc,
            Extraction = extraction,
            Format = parts?.Format,
            Raw = parts?.Raw ?? [],
            Extracted = parts?.Semantic ?? new SemanticDocument(),
            Corrected = parts is null ? new SemanticDocument() : InvoiceContext.Apply(parts.Semantic, shell.ActiveCorrections(CorrectionScope.Source)),
            Messages = parts?.Messages ?? [],
            Corrections = corrections,
            Revision = revision,
            Report = revision is null ? null : CoreJson.Deserialize<RevisionReport>(revision.ReportJson),
            Data = revision is null ? null : JsonNode.Parse(revision.DataJson) as JsonObject,
            Schema = schemaVersion is null ? null : DefinitionService.Definition(schemaVersion),
            Validation = validation,
            ValidationReport = validation is null ? null : CoreJson.Deserialize<ValidationReport>(validation.ReportJson),
            XRechnungMissing = validation is null ? [] : CoreJson.Deserialize<List<Finding>>(validation.XRechnungMissingJson),
            Files = generated,
        };
    }

    /// <summary>
    /// The normalized data of a document in a schema and mapping that are not (yet) its own, without storing anything: for the preview of a draft and for
    /// exports into another schema.
    /// </summary>
    public static NormalizationResult NormalizeInMemory(InvoiceContext ctx, Core.Schemas.SchemaDefinition schema, MappingDefinition mapping, long schemaId)
    {
        var parts = new ExtractionResultParts(ctx.Format ?? new DetectedFormat(), ctx.Raw, ctx.Extracted, ctx.Messages);
        var overrides = ctx.ActiveCorrections(CorrectionScope.Target, schemaId).ToDictionary(c => c.Key, c => c.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        return MappingEngine.Run(schema, mapping, BuildInput(ctx.Doc, parts, ctx.Corrected), overrides);
    }

    /// <summary>The value of a field of the normalized data by its address ("invoice.number", "lines[2].quantity").</summary>
    public static string? FieldText(JsonObject? data, string address)
    {
        if (data is null)
        {
            return null;
        }

        var dot = address.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
        {
            return null;
        }

        var group = address[..dot];
        var field = address[(dot + 1)..];
        var bracket = group.IndexOf('[', StringComparison.Ordinal);
        if (bracket > 0 && int.TryParse(group[(bracket + 1)..^1], out var index))
        {
            var array = data[group[..bracket]] as JsonArray;
            return array is not null && index >= 1 && index <= array.Count ? Core.Schemas.FieldValues.AsText(array[index - 1]?[field]) : null;
        }

        return Core.Schemas.FieldValues.AsText((data[group] as JsonObject)?[field]);
    }

    public async Task EventAsync(InvoiceDocument doc, string kind, string message, CancellationToken ct = default)
    {
        db.InvoiceEvent.Add(new InvoiceEvent { InvoiceId = doc.Id, OccurredDate = clock.GetUtcNow().UtcDateTime, Kind = kind, Message = message, UserName = user.Name ?? "System" });
        await db.SaveChangesAsync(ct);
    }

    public static string MethodLabel(ExtractionMethod m) => m switch
    {
        ExtractionMethod.Xml => "XML",
        ExtractionMethod.EmbeddedXml => "eingebettetes XML",
        ExtractionMethod.PdfText => "PDF-Text",
        ExtractionMethod.Ocr => "Texterkennung (OCR)",
        _ => "–",
    };

    public static string ValidationLabel(ValidationStatus s) => s switch
    {
        ValidationStatus.Valid => "gültig",
        ValidationStatus.Warnings => "gültig mit Warnungen",
        ValidationStatus.Invalid => "ungültig",
        _ => "nicht geprüft",
    };
}
