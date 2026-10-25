using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Teammy.Infrastructure;

namespace Teammy.Api.Tests;

internal class ApiFactory(string? existingPath = null, bool deleteDatabase = true) : WebApplicationFactory<Program>
{
    public string DatabasePath { get; } = existingPath ?? Path.Combine(Path.GetTempPath(), $"teammy-api-{Guid.NewGuid():N}.db");
    protected virtual bool DemoAuthentication => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Minimal hosting reads configuration before ConfigureWebHost runs. Inject into
        // the deferred host configuration so startup validation is exercised unchanged.
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Authentication:DemoEnabled"] = DemoAuthentication.ToString(),
                ["Authentication:Authority"] = "https://issuer.teammy.test",
                ["Authentication:Audience"] = "teammy-tests",
                ["Authentication:MemberMappings:0:Subject"] = "oidc-engineer",
                ["Authentication:MemberMappings:0:MemberId"] = "engineer-anirudh",
                ["Authentication:MemberMappings:1:Subject"] = "oidc-approver",
                ["Authentication:MemberMappings:1:MemberId"] = "approver-elena",
                ["Authentication:MemberMappings:2:Subject"] = "oidc-viewer",
                ["Authentication:MemberMappings:2:MemberId"] = "viewer-guest",
                ["Database:Provider"] = "Sqlite",
                ["Database:Initialize"] = "true",
                ["ConnectionStrings:Workspace"] = $"Data Source={DatabasePath};Default Timeout=10",
                ["Logging:LogLevel:Default"] = "Error"
            }));
        return base.CreateHost(builder);
    }

    public HttpClient Client(string? role = "Engineer")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (role is not null)
        {
            client.DefaultRequestHeaders.Add("X-Teammy-Demo-Role", role);
        }

        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && deleteDatabase)
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-shm", "-wal" })
            {
                File.Delete(DatabasePath + suffix);
            }
        }
    }
}
