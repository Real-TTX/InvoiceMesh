using System.Text.RegularExpressions;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.Services.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace InvoiceMesh.Tests.Integration;

/// <summary>
/// A running application against a real PostgreSQL: its own database (made and dropped here) and its own data folder. The tests need a server and
/// skip themselves without one: <c>INVOICEMESH_TEST_DB</c> is a connection string such as <c>Host=localhost;Port=5432;Username=postgres;Password=...</c>.
/// </summary>
public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Variable = "INVOICEMESH_TEST_DB";

    public static bool Available => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable));

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "im-it-" + Guid.NewGuid().ToString("N")[..8]);

    public string DatabaseName { get; } = "im_it_" + Guid.NewGuid().ToString("N")[..10];

    // The application reads where it lives from the environment while it starts. The test classes share one collection, so they start one after the other.
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var b = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Variable));
        Directory.CreateDirectory(DataDir);
        Environment.SetEnvironmentVariable("INVOICEMESH_DATA", DataDir);
        Environment.SetEnvironmentVariable("INVOICEMESH__Database__Host", b.Host);
        Environment.SetEnvironmentVariable("INVOICEMESH__Database__Port", b.Port.ToString());
        Environment.SetEnvironmentVariable("INVOICEMESH__Database__Username", b.Username);
        Environment.SetEnvironmentVariable("INVOICEMESH__Database__Password", b.Password);
        Environment.SetEnvironmentVariable("INVOICEMESH__Database__Database", DatabaseName);
        return base.CreateHost(builder);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        if (!Available)
        {
            return;
        }

        try
        {
            var b = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Variable)) { Database = "postgres", Pooling = false };
            await using var connection = new NpgsqlConnection(b.ConnectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
            Directory.Delete(DataDir, recursive: true);
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException)
        {
            // a temporary database that stays behind is not worth failing a test run
        }
    }

    // ------------------------------------------------------------------------------------------------ what the tests start from

    /// <summary>Runs something with the services of a request scope (a context, the services of the application).</summary>
    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public Task WithScopeAsync(Func<IServiceProvider, Task> action) => WithScopeAsync<int>(async sp =>
    {
        await action(sp);
        return 0;
    });

    public Task<User> CreateUserAsync(string email, string password, UserRole role)
        => WithScopeAsync(sp => sp.GetRequiredService<UserService>().CreateAsync(email, "Test " + role, role, password));

    /// <summary>Until there is an administrator the only page is the setup; most tests start with one.</summary>
    public async Task EnsureAdminAsync()
    {
        if (!await WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().User.AnyAsync(u => u.Role == UserRole.Admin)))
        {
            await CreateUserAsync("admin@example.com", "Start12345", UserRole.Admin);
        }
    }

    /// <summary>
    /// A processed document: the sample XRechnung with its own number (so that no two tests share one), uploaded and processed (by the worker, or here when the
    /// worker has not taken it yet). The original is in the store and in the table of the files.
    /// </summary>
    public async Task<(InvoiceDocument Document, StoredFile Original)> CreateDocumentAsync(string name)
    {
        var xml = System.Text.Encoding.UTF8.GetString(Core.Samples.Bytes("xrechnung-ubl.xml")).Replace("RE-2026-0815", "RE-" + name, StringComparison.Ordinal);
        var id = await WithScopeAsync(async sp => (await sp.GetRequiredService<InvoiceService>().AddAsync(System.Text.Encoding.UTF8.GetBytes(xml), name + ".xml")).Document.Id);
        return await ProcessedAsync(id);
    }

    /// <summary>Waits until a document is processed (it processes it itself when it is still waiting) and returns it with its original.</summary>
    public async Task<(InvoiceDocument Document, StoredFile Original)> ProcessedAsync(long id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            await WithScopeAsync(sp => sp.GetRequiredService<InvoiceService>().ProcessAsync(id));
            var doc = await WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().Invoice.IgnoreQueryFilters().AsNoTracking().Include(i => i.OriginalFile).FirstAsync(i => i.Id == id));
            if (doc.ProcessingStatus is ProcessingStatus.Processed or ProcessingStatus.Failed)
            {
                return (doc, doc.OriginalFile!);
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Document {id} was not processed in time ({doc.ProcessingStatus}).");
            }

            await Task.Delay(200);
        }
    }

    // ------------------------------------------------------------------------------------------------ talking to the pages

    public HttpClient NewClient(bool followRedirects = false)
        => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = followRedirects, HandleCookies = true });

    private static readonly Regex Token = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>The anti-forgery token of the form on a page (the cookie that belongs to it is kept by the client).</summary>
    public static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        return Token.Match(html) is { Success: true } m ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : throw new InvalidOperationException("No form on " + url);
    }

    /// <summary>Posts a form the way a browser does: loads the page (for the token), then posts the fields.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageUrl, IDictionary<string, string> fields, string? postUrl = null)
    {
        var token = await TokenAsync(client, pageUrl);
        var body = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        return await client.PostAsync(postUrl ?? pageUrl, new FormUrlEncodedContent(body));
    }

    /// <summary>The path a redirect points to (the address may be relative).</summary>
    public static string LocationPath(HttpResponseMessage response) => new Uri(new Uri("http://localhost"), response.Headers.Location!).AbsolutePath;

    /// <summary>A value of the query of the address a redirect points to.</summary>
    public static string LocationQuery(HttpResponseMessage response, string name)
        => System.Web.HttpUtility.ParseQueryString(new Uri(new Uri("http://localhost"), response.Headers.Location!).Query)[name] ?? throw new InvalidOperationException($"No {name} in {response.Headers.Location}");

    /// <summary>The text of a page as a person reads it: Razor writes "ü" as an HTML entity.</summary>
    public static async Task<string> TextAsync(HttpResponseMessage response) => System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    public static async Task<string> TextAsync(HttpClient client, string url) => System.Net.WebUtility.HtmlDecode(await client.GetStringAsync(url));

    public static async Task LoginAsync(HttpClient client, string email, string password)
    {
        var response = await PostFormAsync(client, "/Account/Login", new Dictionary<string, string> { ["Input.Email"] = email, ["Input.Password"] = password });
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    }
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection
{
    public const string Name = "integration";
}

/// <summary>A test that needs the PostgreSQL of <see cref="AppFixture.Variable"/>; without it the test is skipped (and says so).</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (!AppFixture.Available)
        {
            Skip = $"{AppFixture.Variable} is not set: no PostgreSQL to run the application against.";
        }
    }
}
