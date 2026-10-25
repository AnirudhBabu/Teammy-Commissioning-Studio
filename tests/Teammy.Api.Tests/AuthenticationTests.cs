using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Teammy.Api.Authentication;

namespace Teammy.Api.Tests;

public sealed class AuthenticationTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("production")]
    public void DemoIdentityCannotBeEnabledInDeployedEnvironments(string environment)
    {
        Assert.Throws<InvalidOperationException>(() => WorkspaceAuthentication.Validate(
            new WorkspaceAuthenticationOptions { DemoEnabled = true }, environment));
    }

    [Fact]
    public void ProductionFailsClosedWithoutTrustedIdentityConfiguration()
    {
        Assert.Throws<InvalidOperationException>(() => WorkspaceAuthentication.Validate(new(), "Production"));
        Assert.Throws<InvalidOperationException>(() => WorkspaceAuthentication.Validate(new()
        {
            Authority = "http://issuer.example",
            Audience = "teammy",
            MemberMappings = [new() { Subject = "user", MemberId = "engineer-anirudh" }]
        }, "Production"));
    }

    [Fact]
    public void MemberMappingsCannotAliasTwoPeopleToOneApprovalIdentity()
    {
        Assert.Throws<InvalidOperationException>(() => WorkspaceAuthentication.Validate(new()
        {
            Authority = "https://issuer.example",
            Audience = "teammy",
            MemberMappings = [new() { Subject = "one", MemberId = "engineer-anirudh" }, new() { Subject = "two", MemberId = "engineer-anirudh" }]
        }, "Production"));
    }

    [Fact]
    public async Task ValidSignedTokenWithMappedSubjectCanReadWorkspace()
    {
        using var app = new JwtApiFactory();
        using var client = app.Client(null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/workspace")).StatusCode);
    }

    [Theory]
    [InlineData("unknown-subject", "Engineer")]
    [InlineData("oidc-engineer", "Approver")]
    [InlineData("oidc-engineer", "Administrator")]
    public async Task AuthenticatedButUnmappedOrMismatchedRoleIsForbidden(string subject, string role)
    {
        using var app = new JwtApiFactory();
        using var client = app.Client(null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token(subject, role));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/workspace")).StatusCode);
    }

    [Theory]
    [InlineData("wrong-audience")]
    [InlineData("wrong-issuer")]
    [InlineData("expired")]
    [InlineData("wrong-signature")]
    public async Task InvalidTokenIsRejectedByRealBearerMiddleware(string fault)
    {
        using var app = new JwtApiFactory();
        using var client = app.Client(null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token(fault: fault));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/workspace")).StatusCode);
    }

    [Fact]
    public async Task DemoRoleHeaderCannotBypassJwtMode()
    {
        using var app = new JwtApiFactory();
        using var client = app.Client("Approver");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/workspace")).StatusCode);
    }
}

internal sealed class JwtApiFactory : ApiFactory
{
    private readonly RSA _rsa = RSA.Create(2048);
    protected override bool DemoAuthentication => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var key = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
            options.Configuration = new OpenIdConnectConfiguration { Issuer = "https://issuer.teammy.test" };
            options.Configuration.SigningKeys.Add(key);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
        }));
    }

    public string Token(string subject = "oidc-engineer", string role = "Engineer", string? fault = null)
    {
        using var wrongKey = RSA.Create(2048);
        var key = new RsaSecurityKey(fault == "wrong-signature" ? wrongKey : _rsa) { KeyId = "test-key" };
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: fault == "wrong-issuer" ? "https://other-issuer.teammy.test" : "https://issuer.teammy.test",
            audience: fault == "wrong-audience" ? "another-api" : "teammy-tests",
            claims: [new Claim("sub", subject), new Claim("roles", role)],
            notBefore: now.AddMinutes(-20),
            expires: fault == "expired" ? now.AddMinutes(-10) : now.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _rsa.Dispose();
        }
    }
}
