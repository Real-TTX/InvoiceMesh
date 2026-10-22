using System.Net;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace InvoiceMesh.UiKit;

/// <summary>An entry of the user's context menu. A <see cref="Post"/> item changes something (sign out): a button of a small form, not a link.</summary>
public sealed record UserMenuItem(string Title, string Href, string Icon, bool Danger = false, bool SeparatorBefore = false, bool Post = false);

/// <summary>Everything the layout needs to draw the shell around a page: the menus, the user, the version.</summary>
public sealed class ShellModel
{
    public IReadOnlyList<NavSet> Menus { get; init; } = [];

    public bool HasSwitcher => Menus.Count > 1;

    public NavSet CurrentMenu(string? active) => Menus.FirstOrDefault(m => m.Contains(active)) ?? Menus[0];

    public string UserName { get; init; } = "";

    public string UserRole { get; init; } = "";

    public string UserLine { get; init; } = "";

    public IReadOnlyList<UserMenuItem> UserMenu { get; init; } = [];

    public string Version { get; init; } = "";

    public string Tagline { get; init; } = "Rechnungen normalisieren";

    /// <summary>"light" or "dark" when the user chose one; null follows the system.</summary>
    public string? Theme { get; init; }

    public string HomeHref { get; init; } = "/";

    /// <summary>The optional attributes of &lt;html&gt;, written only when they have a value (an empty data-theme would count as a choice).</summary>
    public string RootAttributes => Theme is "light" or "dark" ? $"data-theme=\"{WebUtility.HtmlEncode(Theme)}\"" : string.Empty;
}

/// <summary>Counts shown as badges in the menu. Cached for half a minute: a badge may be a little late, a page must not wait for queries.</summary>
public sealed class NavCounts(IMemoryCache cache, ImDbContext db)
{
    public sealed record Counts(int Review, int Failed, int Processing, int SchemaDrafts, int MappingDrafts);

    public async Task<Counts> GetAsync()
        => (await cache.GetOrCreateAsync("nav-counts", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return new Counts(
                await db.Invoice.CountAsync(i => i.NeedsReview && i.ReviewedDate == null && i.ProcessingStatus == ProcessingStatus.Processed),
                await db.Invoice.CountAsync(i => i.ProcessingStatus == ProcessingStatus.Failed),
                await db.Invoice.CountAsync(i => i.ProcessingStatus == ProcessingStatus.Queued || i.ProcessingStatus == ProcessingStatus.Processing),
                await db.TargetSchemaVersion.CountAsync(v => v.State == VersionState.Draft && v.TargetSchema!.UpdateState != UpdateState.Deleted),
                await db.MappingSetVersion.CountAsync(v => v.State == VersionState.Draft && v.MappingSet!.UpdateState != UpdateState.Deleted));
        }))!;

    public void Invalidate() => cache.Remove("nav-counts");
}

/// <summary>Builds the shell for the person who is signed in: the settings menu only for administrators.</summary>
public sealed class ShellFactory(ICurrentUser user, NavCounts counts, IHttpContextAccessor accessor)
{
    public async Task<ShellModel> BuildAsync()
    {
        var principal = accessor.HttpContext?.User;
        var theme = principal?.FindFirst(AppClaims.Theme)?.Value;

        var menus = new List<NavSet> { await WorkMenuAsync() };
        if (user.IsAdmin)
        {
            menus.Add(AdminMenu());
        }

        return new ShellModel
        {
            Menus = menus,
            UserName = user.Name ?? "Gast",
            UserRole = user.IsAdmin ? "Administrator" : "Benutzer",
            UserLine = principal?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "",
            UserMenu = UserMenu(),
            Version = $"InvoiceMesh {AppVersion.Current}",
            Theme = theme,
        };
    }

    /// <summary>The shell of a page without a menu (sign-in, setup, a shared link).</summary>
    public static ShellModel Bare(string? theme = null) => new()
    {
        Menus = [new NavSet("none", "InvoiceMesh", "home", [])],
        Version = $"InvoiceMesh {AppVersion.Current}",
        Theme = theme,
    };

    private async Task<NavSet> WorkMenuAsync()
    {
        var c = await counts.GetAsync();
        var documents = c.Failed > 0 ? NavBadge.Count(c.Failed, "error", "Belege mit Verarbeitungsfehler")
            : c.Review > 0 ? NavBadge.Count(c.Review, "warn", "Belege zu prüfen")
            : c.Processing > 0 ? NavBadge.Count(c.Processing, "info", "Belege in Verarbeitung") : null;
        return new NavSet("work", "InvoiceMesh", "im-logo",
        [
            new(null,
            [
                new("Hochladen", "/Invoices/Upload", "upload", Primary: true),
                new("Übersicht", "/", "dashboard"),
            ]),
            new("Belege",
            [
                new("Alle Belege", "/Invoices", "file-text", documents),
                new("Exporte", "/Exports", "download"),
            ]),
            new("Datenmodell",
            [
                new("Zielschemas", "/Schemas", "layers", c.SchemaDrafts > 0 ? NavBadge.Dot("info", "Schema-Entwürfe") : null),
                new("Mappings", "/Mappings", "shuffle", c.MappingDrafts > 0 ? NavBadge.Dot("info", "Mapping-Entwürfe") : null),
                new("Formate", "/Formats", "file-code"),
            ]),
        ]);
    }

    private static NavSet AdminMenu() => new("admin", "Einstellungen", "settings",
    [
        new("Organisation",
        [
            new("Benutzer", "/Admin/Users", "user"),
            new("Freigaben", "/Admin/Shares", "share"),
            new("Verarbeitung", "/Admin/Processing", "sliders"),
            new("Formate", "/Admin/Formats", "file-code"),
        ]),
        new("Automatisierung",
        [
            new("Eingänge", "/Admin/Intake", "folder-open"),
            new("Geplante Exporte", "/Admin/ExportPlans", "route"),
            new("API", "/Admin/ApiKeys", "key"),
        ]),
        new("System",
        [
            new("Speicher (NAS)", "/Admin/Storage", "hard-drive"),
            new("Backup", null, "archive", Children:
            [
                new("Übersicht", "/Admin/Backup", null),
                new("Pläne", "/Admin/Backup/Plans", null),
                new("Ziele", "/Admin/Backup/Targets", null),
                new("Wiederherstellung", "/Admin/Backup/Restore", null),
            ]),
            new("Sicherheit", "/Admin/Security", "shield"),
        ]),
        new("Protokolle",
        [
            new("Aktivität", "/Admin/Activity", "activity"),
            new("System", "/Admin/System", "server"),
        ]),
    ]);

    private static List<UserMenuItem> UserMenu() =>
    [
        new("Mein Konto", "/Account/Profile", "user"),
        new("Sicherheit", "/Account/Security", "shield"),
        new("Abmelden", "/Account/Logout", "logout", Danger: true, SeparatorBefore: true, Post: true),
    ];
}
