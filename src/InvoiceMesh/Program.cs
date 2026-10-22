using System.Globalization;
using System.Text;
using InvoiceMesh.Api;
using InvoiceMesh.Configuration;
using InvoiceMesh.Core;
using InvoiceMesh.Core.Extraction;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.Services.Auth;
using InvoiceMesh.Services.Automation;
using InvoiceMesh.Services.Backup;
using InvoiceMesh.Services.Processing;
using InvoiceMesh.Storage;
using InvoiceMesh.UiKit;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

// The image checks itself (HEALTHCHECK in the Dockerfile): it needs no curl.
if (args.Contains("--healthcheck"))
{
    return await HealthCheck.RunAsync();
}

// QuestPDF draws the readable views and the reports; its community licence covers companies below 1 million USD of annual revenue.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var dataDir = Environment.GetEnvironmentVariable("INVOICEMESH_DATA") is { Length: > 0 } fromEnv
    ? fromEnv
    : Path.Combine(Directory.GetCurrentDirectory(), "data");
Directory.CreateDirectory(dataDir);
var config = new ConfigStore(dataDir);
Fmt.Configure(config.Current.Server.TimeZone);

var builder = WebApplication.CreateBuilder(args);

// Port 2308; ASPNETCORE_URLS still wins (tests, a reverse proxy on the same machine).
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls($"http://+:{Environment.GetEnvironmentVariable("INVOICEMESH_PORT") ?? "2308"}");
}

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 210L * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 210L * 1024 * 1024);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

// Sessions live in the database; the keys that protect the cookie (and secrets) live in the data volume, so a restart keeps everybody signed in.
builder.Services.AddDataProtection()
    .SetApplicationName("InvoiceMesh")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

builder.Services.AddDbContextFactory<ImDbContext>(options => options
    // No automatic retries: a save that fails is shown to the person, not repeated behind their back.
    .UseNpgsql(config.ConnectionString())
    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ImDbContext>>().CreateDbContext());

builder.Services.AddSingleton<ICurrentUser, CurrentUser>();
builder.Services.AddSingleton<IconSprite>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<SessionCache>();
builder.Services.AddSingleton<TwoFactorTicket>();
builder.Services.AddSingleton<ActivityLogger>();
builder.Services.AddSingleton<SetupState>();
builder.Services.AddSingleton<ProcessingQueue>();
builder.Services.AddSingleton<TesseractOcr>();
builder.Services.AddSingleton<IOcrEngine>(sp => sp.GetRequiredService<TesseractOcr>());
builder.Services.AddSingleton<SpecificationProvider>();
builder.Services.AddSingleton<InvoicePdf>();
builder.Services.AddScoped<SessionCookieEvents>();
builder.Services.AddScoped<SignInService>();
builder.Services.AddScoped<TwoFactorService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<PublicUrl>();
builder.Services.AddScoped<FileStoreService>();
builder.Services.AddScoped<ShareService>();
builder.Services.AddScoped<DefinitionService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<BackupStoreFactory>();
builder.Services.AddScoped<DatabaseArchive>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddHostedService<BackupScheduler>();
builder.Services.AddScoped<FolderFactory>();
builder.Services.AddSingleton<MailboxReader>();
builder.Services.AddScoped<IntakeService>();
builder.Services.AddScoped<ExportPlanService>();
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddHostedService<AutomationScheduler>();
// Planned exports deliver to webhooks: a short timeout, no cookies, redirects not followed (the address is the one the administrator gave).
builder.Services.AddHttpClient(ExportPlanService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHostedService<ProcessingWorker>();
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddScoped<SystemService>();
builder.Services.AddScoped<DemoData>();
builder.Services.AddScoped<NavCounts>();
builder.Services.AddScoped<ShellFactory>();

builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

builder.Services.AddAuthentication(SignInService.CookieScheme)
    .AddCookie(SignInService.CookieScheme, options =>
    {
        options.Cookie.Name = ".InvoiceMesh";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = config.Current.Server.ForceSecureCookie ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.SlidingExpiration = false;
        options.EventsType = typeof(SessionCookieEvents);
    });

builder.Services.AddAuthorizationBuilder()
    // Everything needs a signed-in user (administrator or user) unless a page says otherwise: a new page is closed by default.
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(SignInService.CookieScheme)
        .RequireAuthenticatedUser().RequireRole(nameof(UserRole.Admin), nameof(UserRole.User)).Build())
    .AddPolicy("Admin", p => p.AddAuthenticationSchemes(SignInService.CookieScheme).RequireRole(nameof(UserRole.Admin)))
    .AddPolicy("SignedIn", p => p.AddAuthenticationSchemes(SignInService.CookieScheme).RequireAuthenticatedUser());

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin", "Admin");
    options.Conventions.AuthorizeFolder("/Account", "SignedIn");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/TwoFactor");
    options.Conventions.AllowAnonymousToPage("/Account/Setup");
    options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
    options.Conventions.AllowAnonymousToPage("/Offline");
    options.Conventions.AllowAnonymousToFolder("/Share");
})
// A non-nullable string is not "required" by itself: the pages say what is required (the star, the message, the check).
.AddMvcOptions(options => options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

// Numbers in forms and in the query string are always written the invariant way ("12.5"); the pages show them German with Fmt.
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture);
    options.SupportedCultures = [CultureInfo.InvariantCulture];
    options.SupportedUICultures = [CultureInfo.InvariantCulture];
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

