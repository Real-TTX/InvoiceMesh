namespace InvoiceMesh.Data.Entities;

/// <summary>When something runs by itself: every few hours, or daily, weekly or monthly at a time of the installation's clock.</summary>
public interface ISchedulePlan
{
    ScheduleKind Schedule { get; }

    int IntervalHours { get; }

    TimeOnly TimeOfDay { get; }

    DayOfWeek DayOfWeek { get; }

    int DayOfMonth { get; }
}

public enum IntakeKind
{
    Folder,
    Smb,
    Mailbox,
}

public enum MailSecurity
{
    /// <summary>TLS from the start (IMAPS, port 993).</summary>
    Tls,

    /// <summary>Plain connection upgraded with STARTTLS (port 143).</summary>
    StartTls,

    /// <summary>No encryption: only inside a trusted network.</summary>
    None,
}

/// <summary>
/// A way documents come in by themselves: a folder (in the container or mounted), a folder on a share of a NAS, or a mailbox. It is looked at every few
/// minutes; what it finds is taken in like an upload. Files are moved to <see cref="DoneFolder"/> afterwards (or deleted when it is empty), mails are marked
/// as read and moved to <see cref="DoneFolder"/> when one is set.
/// </summary>
public class IntakeSource : AuditedEntity
{
    public string Name { get; set; } = "";

    public IntakeKind Kind { get; set; } = IntakeKind.Folder;

    public bool IsEnabled { get; set; } = true;

    /// <summary>How often it is looked at.</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>Folder: the folder (absolute, or relative to the data volume). SMB: the folder inside the share.</summary>
    public string Path { get; set; } = "";

    /// <summary>Folder and SMB: where the files go after they were taken in (a folder below <see cref="Path"/>); empty deletes them. Mailbox: the IMAP folder mails are moved to; empty leaves them marked as read.</summary>
    public string? DoneFolder { get; set; } = "verarbeitet";

    /// <summary>SMB: the NAS. Mailbox: the IMAP server.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 993;

    public string? Share { get; set; }

    public string? Domain { get; set; }

    public string? UserName { get; set; }

    /// <summary>Protected with the key ring of the application.</summary>
    public string? PasswordProtected { get; set; }

    public MailSecurity Security { get; set; } = MailSecurity.Tls;

    /// <summary>Mailbox: the folder that is read.</summary>
    public string MailFolder { get; set; } = "INBOX";

    public DateTime? NextRunDate { get; set; }

    public DateTime? LastRunDate { get; set; }

    public bool LastRunOk { get; set; } = true;

    public string? LastStatus { get; set; }

    public int LastCount { get; set; }

    public int FailedAttempts { get; set; }
}

/// <summary>Which documents a planned export takes.</summary>
public enum ExportSelection
{
    /// <summary>Those processed since the last run (the first run takes the last <see cref="ExportPlan.Days"/> days).</summary>
    New,

    /// <summary>All processed in the last <see cref="ExportPlan.Days"/> days, every time.</summary>
    LastDays,
}

public enum DeliveryKind
{
    Folder,
    Smb,
    Webhook,
}

/// <summary>An export that runs by itself and delivers its file: into a folder, onto a share of a NAS, or to an address over HTTP (a webhook).</summary>
public class ExportPlan : AuditedEntity, ISchedulePlan
{
    public string Name { get; set; } = "";

    public long ExportProfileId { get; set; }

    public ExportProfile? ExportProfile { get; set; }

    public bool IsEnabled { get; set; } = true;

    public ExportSelection Selection { get; set; } = ExportSelection.New;

    public int Days { get; set; } = 7;

    /// <summary>Only documents that are not invalid.</summary>
    public bool OnlyValid { get; set; } = true;

    /// <summary>No file when there is nothing to export.</summary>
    public bool SkipEmpty { get; set; } = true;

    public ScheduleKind Schedule { get; set; } = ScheduleKind.Daily;

    public int IntervalHours { get; set; } = 6;

    public TimeOnly TimeOfDay { get; set; } = new(6, 0);

    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;

    public int DayOfMonth { get; set; } = 1;

    public DeliveryKind Delivery { get; set; } = DeliveryKind.Folder;

    /// <summary>Folder: the folder (absolute, or relative to the data volume). SMB: the folder inside the share.</summary>
    public string Path { get; set; } = "";

    public string? Host { get; set; }

    public string? Share { get; set; }

    public string? Domain { get; set; }

    public string? UserName { get; set; }

    public string? PasswordProtected { get; set; }

    /// <summary>Webhook: the address the file is posted to.</summary>
    public string? Url { get; set; }

    /// <summary>Webhook: an optional header that proves the sender (for example Authorization), its value protected.</summary>
    public string? HeaderName { get; set; }

    public string? HeaderValueProtected { get; set; }

    /// <summary>Documents processed up to this moment were delivered (selection <see cref="ExportSelection.New"/>).</summary>
    public DateTime? CoveredUntil { get; set; }

    public DateTime? NextRunDate { get; set; }

    public DateTime? LastRunDate { get; set; }

    public bool LastRunOk { get; set; } = true;

    public string? LastStatus { get; set; }

    public int LastCount { get; set; }

    public int FailedAttempts { get; set; }
}

/// <summary>
/// A key for the API. The key itself is shown once when it is made; only its SHA-256 and its first characters are kept. The token is the identity of the
/// record (in addresses of the pages), never the key.
/// </summary>
public class ApiKey : AuditedEntity
{
    public string Name { get; set; } = "";

    public Guid Token { get; set; } = Guid.NewGuid();

    public string KeyHash { get; set; } = "";

    /// <summary>The first characters of the key, so that a person recognises it ("im_3fA9x…").</summary>
    public string KeyPrefix { get; set; } = "";

    public bool CanImport { get; set; } = true;

    public bool CanRead { get; set; } = true;

    public bool IsEnabled { get; set; } = true;

    public DateTime? ExpiresDate { get; set; }

    public DateTime? LastUsedDate { get; set; }
}
