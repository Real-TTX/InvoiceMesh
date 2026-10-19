namespace InvoiceMesh.Core.Validation;

public enum Severity
{
    Info,
    Warning,
    Error,
}

/// <summary>Where a check comes from: the file, its structure against the XML schema, the reading, a standard, the user's schema, the mapping, or plain common sense.</summary>
public enum CheckScope
{
    Format,
    Structure,
    Extraction,
    Content,
    En16931,
    XRechnung,
    Peppol,
    Schema,
    Mapping,
    Plausibility,
}

/// <summary>The result of the validation of an invoice, separate from the result of its processing.</summary>
public enum ValidationStatus
{
    NotValidated,
    Valid,
    Warnings,
    Invalid,
}

/// <summary>One finding: which rule, how bad, what it says, where (a business term, a target field) and the value it saw.</summary>
public sealed record Finding(string RuleId, Severity Severity, CheckScope Scope, string Message, string? Location = null, string? Value = null);

/// <summary>A group of checks that ran (or did not, and why): the "performed checks" of the report.</summary>
public sealed record CheckRun(CheckScope Scope, string Title, int RuleCount, bool Performed, string? Reason = null);

/// <summary>Everything the validation found, with the checks it ran.</summary>
public sealed class ValidationReport
{
    public List<Finding> Findings { get; init; } = [];

    public List<CheckRun> Checks { get; init; } = [];

    public int Errors => Findings.Count(f => f.Severity == Severity.Error);

    public int Warnings => Findings.Count(f => f.Severity == Severity.Warning);

    public int Infos => Findings.Count(f => f.Severity == Severity.Info);

    public ValidationStatus Status => Errors > 0 ? ValidationStatus.Invalid : Warnings > 0 ? ValidationStatus.Warnings : ValidationStatus.Valid;

    public static string ScopeLabel(CheckScope scope) => scope switch
    {
        CheckScope.Format => "Format",
        CheckScope.Structure => "Struktur (XSD)",
        CheckScope.Extraction => "Auslesen",
        CheckScope.Content => "Rechnungsinhalt",
        CheckScope.En16931 => "EN 16931",
        CheckScope.XRechnung => "XRechnung",
        CheckScope.Peppol => "Peppol BIS",
        CheckScope.Schema => "Zielschema",
        CheckScope.Mapping => "Mapping",
        _ => "Plausibilität",
    };
}