await DatabaseInitializer.InitializeAsync(app.Services, config, logger);
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<DefinitionService>().EnsureDefaultsAsync();
}

app.UseForwardedHeaders();
app.UseRequestLocalization();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// A page that is not there (or not for you) is an empty 404/403 for the pipeline; the Error page says it in words.
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Frame-Options"] = "SAMEORIGIN";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; manifest-src 'self'; worker-src 'self'; " +
        "frame-src 'self' blob:; object-src 'self' blob:; base-uri 'self'; form-action 'self'; frame-ancestors 'self'";
    await next();
});

// The web app manifest has its own type; the service worker must never be cached by the browser, or an update would wait for days.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name is "sw.js" or "manifest.webmanifest")
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    },
});
app.UseAuthentication();

// While there is no administrator the only page is the one that makes the first one.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/Account/Setup") && !path.StartsWithSegments("/health") && !path.StartsWithSegments("/css")
        && !path.StartsWithSegments("/js") && !path.StartsWithSegments("/ui") && !path.StartsWithSegments("/img") && !path.StartsWithSegments("/Offline")
        && !path.Value!.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && !path.Value.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase)
        && path.Value != "/sw.js"
        && !await context.RequestServices.GetRequiredService<SetupState>().HasAdminAsync(context.RequestServices))
    {
        context.Response.Redirect("/Account/Setup");
        return;
    }

    await next();
});

app.UseAuthorization();

app.MapGet("/health", async (ImDbContext db) =>
{
    await db.Database.CanConnectAsync();
    return Results.Text("ok");
}).AllowAnonymous();

app.MapPost("/api/theme", async (HttpContext http, IAntiforgery antiforgery, ImDbContext db, SignInService signIn, ICurrentUser user) =>
{
    await antiforgery.ValidateRequestAsync(http);
    var theme = http.Request.Form["theme"].ToString();
    if (theme is not ("system" or "light" or "dark") || user.UserId is not { } id)
    {
        return Results.BadRequest();
    }

    var entity = await db.User.Include(u => u.Totp).FirstAsync(u => u.Id == id);
    entity.Theme = theme;
    await db.SaveChangesAsync();
    await signIn.RefreshAsync(http, entity);
    return Results.NoContent();
}).RequireAuthorization("SignedIn");

// How many of the given documents are still waiting or being processed (a list page reloads when it reaches zero).
app.MapGet("/api/invoices/status", async (string? ids, ImDbContext db) =>
{
    var list = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => long.TryParse(s, out var v) ? v : 0).Where(v => v > 0).Take(200).ToList();
    var pending = await db.Invoice.CountAsync(i => list.Contains(i.Id) && (i.ProcessingStatus == ProcessingStatus.Queued || i.ProcessingStatus == ProcessingStatus.Processing));
    return Results.Ok(new { pending });
});

