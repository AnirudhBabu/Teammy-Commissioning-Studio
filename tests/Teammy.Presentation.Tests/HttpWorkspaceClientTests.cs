using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Teammy.Application;
using Teammy.Domain;
using Teammy.Presentation.Clients;
using Xunit;

namespace Teammy.Presentation.Tests;

public sealed class HttpWorkspaceClientTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Theory]
    [InlineData("http://remote.example.test", false)]
    [InlineData("ftp://localhost", false)]
    [InlineData("https://user:password@example.test", false)]
    [InlineData("https://example.test?token=secret", false)]
    [InlineData("https://example.test#token", false)]
    [InlineData("https://example.test", true)]
    public void UnsafeConnectionOptionsAreRejected(string address, bool demo)
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new HttpWorkspaceClient(http, new Uri(address), demoAuthentication: demo));
    }

    [Fact]
    public async Task CommandWireFormatHasDiscriminatorAndRevisionButNeverClientSuppliedActor()
    {
        string? body = null;
        string? role = null;
        using var handler = new StubHandler(async (request, token) =>
        {
            body = await request.Content!.ReadAsStringAsync(token);
            role = request.Headers.GetValues("X-Teammy-Demo-Role").Single();
            Assert.Equal("/api/commands", request.RequestUri!.AbsolutePath);
            return Ok(SeedWorkspace.Create());
        });
        using var client = new HttpWorkspaceClient(new HttpClient(handler), new Uri("http://localhost:5080"), demoAuthentication: true);
        await client.ExecuteAsync(new RunSimulationCommand(7, SeedWorkspace.ConveyorDriveId));
        using var document = JsonDocument.Parse(body!);
        Assert.Equal("runSimulation", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("expectedRevision").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("actor", out _));
        Assert.Equal("Engineer", role);
    }

    [Fact]
    public async Task ProductionRequestsUseBearerAuthenticationWithoutDemoHeaders()
    {
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("short-lived-test-token", request.Headers.Authorization?.Parameter);
            Assert.False(request.Headers.Contains("X-Teammy-Demo-Role"));
            return Task.FromResult(Ok(SeedWorkspace.Approver));
        });
        using var client = new HttpWorkspaceClient(new HttpClient(handler), new Uri("https://example.test"), "short-lived-test-token");
        Assert.Equal(SeedWorkspace.Approver, await client.GetActorAsync());
        Assert.False(client.AllowsActorSwitch);
        Assert.Throws<InvalidOperationException>(() => client.SetDemoActor(SeedWorkspace.Engineer));
    }

    [Fact]
    public async Task DemoIdentitySwitchChangesOnlyExplicitDevelopmentHeader()
    {
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("Approver", request.Headers.GetValues("X-Teammy-Demo-Role").Single());
            return Task.FromResult(Ok(SeedWorkspace.Approver));
        });
        using var client = new HttpWorkspaceClient(new HttpClient(handler), new Uri("http://127.0.0.1:5080"), demoAuthentication: true);
        client.SetDemoActor(SeedWorkspace.Approver);
        Assert.Equal(WorkspaceRole.Approver, (await client.GetActorAsync()).Role);
    }

    [Fact]
    public async Task StructuredConflictsPreserveTheServerCodeAndSafeMessage()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new { code = "revision_conflict", detail = "The revision is stale." })
        }));
        using var client = new HttpWorkspaceClient(new HttpClient(handler), new Uri("https://example.test"));
        var error = await Assert.ThrowsAsync<WorkspaceException>(() => client.LoadAsync());
        Assert.Equal(WorkspaceErrorKind.Conflict, error.Kind);
        Assert.Equal("revision_conflict", error.Code);
        Assert.Equal("The revision is stale.", error.Message);
    }

    [Fact]
    public async Task ProxyHtmlIsNotDisplayedAsAnErrorOrTreatedAsSuccess()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>sensitive proxy internals</html>", Encoding.UTF8, "text/html")
        }));
        using var client = new HttpWorkspaceClient(new HttpClient(handler), new Uri("https://example.test"));
        var error = await Assert.ThrowsAsync<WorkspaceException>(() => client.LoadAsync());
        Assert.DoesNotContain("sensitive", error.Message, StringComparison.Ordinal);
        Assert.Equal("service_error", error.Code);
    }

    [Fact]
    public async Task AuthenticationFailureHasAnActionableMessage()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("")
        }));
        using var client = new HttpWorkspaceClient(new HttpClient(handler), new Uri("https://example.test"));
        var error = await Assert.ThrowsAsync<WorkspaceException>(() => client.LoadAsync());
        Assert.Equal(WorkspaceErrorKind.Forbidden, error.Kind);
        Assert.Contains("access token", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationReachesTheHttpHandler()
    {
        using var handler = new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Ok(SeedWorkspace.Create());
        });
        using var client = new HttpWorkspaceClient(new HttpClient(handler), new Uri("https://example.test"));
        using var source = new CancellationTokenSource();
        var pending = client.LoadAsync(source.Token);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value, options: Json)
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
