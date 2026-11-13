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
        builder.Services.AddSingleton(new RuntimeStorageSettings("Sqlite", true, "C:\\data\\mulse.db", true, "primary", "MULSE_STATE_ENCRYPTION_KEY"));
        using var workspace = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        builder.Services.AddSingleton<IRuntimeConfigurationStore>(workspace.Store());
        builder.Services.AddSingleton<ProcessingCoordinator>();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/management", () => Results.Ok());
        app.MapStorageEndpoints();
        app.MapHistoryEndpoints();
        app.MapProcessingEndpoints();
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
            using var anonymousStorage = await http.GetAsync("/api/workspace/storage");
            using var allowedStorageRequest = CreateManagementRequest("mulse-api-access", "mulse-service");
            allowedStorageRequest.RequestUri = new Uri("/api/workspace/storage", UriKind.Relative);
            using var allowedStorage = await http.SendAsync(allowedStorageRequest);
            using var forbiddenStorageRequest = CreateManagementRequest("other-role", "mulse-service");
            forbiddenStorageRequest.RequestUri = new Uri("/api/workspace/storage", UriKind.Relative);
            using var forbiddenStorage = await http.SendAsync(forbiddenStorageRequest);

            using var anonymousHistory = await http.GetAsync("/api/history/executions");
            using var anonymousPayload = await http.GetAsync("/api/history/messages/" + Guid.NewGuid());
            using var forbiddenHistoryRequest = CreateManagementRequest("other-role", "mulse-service");
            forbiddenHistoryRequest.RequestUri = new Uri("/api/history/messages", UriKind.Relative);
            using var forbiddenHistory = await http.SendAsync(forbiddenHistoryRequest);
            using var anonymousMigration = await http.GetAsync("/api/workspace/storage/migration");
            using var anonymousCopy = await http.PostAsync("/api/workspace/storage/migration/execute", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            using var forbiddenMigrationRequest = CreateManagementRequest("other-role", "mulse-service");
            forbiddenMigrationRequest.Method = HttpMethod.Post;
            forbiddenMigrationRequest.RequestUri = new Uri("/api/workspace/storage/migration/preview", UriKind.Relative);
            forbiddenMigrationRequest.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var forbiddenMigration = await http.SendAsync(forbiddenMigrationRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousMigration.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousCopy.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenMigration.StatusCode);
            using var anonymousProcessing = await http.GetAsync("/api/workspace/processing");
            using var anonymousBacklog = await http.GetAsync("/api/workspace/processing/backlog");
            using var anonymousPause = await http.PutAsync("/api/workspace/processing/maintenance", new StringContent("{\"requested\":true}", System.Text.Encoding.UTF8, "application/json"));
            using var forbiddenPauseRequest = CreateManagementRequest("other-role", "mulse-service");
            forbiddenPauseRequest.Method = HttpMethod.Put;
            forbiddenPauseRequest.RequestUri = new Uri("/api/workspace/processing/maintenance", UriKind.Relative);
            forbiddenPauseRequest.Content = new StringContent("{\"requested\":true}", System.Text.Encoding.UTF8, "application/json");
            using var forbiddenPause = await http.SendAsync(forbiddenPauseRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousProcessing.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousBacklog.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousPause.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenPause.StatusCode);
            Assert.False(app.Services.GetRequiredService<IRuntimeConfigurationStore>().GetState().MaintenanceRequested);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousHistory.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousPayload.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenHistory.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousStorage.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenStorage.StatusCode);
            Assert.Equal(HttpStatusCode.OK, allowedStorage.StatusCode);
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
