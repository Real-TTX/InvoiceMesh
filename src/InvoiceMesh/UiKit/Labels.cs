using InvoiceMesh.Core.Mapping;
using InvoiceMesh.Core.Semantics;
using InvoiceMesh.Core.Validation;
using InvoiceMesh.Data.Entities;
using Microsoft.AspNetCore.Html;

namespace InvoiceMesh.UiKit;

/// <summary>The German words for the enums of the data model, and the tone of a status pill (the colour is never alone: the pill carries the word and an icon).</summary>
public static class Labels
{
    public static string Of(InvoiceSource s) => s switch
    {
        InvoiceSource.Upload => "Hochgeladen",
        InvoiceSource.Demo => "Beispieldaten",
        InvoiceSource.Api => "Über die API",
        InvoiceSource.Folder => "Aus einem Ordner",
        _ => "Per E-Mail",
    };

    public static string Of(UserRole r) => r == UserRole.Admin ? "Administrator" : "Benutzer";

    public static string Of(ProcessingStatus s) => s switch
    {
        ProcessingStatus.Queued => "Wartet",
        ProcessingStatus.Processing => "In Verarbeitung",
        ProcessingStatus.Processed => "Verarbeitet",
        _ => "Fehlgeschlagen",
    };

    public static IHtmlContent Pill(ProcessingStatus s) => s switch
    {
        ProcessingStatus.Queued => Ui.Pill("Wartet", "busy", "clock"),
        ProcessingStatus.Processing => Ui.Pill("In Verarbeitung", "busy", "refresh"),
        ProcessingStatus.Processed => Ui.Pill("Verarbeitet", "ok", "check"),
        _ => Ui.Pill("Fehlgeschlagen", "error", "x-circle"),
    };

    public static string Of(ValidationStatus s) => s switch
    {
        ValidationStatus.Valid => "Gültig",
        ValidationStatus.Warnings => "Warnungen",
        ValidationStatus.Invalid => "Ungültig",
        _ => "Nicht geprüft",
    };

    public static IHtmlContent Pill(ValidationStatus s) => s switch
    {
        ValidationStatus.Valid => Ui.Pill("Gültig", "ok", "check-circle"),
        ValidationStatus.Warnings => Ui.Pill("Warnungen", "warn", "alert"),
        ValidationStatus.Invalid => Ui.Pill("Ungültig", "error", "x-circle"),
        _ => Ui.Pill("Nicht geprüft", "plain", "minus"),
    };

    /// <summary>The state of a document as one pill for lists: processing first, then validation, then whether a person should look.</summary>
    public static IHtmlContent Pill(InvoiceDocument d)
        => d.ProcessingStatus != ProcessingStatus.Processed ? Pill(d.ProcessingStatus)
            : d.NeedsReview && d.ReviewedDate is null && d.ValidationStatus != ValidationStatus.Invalid ? Ui.Pill("Prüfen", "warn", "eye")
            : Pill(d.ValidationStatus);

    public static string Of(Severity s) => s switch
    {
        Severity.Error => "Fehler",
        Severity.Warning => "Warnung",
        _ => "Hinweis",
    };

    public static IHtmlContent Pill(Severity s) => s switch
    {
        Severity.Error => Ui.Pill("Fehler", "error", "x-circle"),
        Severity.Warning => Ui.Pill("Warnung", "warn", "alert"),
        _ => Ui.Pill("Hinweis", "info", "info"),
    };

    public static string Of(FieldStatus s) => s switch
    {
        FieldStatus.Mapped => "Zugeordnet",
        FieldStatus.Default => "Standardwert",
        FieldStatus.Manual => "Manuell",
        FieldStatus.Conflict => "Konflikt",
        FieldStatus.Invalid => "Ungültig",
        FieldStatus.Missing => "Fehlt",
        _ => "Nicht zugeordnet",
    };

    public static IHtmlContent Pill(FieldStatus s) => s switch
    {
        FieldStatus.Mapped => Ui.Pill("Zugeordnet", "ok"),
        FieldStatus.Default => Ui.Pill("Standardwert", "info"),
        FieldStatus.Manual => Ui.Pill("Manuell", "accent"),
        FieldStatus.Conflict => Ui.Pill("Konflikt", "warn"),
        FieldStatus.Invalid => Ui.Pill("Ungültig", "error"),
        FieldStatus.Missing => Ui.Pill("Fehlt", "plain"),
        _ => Ui.Pill("Nicht zugeordnet", "plain"),
    };

    public static IHtmlContent Pill(VersionState s) => s switch
    {
        VersionState.Active => Ui.Pill("Aktiv", "ok", "check"),
        VersionState.Draft => Ui.Pill("Entwurf", "info", "edit"),
        _ => Ui.Pill("Abgelöst", "plain"),
    };

    public static string Of(Confidence c) => c switch
    {
        Confidence.High => "sicher",
        Confidence.Medium => "wahrscheinlich",
        _ => "unsicher",
    };

    public static string Of(GeneratedKind k) => k switch
    {
        GeneratedKind.XRechnungUbl => "XRechnung (UBL)",
        GeneratedKind.XRechnungCii => "XRechnung (CII)",
        GeneratedKind.Pdf => "PDF-Ansicht",
        GeneratedKind.ValidationReportPdf => "Validierungsbericht (PDF)",
        _ => "JSON",
    };

    public static string Of(BackupScope s) => s == BackupScope.SystemOnly ? "System (Datenbank und Einstellungen)" : "System und Dateien (mit allen Originalen)";

    public static string Of(BackupTargetKind k) => k == BackupTargetKind.Smb ? "NAS (SMB-Freigabe)" : "Ordner auf dem Server";

    public static string Of(ScheduleKind k) => k switch
    {
        ScheduleKind.Hourly => "Stündlich",
        ScheduleKind.Daily => "Täglich",
        ScheduleKind.Weekly => "Wöchentlich",
        _ => "Monatlich",
    };

    public static string Of(DayOfWeek d) => Services.Fmt.Culture.DateTimeFormat.GetDayName(d);

    public static string Syntax(string? syntax) => syntax switch
    {
        "Ubl" => "UBL",
        "Cii" => "CII",
        "CiiZugferdV1" => "CII (ZUGFeRD 1.0)",
        "OtherXml" => "anderes XML",
        "None" => "PDF-Text und Scans",
        null => "beliebig",
        _ => syntax,
    };

    public static string Method(string? method) => method switch
    {
        "Xml" => "XML",
        "EmbeddedXml" => "Eingebettetes XML",
        "PdfText" => "PDF-Text",
        "Ocr" => "Texterkennung (OCR)",
        _ => "–",
    };
}