// ------------------------------------------------------------------------------------------------ the files of a document

// The original file, exactly as it came. PDFs and images may be shown in the page (with a policy that lets nothing run); anything else is a download.
app.MapGet("/files/{id:long}/original", async (long id, HttpContext http, ImDbContext db, FileStoreService files) =>
{
    var doc = await db.Invoice.AsNoTracking().Include(i => i.OriginalFile).FirstOrDefaultAsync(i => i.Id == id);
    return doc?.OriginalFile is null ? Results.NotFound() : await FileResults.SendAsync(http, files, doc.OriginalFile, doc.FileName);
});

// The XML that was inside a hybrid PDF, as it was found.
app.MapGet("/files/{id:long}/embedded", async (long id, HttpContext http, ImDbContext db, FileStoreService files) =>
{
    var doc = await db.Invoice.AsNoTracking().Include(i => i.EmbeddedXmlFile).FirstOrDefaultAsync(i => i.Id == id);
    return doc?.EmbeddedXmlFile is null ? Results.NotFound() : await FileResults.SendAsync(http, files, doc.EmbeddedXmlFile, doc.EmbeddedXmlFile.FileName, forceDownload: true);
});

// A file InvoiceMesh made (XRechnung, readable PDF).
app.MapGet("/files/generated/{fileId:long}", async (long fileId, HttpContext http, ImDbContext db, FileStoreService files) =>
{
    var generated = await db.GeneratedFile.AsNoTracking().Include(f => f.StoredFile).FirstOrDefaultAsync(f => f.Id == fileId);
    return generated?.StoredFile is null ? Results.NotFound() : await FileResults.SendAsync(http, files, generated.StoredFile, generated.StoredFile.FileName);
});

// The normalized data of the current revision as JSON, in the user's schema.
app.MapGet("/files/{id:long}/normalized.json", async (long id, InvoiceService invoices) =>
{
    var ctx = await invoices.LoadAsync(id);
    return ctx?.NormalizedJson() is { } json ? Results.File(Encoding.UTF8.GetBytes(json), "application/json", $"{InvoiceService.SafeStem(ctx.Doc)}.normalisiert.json") : Results.NotFound();
});

// The extracted data (format, semantic model as read and as corrected, raw fields) as JSON: the source side of the comparison.
app.MapGet("/files/{id:long}/extracted.json", async (long id, InvoiceService invoices) =>
{
    var ctx = await invoices.LoadAsync(id);
    return ctx?.ExtractedJson() is { } json ? Results.File(Encoding.UTF8.GetBytes(json), "application/json", $"{InvoiceService.SafeStem(ctx.Doc)}.ausgelesen.json") : Results.NotFound();
});

// The validation report as JSON or PDF. The PDF is shown in the page (the preview of the files) unless ?download asks for the file.
app.MapGet("/files/{id:long}/report.{format}", async (long id, string format, HttpContext http, InvoiceService invoices) =>
{
    var ctx = await invoices.LoadAsync(id);
    if (ctx?.ReportJson() is not { } json)
    {
        return Results.NotFound();
    }

    var stem = InvoiceService.SafeStem(ctx.Doc) + ".validierung";
    return format switch
    {
        "json" => Results.File(Encoding.UTF8.GetBytes(json), "application/json", stem + ".json"),
        "pdf" => FileResults.Pdf(http, await invoices.ValidationReportPdfAsync(ctx.Doc), stem + ".pdf"),
        _ => Results.NotFound(),
    };
});

// ------------------------------------------------------------------------------------------------ the files behind a share link

