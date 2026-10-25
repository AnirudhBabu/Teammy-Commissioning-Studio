using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Teammy.Application;
using Teammy.Domain;

namespace Teammy.Presentation.Clients;

public sealed class HttpWorkspaceClient : IWorkspaceClient
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();
    private readonly HttpClient _http;
    private readonly bool _demoAuthentication;
    private Actor _demoActor = SeedWorkspace.Engineer;

    public HttpWorkspaceClient(HttpClient http, Uri baseAddress, string? bearerToken = null, bool demoAuthentication = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri || (baseAddress.Scheme != Uri.UriSchemeHttps &&
            !(baseAddress.Scheme == Uri.UriSchemeHttp && baseAddress.IsLoopback)))
        {
            throw new ArgumentException("Use HTTPS for a remote service. HTTP is allowed only on loopback.", nameof(baseAddress));
        }

        if (!string.IsNullOrEmpty(baseAddress.UserInfo) || !string.IsNullOrEmpty(baseAddress.Query) ||
            !string.IsNullOrEmpty(baseAddress.Fragment))
        {
            throw new ArgumentException("The service URL cannot contain credentials, a query, or a fragment.", nameof(baseAddress));
        }

        if (demoAuthentication && !baseAddress.IsLoopback)
        {
            throw new ArgumentException("Demo authentication is restricted to a loopback development API.", nameof(baseAddress));
        }

        _http = http;
        _http.BaseAddress = new Uri(baseAddress.AbsoluteUri.TrimEnd('/') + '/');
        _http.Timeout = TimeSpan.FromSeconds(25);
        _demoAuthentication = demoAuthentication;
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
        }

        ConnectionDescription = baseAddress.GetLeftPart(UriPartial.Authority) +
            (demoAuthentication ? " · development identities" : " · authenticated API");
    }

    public string ModeLabel => _demoAuthentication ? "API DEMO" : "CONNECTED";
    public string ConnectionDescription { get; }
    public bool AllowsActorSwitch => _demoAuthentication;

    public void SetDemoActor(Actor actor)
    {
        if (!_demoAuthentication)
        {
            throw new InvalidOperationException("The connected service controls your identity.");
        }

        if (actor != SeedWorkspace.Engineer && actor != SeedWorkspace.Approver && actor != SeedWorkspace.Viewer)
        {
            throw new ArgumentException("Choose a supported demonstration identity.", nameof(actor));
        }

        _demoActor = actor;
    }

    public Task<Actor> GetActorAsync(CancellationToken cancellationToken = default) =>
        SendAsync<Actor>(HttpMethod.Get, "api/me", null, cancellationToken);

    public Task<WorkspaceState> LoadAsync(CancellationToken cancellationToken = default) =>
        SendAsync<WorkspaceState>(HttpMethod.Get, "api/workspace", null, cancellationToken);

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(CancellationToken cancellationToken = default) =>
        await SendAsync<AuditEntry[]>(HttpMethod.Get, "api/audit?limit=100", null, cancellationToken);

    public Task<WorkspaceState> ExecuteAsync(WorkspaceCommand command, CancellationToken cancellationToken = default) =>
        SendAsync<WorkspaceState>(HttpMethod.Post, "api/commands", command, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, WorkspaceCommand? command, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (_demoAuthentication)
        {
            request.Headers.Add("X-Teammy-Demo-Role", _demoActor.Role.ToString());
        }

        if (command is not null)
        {
            // The declared base type is required for the safe polymorphic discriminator.
            request.Content = JsonContent.Create<WorkspaceCommand>(command, options: Json);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ReadProblemAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken) ??
            throw new InvalidOperationException("The service returned an empty response. Refresh the workspace and try again.");
    }

    private static async Task<WorkspaceException> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var code = "service_error";
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your session has expired or is missing. Obtain a new access token and reconnect.",
            HttpStatusCode.Forbidden => "Your account does not have permission to perform this action.",
            HttpStatusCode.Conflict => "The workspace changed. Refresh it before retrying your action.",
            _ => "The service could not complete this action. Refresh the workspace and try again."
        };

        try
        {
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (body.RootElement.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String)
            {
                code = codeElement.GetString() ?? code;
            }

            if (body.RootElement.TryGetProperty("detail", out var detailElement) && detailElement.ValueKind == JsonValueKind.String)
            {
                message = detailElement.GetString() ?? message;
            }
        }
        catch (JsonException)
        {
            // A proxy may return HTML. Keep the safe status-derived message.
        }

        var kind = response.StatusCode switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => WorkspaceErrorKind.Validation,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => WorkspaceErrorKind.Forbidden,
            HttpStatusCode.NotFound => WorkspaceErrorKind.NotFound,
            HttpStatusCode.Conflict => WorkspaceErrorKind.Conflict,
            _ => WorkspaceErrorKind.Validation
        };
        return new WorkspaceException(kind, code, message);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public void Dispose() => _http.Dispose();
}
