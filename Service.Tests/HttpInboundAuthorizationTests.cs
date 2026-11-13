using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mulse.Modules.Triggers;
using Service.Endpoints;

namespace Service.Tests;

public sealed class HttpInboundAuthorizationTests
{
    [Fact]
    public async Task InboundRoute_RemainsAvailableWhenManagementEndpointsRequireAuthentication()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, NoAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder("test")
                .RequireAuthenticatedUser()
                .RequireRole("mulse-api-access")
                .RequireClaim("azp", "mulse-service")
                .Build();
        });
        builder.Services.AddSingleton<IHttpInboundRegistry, EmptyInboundRegistry>();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/management", () => Results.Ok());
        app.MapHttpInboundEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };

            using var anonymousManagementRequest = new HttpRequestMessage(HttpMethod.Get, "/api/management");
            using var anonymousManagementResponse = await http.SendAsync(anonymousManagementRequest);

            using var missingRoleRequest = CreateManagementRequest("other-role", "mulse-service");
            using var missingRoleResponse = await http.SendAsync(missingRoleRequest);

            using var wrongClientRequest = CreateManagementRequest("mulse-api-access", "another-client");
            using var wrongClientResponse = await http.SendAsync(wrongClientRequest);

            using var allowedRequest = CreateManagementRequest("mulse-api-access", "mulse-service");
            using var allowedResponse = await http.SendAsync(allowedRequest);

            using var inboundResponse = await http.PostAsync("/api/inbound/existing-source", new StringContent("payload"));

            Assert.Equal(HttpStatusCode.Unauthorized, anonymousManagementResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, missingRoleResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, wrongClientResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, inboundResponse.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static HttpRequestMessage CreateManagementRequest(string role, string client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/management");
        request.Headers.Add("X-Test-Authenticated", "true");
        request.Headers.Add("X-Test-Role", role);
        request.Headers.Add("X-Test-Client", client);
        return request;
    }

    private sealed class EmptyInboundRegistry : IHttpInboundRegistry
    {
        public bool TryGetRoute(string routeKey, out IHttpInboundRoute route)
        {
            route = null!;
            return false;
        }

        public IDisposable RegisterRoute(string routeKey, IHttpInboundRoute route)
            => throw new NotSupportedException();
    }

    private sealed class NoAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-Test-Authenticated"] != "true")
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.Role, Request.Headers["X-Test-Role"].ToString()),
                new Claim("azp", Request.Headers["X-Test-Client"].ToString())
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