app.MapGet("/s/{token:guid}/file/{what}", async (Guid token, string what, long? fileId, HttpContext http, ShareService shares, ImDbContext db, FileStoreService files, InvoiceService invoices) =>
{
    var resolution = await shares.ResolveAsync(token, http.RequestAborted);
    if (resolution.State != ShareState.Active || resolution.Link is not { } link)
    {
        return Results.StatusCode(resolution.State == ShareState.Unknown ? 404 : 410);
    }

    if (shares.NeedsPassword(link, http))
    {
        return Results.Redirect($"/s/{token}");
    }

    switch (what)
    {
        case "original" when link.AllowOriginal:
            var doc = await db.Invoice.AsNoTracking().Include(i => i.OriginalFile).FirstAsync(i => i.Id == link.InvoiceId);
            return await FileResults.SendAsync(http, files, doc.OriginalFile!, doc.FileName, forceDownload: true);
        case "generated" when link.AllowGenerated && fileId is { } gid:
            var generated = await db.GeneratedFile.AsNoTracking().Include(f => f.StoredFile).FirstOrDefaultAsync(f => f.Id == gid && f.InvoiceId == link.InvoiceId);
            return generated?.StoredFile is null ? Results.NotFound() : await FileResults.SendAsync(http, files, generated.StoredFile, generated.StoredFile.FileName, forceDownload: true);
        case "report" when link.AllowGenerated:
            var ctx = await invoices.LoadAsync(link.InvoiceId);
            return ctx is null ? Results.NotFound() : Results.File(await invoices.ValidationReportPdfAsync(ctx.Doc), "application/pdf", InvoiceService.SafeStem(ctx.Doc) + ".validierung.pdf");
        default:
            return Results.NotFound();
    }
}).AllowAnonymous();

app.MapInvoiceApi();
app.MapRazorPages();

await app.RunAsync();
return 0;

/// <summary>How a stored file is sent: PDFs and images may be shown inline (with a policy that lets nothing run), everything else is a download.</summary>
public static class FileResults
{
    public static async Task<IResult> SendAsync(HttpContext http, FileStoreService files, StoredFile file, string name, bool forceDownload = false)
    {
        var bytes = await files.ReadAllAsync(file, http.RequestAborted);
        var kind = InvoiceReader.Sniff(bytes, out var mediaType, out _);
        var inline = !forceDownload && !http.Request.Query.ContainsKey("download") && kind is InvoiceMesh.Core.Formats.FileKind.Pdf or InvoiceMesh.Core.Formats.FileKind.Image && mediaType != "image/tiff";
        http.Response.Headers.CacheControl = "private, no-store";
        // A PDF viewer does not run in a sandboxed document; every other type gets the sandbox as well.
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; object-src 'self'" + (kind == InvoiceMesh.Core.Formats.FileKind.Pdf ? "" : "; sandbox");
        return Results.File(bytes, inline ? mediaType : kind == InvoiceMesh.Core.Formats.FileKind.Xml ? "application/xml" : "application/octet-stream", inline ? null : name);
    }

    /// <summary>A PDF InvoiceMesh made on request (the validation report): shown inline, as a file with ?download.</summary>
    public static IResult Pdf(HttpContext http, byte[] bytes, string name)
    {
        var download = http.Request.Query.ContainsKey("download");
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; object-src 'self'";
        return Results.File(bytes, "application/pdf", download ? name : null);
    }
}

/// <summary>Whether the first administrator exists: the question is asked on every request until the answer is yes, then never again.</summary>
public sealed class SetupState
{
    private volatile bool _done;

    public async Task<bool> HasAdminAsync(IServiceProvider services)
    {
        if (_done)
        {
            return true;
        }

        var db = services.GetRequiredService<ImDbContext>();
        _done = await db.User.AnyAsync(u => u.Role == UserRole.Admin);
        return _done;
    }

    public void MarkDone() => _done = true;
}

/// <summary>The command <c>dotnet InvoiceMesh.dll --healthcheck</c>: asks the running site; exit code 0 means healthy.</summary>
public static class HealthCheck
{
    public static async Task<int> RunAsync()
    {
        var port = Environment.GetEnvironmentVariable("INVOICEMESH_PORT") ?? "2308";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync($"http://localhost:{port}/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}

/// <summary>Makes the entry point visible to the tests (<c>WebApplicationFactory&lt;Program&gt;</c>).</summary>
public partial class Program;
