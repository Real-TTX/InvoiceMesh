using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services.Automation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceMesh.Tests.Integration;

/// <summary>The ways documents come in by themselves and go out by themselves: the API, a folder, planned exports into a folder and to a webhook.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class AutomationTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public AutomationTests(AppFixture app) => _app = app;

    private static byte[] Sample(string number)
        => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Core.Samples.Bytes("xrechnung-ubl.xml")).Replace("RE-2026-0815", number, StringComparison.Ordinal));

    private Task<string> KeyAsync(bool canImport, bool canRead, DateTime? expires = null)
        => _app.WithScopeAsync(async sp => (await sp.GetRequiredService<ApiKeyService>().CreateAsync("Test", canImport, canRead, expires)).Plain);

    private HttpClient Api(string? key)
    {
        var client = _app.NewClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return client;
    }

    private static MultipartFormDataContent Form(params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            form.Add(new ByteArrayContent(bytes), "file", name);
        }

        return form;
    }

    [DatabaseFact]
    public async Task The_API_takes_documents_with_a_key_that_may_and_gives_their_state_data_and_XRechnung()
    {
        await _app.EnsureAdminAsync();
        var full = await KeyAsync(true, true);
        var readOnly = await KeyAsync(false, true);
        var expired = await KeyAsync(true, true, DateTime.UtcNow.AddMinutes(-1));

        // without a key, with a key that may not, with one that expired
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api(null).PostAsync("/api/v1/invoices", Form(("a.xml", Sample("RE-API-0"))))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api("im_falsch").GetAsync("/api/v1/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Api(readOnly).PostAsync("/api/v1/invoices", Form(("a.xml", Sample("RE-API-0"))))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api(expired).GetAsync("/api/v1/invoices")).StatusCode);

        // two files in a form, one as the body, and a ZIP with two more
        var posted = await Api(full).PostAsync("/api/v1/invoices", Form(("a.xml", Sample("RE-API-1")), ("leer.pdf", [])));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        using var answer = JsonDocument.Parse(await posted.Content.ReadAsStringAsync());
        var id = answer.RootElement.GetProperty("documents")[0].GetProperty("id").GetInt64();
        Assert.Contains("leer", answer.RootElement.GetProperty("problems")[0].GetString(), StringComparison.Ordinal);

        using var raw = new ByteArrayContent(Sample("RE-API-2"));
        raw.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/invoices") { Content = raw };
        request.Headers.Add("X-File-Name", "zwei.xml");
        Assert.Equal(HttpStatusCode.Accepted, (await Api(full).SendAsync(request)).StatusCode);

        var zip = Core.ArchiveTests.Zip(("drei.xml", Sample("RE-API-3")), ("vier.xml", Sample("RE-API-4")));
        var zipped = await Api(full).PostAsync("/api/v1/invoices", Form(("paket.zip", zip)));
        using var zipAnswer = JsonDocument.Parse(await zipped.Content.ReadAsStringAsync());
        Assert.Equal(2, zipAnswer.RootElement.GetProperty("documents").GetArrayLength());

        // a body without a name, and nothing that could be taken
        Assert.Equal(HttpStatusCode.BadRequest, (await Api(full).PostAsync("/api/v1/invoices", new ByteArrayContent([1, 2, 3]))).StatusCode);
        var nothing = await Api(full).PostAsync("/api/v1/invoices", Form(("leer.pdf", [])));
        Assert.Equal((HttpStatusCode)422, nothing.StatusCode);

        // the document knows where it came from; once processed, its data and XRechnung can be read (with a key that may read)
        var stored = await _app.WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().Invoice.AsNoTracking().Where(i => i.Source == InvoiceSource.Api).ToListAsync());
        Assert.Equal(4, stored.Count);
        Assert.Contains(stored, d => d.SourceDetail!.Contains("aus paket.zip: vier.xml", StringComparison.Ordinal));
        await _app.ProcessedAsync(id);

        var detail = await Api(readOnly).GetStringAsync($"/api/v1/invoices/{id}");
        Assert.Contains("\"source\":\"api\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"processingStatus\":\"processed\"", detail, StringComparison.Ordinal);
        Assert.Contains("RE-API-1", await Api(readOnly).GetStringAsync($"/api/v1/invoices/{id}/normalized"), StringComparison.Ordinal);
        Assert.Contains("\"findings\"", await Api(readOnly).GetStringAsync($"/api/v1/invoices/{id}/report"), StringComparison.Ordinal);
        var xrechnung = await Api(readOnly).GetAsync($"/api/v1/invoices/{id}/xrechnung");
        Assert.Equal(HttpStatusCode.OK, xrechnung.StatusCode);
        Assert.Contains("RE-API-1", await xrechnung.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(Sample("RE-API-1"), await Api(readOnly).GetByteArrayAsync($"/api/v1/invoices/{id}/original"));
        Assert.Contains($"\"id\":{id}", await Api(readOnly).GetStringAsync("/api/v1/invoices?status=processed"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await Api(readOnly).GetAsync("/api/v1/invoices/999999")).StatusCode);

        // an import-only key may not read
        Assert.Equal(HttpStatusCode.Forbidden, (await Api(await KeyAsync(true, false)).GetAsync($"/api/v1/invoices/{id}")).StatusCode);
    }

    [DatabaseFact]
    public async Task A_key_made_on_the_page_is_shown_once_and_works()
    {
        await _app.EnsureAdminAsync();
        using var admin = _app.NewClient();
        await AppFixture.LoginAsync(admin, "admin@example.com", "Start12345");

        var made = await AppFixture.PostFormAsync(admin, "/Admin/ApiKeys/Edit", new Dictionary<string, string>
        {
            ["Input.Name"] = "ERP", ["Input.CanImport"] = "true", ["Input.CanRead"] = "true", ["Input.IsEnabled"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, made.StatusCode);
        var page = await AppFixture.TextAsync(admin, made.Headers.Location!.ToString());
        var key = System.Text.RegularExpressions.Regex.Match(page, "value=\"(im_[A-Za-z0-9_-]{40,})\"").Groups[1].Value;
        Assert.NotEmpty(key);
        Assert.DoesNotContain(key, await AppFixture.TextAsync(admin, made.Headers.Location!.ToString()), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await Api(key).GetAsync("/api/v1/invoices")).StatusCode);
        foreach (var url in new[] { "/Admin/ApiKeys", "/Admin/Intake", "/Admin/Intake/Edit", "/Admin/ExportPlans", "/Admin/ExportPlans/Edit" })
        {
            Assert.Contains("<h1", await admin.GetStringAsync(url), StringComparison.Ordinal);
        }
    }

    [DatabaseFact]
    public async Task An_intake_source_is_checked_and_then_saved_from_the_page_it_was_checked_on()
    {
        await _app.EnsureAdminAsync();
        using var admin = _app.NewClient();
        await AppFixture.LoginAsync(admin, "admin@example.com", "Start12345");
        var fields = new Dictionary<string, string>
        {
            ["Input.Name"] = "Ablage", ["Input.Kind"] = "Folder", ["Input.IntervalMinutes"] = "15", ["Input.Path"] = Path.Combine(_app.DataDir, "ablage"), ["Input.DoneFolder"] = "verarbeitet",
        };

        // "Prüfen" answers with the page; its form must post to the save handler again, not to the check
        var checkedPage = await AppFixture.PostFormAsync(admin, "/Admin/Intake/Edit", fields, "/Admin/Intake/Edit?handler=Test");
        var html = await AppFixture.TextAsync(checkedPage);
        Assert.Contains("ist da und beschreibbar", html, StringComparison.Ordinal);
        var form = System.Text.RegularExpressions.Regex.Match(html, "<form[^>]*class=\"mat-form\"[^>]*>").Value;
        var action = System.Text.RegularExpressions.Regex.Match(form, "action=\"([^\"]*)\"").Groups[1].Value;
        Assert.Equal("/Admin/Intake/Edit", System.Net.WebUtility.HtmlDecode(action));

        var saved = await AppFixture.PostFormAsync(admin, "/Admin/Intake/Edit", fields, action);
        Assert.Equal("/Admin/Intake", AppFixture.LocationPath(saved));
        Assert.True(await _app.WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().IntakeSource.AnyAsync(s => s.Name == "Ablage")));
    }

    [DatabaseFact]
    public async Task A_folder_is_emptied_into_documents_with_ZIPs_unpacked_and_failures_kept_apart()
    {
        await _app.EnsureAdminAsync();
        var folder = Path.Combine(_app.DataDir, "eingang-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        var old = DateTime.UtcNow.AddMinutes(-1);
        void Put(string name, byte[] bytes, DateTime? modified = null)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, modified ?? old);
        }

        Put("scan.xml", Sample("RE-ORDNER-1"));
        Put("paket.zip", Core.ArchiveTests.Zip(("a.xml", Sample("RE-ORDNER-2")), ("b.xml", Sample("RE-ORDNER-3"))));
        Put("leer.pdf", []);
        Put("noch-im-schreiben.xml", Sample("RE-ORDNER-4"), DateTime.UtcNow);

        var source = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ImDbContext>();
            var s = db.IntakeSource.Add(new IntakeSource { Name = "Scanner", Kind = IntakeKind.Folder, Path = folder, DoneFolder = "verarbeitet", IsEnabled = false }).Entity;
            await db.SaveChangesAsync();
            return s.Id;
        });

        var outcome = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ImDbContext>();
            return await sp.GetRequiredService<IntakeService>().RunAsync(await db.IntakeSource.FirstAsync(s => s.Id == source), manual: true);
        });

        Assert.Equal(3, outcome.Added.Count);
        Assert.Single(outcome.Problems);
        Assert.Equal(["noch-im-schreiben.xml"], Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(folder, "verarbeitet")).Length);
        Assert.Single(Directory.GetFiles(Path.Combine(folder, "fehler")));

        var saved = await _app.WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().IntakeSource.AsNoTracking().FirstAsync(s => s.Id == source));
        Assert.True(saved.LastRunOk);
        Assert.Equal(3, saved.LastCount);
        Assert.Contains("3 Belege", saved.LastStatus, StringComparison.Ordinal);

        var docs = await _app.WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().Invoice.AsNoTracking().Where(i => i.Source == InvoiceSource.Folder).ToListAsync());
        Assert.Equal(3, docs.Count);
        Assert.All(docs, d => Assert.StartsWith("Scanner: ", d.SourceDetail, StringComparison.Ordinal));

        // a folder that cannot be reached is a failure of the run, written down, not an exception
        var broken = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ImDbContext>();
            var s = db.IntakeSource.Add(new IntakeSource { Name = "Kaputt", Kind = IntakeKind.Smb, Host = "127.0.0.1", Share = "nichts", Path = "x", IsEnabled = false, UserName = "x" }).Entity;
            await db.SaveChangesAsync();
            await sp.GetRequiredService<IntakeService>().RunAsync(s, manual: true);
            return s;
        });
        Assert.False(broken.LastRunOk);
        Assert.StartsWith("Fehler", broken.LastStatus, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task A_planned_export_delivers_what_is_new_into_a_folder_and_to_a_webhook()
    {
        await _app.EnsureAdminAsync();

        // documents of other tests of this class that are still waiting would turn up as "new" in the middle of this test
        var waiting = await _app.WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().Invoice.Where(i => i.ProcessingStatus == ProcessingStatus.Queued || i.ProcessingStatus == ProcessingStatus.Processing).Select(i => i.Id).ToListAsync());
        foreach (var id in waiting)
        {
            await _app.ProcessedAsync(id);
        }

        // the documents of this test are taken at once, not 30 seconds after they were processed
        ExportPlanService.Settle = TimeSpan.Zero;
        await _app.CreateDocumentAsync("PLAN-1");
        var folder = Path.Combine(_app.DataDir, "exporte-" + Guid.NewGuid().ToString("N")[..6]);
        var profile = await _app.WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().ExportProfile.Where(e => e.Name == "CSV je Rechnung").Select(e => e.Id).FirstAsync());

        async Task<(ExportPlanService.RunResult Result, ExportPlan Plan)> RunAsync(ExportPlan plan)
            => await _app.WithScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<ImDbContext>();
                var tracked = plan.Id == 0 ? db.ExportPlan.Add(plan).Entity : await db.ExportPlan.FirstAsync(p => p.Id == plan.Id);
                await db.SaveChangesAsync();
                var result = await sp.GetRequiredService<ExportPlanService>().RunAsync(tracked, manual: true);
                return (result, tracked);
            });

        var (first, plan) = await RunAsync(new ExportPlan { Name = "Buchhaltung", ExportProfileId = profile, Delivery = DeliveryKind.Folder, Path = folder, Days = 1, IsEnabled = false });
        Assert.True(first.Ok, first.Message);
        Assert.True(first.Count >= 1);
        var file = Assert.Single(Directory.GetFiles(folder));
        Assert.Contains("RE-PLAN-1", await File.ReadAllTextAsync(file), StringComparison.Ordinal);

        // nothing new: no file
        var (second, _) = await RunAsync(plan);
        Assert.True(second.Ok);
        Assert.Equal(0, second.Count);
        Assert.Single(Directory.GetFiles(folder));

        // a new document is new again
        await _app.CreateDocumentAsync("PLAN-2");
        var (third, _) = await RunAsync(plan);
        Assert.Equal(1, third.Count);

        // a webhook gets the file with its name and the header of the plan
        var received = new List<(string? Authorization, string? Disposition, string Body)>();
        var hook = WebApplication.CreateSlimBuilder();
        hook.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = hook.Build();
        server.MapPost("/hook", async (HttpRequest r) =>
        {
            using var reader = new StreamReader(r.Body);
            received.Add((r.Headers.Authorization, r.Headers.ContentDisposition, await reader.ReadToEndAsync()));
            return Results.Ok();
        });
        await server.StartAsync();
        var url = server.Urls.First() + "/hook";

        var secret = await _app.WithScopeAsync(sp => Task.FromResult(sp.GetRequiredService<InvoiceMesh.Configuration.SecretProtector>().Protect("Bearer geheim")));
        var webhook = new ExportPlan
        {
            Name = "ERP", ExportProfileId = profile, Delivery = DeliveryKind.Webhook, Url = url, HeaderName = "Authorization", HeaderValueProtected = secret,
            Selection = ExportSelection.LastDays, Days = 1, IsEnabled = false,
        };
        var (pushed, _) = await RunAsync(webhook);
        Assert.True(pushed.Ok, pushed.Message);
        var (authorization, disposition, body) = Assert.Single(received);
        Assert.Equal("Bearer geheim", authorization);
        Assert.Contains(".csv", disposition, StringComparison.Ordinal);
        Assert.Contains("RE-PLAN-2", body, StringComparison.Ordinal);

        // an address that does not answer is a failed run, tried again soon
        var (failed, failedPlan) = await RunAsync(new ExportPlan { Name = "Weg", ExportProfileId = profile, Delivery = DeliveryKind.Webhook, Url = "http://127.0.0.1:1/x", Selection = ExportSelection.LastDays, Days = 1, IsEnabled = false });
        Assert.False(failed.Ok);
        Assert.Equal(1, failedPlan.FailedAttempts);
    }
}
