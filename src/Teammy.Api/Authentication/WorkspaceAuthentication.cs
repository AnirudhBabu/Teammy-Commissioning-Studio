using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Teammy.Application;
using Teammy.Domain;

namespace Teammy.Api.Authentication;

public sealed class WorkspaceAuthenticationOptions
{
    public bool DemoEnabled { get; set; }
    public string? Authority { get; set; }
    public string? Audience { get; set; }
    public string RoleClaimType { get; set; } = "roles";
    public List<MemberMapping> MemberMappings { get; set; } = [];
}

public sealed class MemberMapping
{
    public required string Subject { get; set; }
    public required string MemberId { get; set; }
}

public static class WorkspaceAuthentication
{
    public static IServiceCollection AddWorkspaceAuthentication(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection("Authentication").Get<WorkspaceAuthenticationOptions>() ?? new();
        Validate(options, environment.EnvironmentName);
        services.AddSingleton(options);

        if (options.DemoEnabled)
        {
            services.AddAuthentication(DemoAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DemoAuthenticationHandler>(
                    DemoAuthenticationHandler.SchemeName, _ => { });
        }
        else
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.Audience = options.Audience;
                jwt.MapInboundClaims = false;
                jwt.RequireHttpsMetadata = true;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "name",
                    RoleClaimType = options.RoleClaimType
                };
            });
        }

        services.AddAuthorizationBuilder().AddPolicy("WorkspaceMember", policy => policy
            .RequireAuthenticatedUser().RequireRole("Viewer", "Engineer", "Approver"));
        services.AddSingleton<ActorResolver>();
        return services;
    }

    public static void Validate(WorkspaceAuthenticationOptions options, string environment)
    {
        if (options.DemoEnabled)
        {
            if (environment is not ("Development" or "Testing"))
            {
                throw new InvalidOperationException("Demo authentication is allowed only in Development or Testing.");
            }

            return;
        }

        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) ||
            authority.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(options.Audience))
        {
            throw new InvalidOperationException("Configure an HTTPS Authentication:Authority and Authentication:Audience.");
        }

        if (string.IsNullOrWhiteSpace(options.RoleClaimType) || options.MemberMappings.Count == 0 ||
            options.MemberMappings.Any(mapping => string.IsNullOrWhiteSpace(mapping.Subject) ||
                string.IsNullOrWhiteSpace(mapping.MemberId)) ||
            options.MemberMappings.Select(mapping => mapping.Subject).Distinct(StringComparer.Ordinal).Count() != options.MemberMappings.Count ||
            options.MemberMappings.Select(mapping => mapping.MemberId).Distinct(StringComparer.Ordinal).Count() != options.MemberMappings.Count)
        {
            throw new InvalidOperationException("Configure distinct OIDC subject-to-workspace-member mappings and a role claim type.");
        }
    }
}

public sealed class ActorResolver(WorkspaceAuthenticationOptions options)
{
    public Actor Resolve(ClaimsPrincipal principal, WorkspaceState state)
    {
        var subject = principal.FindFirstValue("sub");
        var memberId = options.DemoEnabled ? subject : options.MemberMappings
            .SingleOrDefault(mapping => string.Equals(mapping.Subject, subject, StringComparison.Ordinal))?.MemberId;
        var member = state.Members.SingleOrDefault(candidate => candidate.Id == memberId);
        if (principal.Identity?.IsAuthenticated != true || member is null ||
            !principal.IsInRole(member.Role.ToString()))
        {
            throw new WorkspaceException(WorkspaceErrorKind.Forbidden, "identity.not_member",
                "This authenticated identity is not assigned to a matching workspace role.");
        }

        return new Actor(member.Id, member.DisplayName, member.Role);
    }
}

public sealed class DemoAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ExplicitDevelopmentDemo";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Teammy-Demo-Role"].ToString();
        if (string.IsNullOrWhiteSpace(role))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        Actor? actor = role switch
        {
            "Engineer" => SeedWorkspace.Engineer,
            "Approver" => SeedWorkspace.Approver,
            "Viewer" => SeedWorkspace.Viewer,
            _ => null
        };
        if (actor is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown demo role."));
        }

        var identity = new ClaimsIdentity([
            new Claim("sub", actor.Id), new Claim("name", actor.DisplayName),
            new Claim(ClaimTypes.Role, actor.Role.ToString())
        ], SchemeName, "name", ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
