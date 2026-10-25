using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Teammy.Api;
using Teammy.Api.Authentication;
using Teammy.Api.Observability;
using Teammy.Application;
using Teammy.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var migrateOnly = args.Contains("--migrate", StringComparer.Ordinal);
var initialize = builder.Configuration.GetValue<bool>("Database:Initialize");
if (initialize && !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
{
    throw new InvalidOperationException("Production schema changes must use the separate --migrate command.");
}

var provider = builder.Configuration["Database:Provider"] ?? "PostgreSql";
var connection = builder.Configuration.GetConnectionString("Workspace");
if (string.IsNullOrWhiteSpace(connection))
{
    throw new InvalidOperationException("Configure ConnectionStrings:Workspace externally.");
}

switch (provider)
{
    case "Sqlite":
        builder.Services.AddDbContext<SqliteWorkspaceDbContext>(options => options.UseSqlite(connection));
        builder.Services.AddScoped<WorkspaceDbContext>(services => services.GetRequiredService<SqliteWorkspaceDbContext>());
        break;
    case "PostgreSql":
        builder.Services.AddDbContext<PostgresWorkspaceDbContext>(options =>
            options.UseNpgsql(connection, postgres => postgres.CommandTimeout(15)));
        builder.Services.AddScoped<WorkspaceDbContext>(services => services.GetRequiredService<PostgresWorkspaceDbContext>());
        break;
    default:
        throw new InvalidOperationException("Database:Provider must be Sqlite or PostgreSql.");
}
builder.Services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
builder.Services.AddSingleton<WorkspaceEngine>();
builder.Services.AddSingleton(TimeProvider.System);

if (migrateOnly)
{
    // Deliberately independent of HTTP/auth hosting: CI runs this once before traffic moves.
    using var migrationHost = builder.Build();
    await using var scope = migrationHost.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
    await DatabaseBootstrapper.InitializeAsync(database);
    var runtimePassword = builder.Configuration["Database:RuntimePassword"];
    if (!string.IsNullOrEmpty(runtimePassword))
    {
        await RuntimeRoleProvisioner.ProvisionAsync(database, runtimePassword);
    }
    return;
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 64 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddWorkspaceAuthentication(builder.Configuration, builder.Environment);
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
    options.SerializerOptions.AllowOutOfOrderMetadataProperties = true;
});
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
});
builder.Services.AddExceptionHandler<WorkspaceExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<WorkspaceHealthCheck>("workspace", tags: ["ready"]);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("workspace", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst("sub")?.Value ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var telemetry = builder.Services.AddOpenTelemetry().ConfigureResource(resource => resource
    .AddService("teammy-api", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
    .WithTracing(tracing => tracing.AddSource(WorkspaceTelemetry.Name)
        .AddAspNetCoreInstrumentation(options => options.Filter = context =>
            !context.Request.Path.StartsWithSegments("/health"))
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics.AddMeter(WorkspaceTelemetry.Name)
        .AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    telemetry.WithTracing(tracing => tracing.AddOtlpExporter()).WithMetrics(metrics => metrics.AddOtlpExporter());
}

var app = builder.Build();
if (initialize)
{
    await using var scope = app.Services.CreateAsyncScope();
    await DatabaseBootstrapper.InitializeAsync(scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>());
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-Trace-Id"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi().RequireAuthorization("WorkspaceMember");
}

var api = app.MapGroup("/api").RequireAuthorization("WorkspaceMember").RequireRateLimiting("workspace");
api.MapGet("/workspace", async (IWorkspaceRepository repository, ActorResolver resolver,
    HttpContext context, CancellationToken cancellationToken) =>
{
    var state = await repository.ReadAsync(cancellationToken);
    resolver.Resolve(context.User, state);
    context.Response.Headers.ETag = $"\"{state.Revision}\"";
    return Results.Ok(state);
}).WithName("GetWorkspace");

api.MapGet("/me", async (IWorkspaceRepository repository, ActorResolver resolver,
    HttpContext context, CancellationToken cancellationToken) =>
    Results.Ok(resolver.Resolve(context.User, await repository.ReadAsync(cancellationToken))))
    .WithName("GetCurrentActor");

api.MapGet("/audit", async (IWorkspaceRepository repository, ActorResolver resolver,
    HttpContext context, int? limit, int? beforeRevision, CancellationToken cancellationToken) =>
{
    resolver.Resolve(context.User, await repository.ReadAsync(cancellationToken));
    return Results.Ok(await repository.ReadAuditAsync(limit ?? 100, beforeRevision ?? int.MaxValue, cancellationToken));
}).WithName("GetAuditTrail");

api.MapPost("/commands", async (WorkspaceCommand command, IWorkspaceRepository repository, WorkspaceEngine engine,
    ActorResolver resolver, TimeProvider clock, HttpContext context, CancellationToken cancellationToken) =>
{
    var started = Stopwatch.GetTimestamp();
    using var activity = WorkspaceTelemetry.Activities.StartActivity("workspace.command");
    var commandName = command.GetType().Name;
    activity?.SetTag("teammy.command", commandName);
    var outcome = "rejected";
    try
    {
        var state = await repository.ReadAsync(cancellationToken);
        var actor = resolver.Resolve(context.User, state);
        var mutation = engine.Apply(state, command, actor, clock.GetUtcNow());
        await repository.SaveAsync(mutation, command.ExpectedRevision, cancellationToken);
        outcome = "committed";
        activity?.SetTag("teammy.revision", mutation.State.Revision);
        context.Response.Headers.ETag = $"\"{mutation.State.Revision}\"";
        return Results.Ok(mutation.State);
    }
    finally
    {
        var tags = new TagList { { "command", commandName }, { "outcome", outcome } };
        WorkspaceTelemetry.Commands.Add(1, tags);
        WorkspaceTelemetry.CommandDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
    }
}).WithName("ExecuteWorkspaceCommand");

app.Run();

public partial class Program;
