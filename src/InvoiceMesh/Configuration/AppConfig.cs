using System.Text.Json.Serialization;
using InvoiceMesh.Core.Formats;

namespace InvoiceMesh.Configuration;

/// <summary>
/// The configuration (JSON, <c>{data}/config/app.json</c>): how this installation is deployed and what the administrator set up.
/// The sections <see cref="Database"/> and <see cref="Server"/> can be overridden by environment variables
/// (<c>INVOICEMESH__Database__Host</c>); the rest is edited on the administration pages. Schemas and mappings are no configuration in this sense: they are
/// versioned data that documents refer to, and live in the database.
/// </summary>
public sealed class AppConfig
{
    public DatabaseConfig Database { get; set; } = new();

    public ServerConfig Server { get; set; } = new();

    public ProcessingConfig Processing { get; set; } = new();

    public FormatsConfig Formats { get; set; } = new();

    public StorageConfig Storage { get; set; } = new();

    public SecurityConfig Security { get; set; } = new();

    public BackupConfig Backup { get; set; } = new();
}

public enum XRechnungSyntaxChoice
{
    /// <summary>The syntax of the source (UBL stays UBL, CII stays CII); UBL for a PDF or a scan.</summary>
    Auto,
    Ubl,
    Cii,
}

/// <summary>How documents are processed.</summary>
public sealed class ProcessingConfig
{
    /// <summary>The languages of the text recognition (Tesseract codes joined by +).</summary>
    public string OcrLanguages { get; set; } = "deu+eng+fra+spa+ita+nld";

    /// <summary>Make an XRechnung by itself when a document has everything it needs.</summary>
    public bool AutoXRechnung { get; set; } = true;

    public XRechnungSyntaxChoice XRechnungSyntax { get; set; } = XRechnungSyntaxChoice.Auto;

    /// <summary>Make a readable PDF by itself when there is no PDF original (XML, images).</summary>
    public bool AutoPdf { get; set; } = true;

    /// <summary>How far a computed sum may differ from the stated one, in the currency (rounding).</summary>
    public decimal Tolerance { get; set; } = 0.01m;

    /// <summary>The largest file that is accepted, in megabytes.</summary>
    public int MaxUploadMb { get; set; } = 25;

    /// <summary>A document that is the same file as one already there (by its SHA-256) is still taken, with a warning.</summary>
    public bool WarnDuplicates { get; set; } = true;
}

/// <summary>A specification identifier the administrator added: a national format, a CIUS, a new version.</summary>
public sealed class CustomSpecification
{
    public string Identifier { get; set; } = "";

    public bool IsPrefix { get; set; }

    public string Standard { get; set; } = "";

    public string Profile { get; set; } = "";

    public string Version { get; set; } = "";

    public bool En16931 { get; set; } = true;

    public bool XRechnung { get; set; }

    public bool Peppol { get; set; }

    public string? Note { get; set; }

    public Specification ToSpecification() => new(Identifier.Trim(), IsPrefix, Standard.Trim(), Profile.Trim(), Version.Trim(),
        (En16931 ? RuleSets.En16931 : RuleSets.None) | (XRechnung ? RuleSets.XRechnung : RuleSets.None) | (Peppol ? RuleSets.Peppol : RuleSets.None), [], Note, IsBuiltIn: false);
}

public sealed class FormatsConfig
{
    public List<CustomSpecification> Custom { get; set; } = [];
}

public sealed class DatabaseConfig
{
    public string Host { get; set; } = "db";

    public int Port { get; set; } = 5432;

    public string Database { get; set; } = "invoicemesh";

    public string Username { get; set; } = "postgres";

    public string Password { get; set; } = "invoicemesh";
}

public sealed class ServerConfig
{
    /// <summary>The address people use, for the links that are handed on ("https://rechnungen.example.de"). Empty: taken from the request.</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>"Europe/Berlin": the time zone of the installation, for dates and schedules.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>The cookie is marked secure when the site is reached over HTTPS (also behind a reverse proxy that says so).</summary>
    public bool ForceSecureCookie { get; set; }
}

public enum StorageMode
{
    /// <summary>The files are kept in the data volume.</summary>
    Local,

    /// <summary>The files are kept on a share of a NAS (SMB).</summary>
    Smb,
}

public sealed class StorageConfig
{
    public StorageMode Mode { get; set; } = StorageMode.Local;

    public string Host { get; set; } = "";

    public int Port { get; set; } = 445;

    public string Share { get; set; } = "";

    /// <summary>A folder inside the share.</summary>
    public string Path { get; set; } = "InvoiceMesh";

    public string Domain { get; set; } = "";

    public string UserName { get; set; } = "";

    public string PasswordProtected { get; set; } = "";
}

public sealed class SecurityConfig
{
    public int SessionDays { get; set; } = 14;

    /// <summary>Administrators have to set up an authenticator app.</summary>
    public bool RequireTotpForAdmins { get; set; }

    /// <summary>Default life of a share link of a document, in days.</summary>
    public int ShareLinkDays { get; set; } = 30;

    [JsonIgnore]
    public int MaxFailedLogins => 5;
}

public sealed class BackupConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>The scratch folder of a backup; empty: {data}/tmp.</summary>
    public string TempDirectory { get; set; } = "";
}
