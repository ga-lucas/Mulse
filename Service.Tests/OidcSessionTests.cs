using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class OidcSessionTests
{
    [Fact]
    public async Task TicketReceived_RetainsOnlyIdToken()
    {
        var options = new OpenIdConnectOptions();
        OidcSession.Configure(options);
        var properties = new AuthenticationProperties();
        properties.StoreTokens([
            new AuthenticationToken { Name = "id_token", Value = "test-id-token" },
            new AuthenticationToken { Name = "access_token", Value = "test-access-token" },
            new AuthenticationToken { Name = "refresh_token", Value = "test-refresh-token" }
        ]);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("test")), properties, "oidc");
        var context = new TicketReceivedContext(new DefaultHttpContext(),
            new AuthenticationScheme("oidc", null, typeof(OpenIdConnectHandler)), options, ticket);

        await options.Events.TicketReceived(context);

        Assert.True(options.SaveTokens);
        var savedToken = Assert.Single(properties.GetTokens());
        Assert.Equal("id_token", savedToken.Name);
        Assert.Equal("test-id-token", savedToken.Value);
    }

    [Fact]
    public async Task TicketReceived_WithoutProperties_CreatesEmptyTokenStorage()
    {
        var options = new OpenIdConnectOptions();
        OidcSession.Configure(options);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("test")), "oidc");
        var context = new TicketReceivedContext(new DefaultHttpContext(),
            new AuthenticationScheme("oidc", null, typeof(OpenIdConnectHandler)), options, ticket)
        {
            Properties = null
        };

        await options.Events.TicketReceived(context);

        Assert.NotNull(context.Properties);
        Assert.Empty(context.Properties.GetTokens());
        Assert.Same(ticket.Principal, context.Principal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Logout_UsesSavedIdTokenOrClientIdAndClearsCookie(bool hasIdToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie()
        .AddOpenIdConnect(options =>
        {
            options.ClientId = "mulse-ui";
            options.ClientSecret = "test-only-secret";
            options.ResponseType = "code";
            options.Configuration = new OpenIdConnectConfiguration
            {
                Issuer = "https://identity.example.test/realms/mulse",
                AuthorizationEndpoint = "https://identity.example.test/auth",
                TokenEndpoint = "https://identity.example.test/token",
                EndSessionEndpoint = "https://identity.example.test/logout"
            };
            OidcSession.Configure(options);
        });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/", () => "Protected workspace").RequireAuthorization();
        app.MapGet("/test-signin", async (HttpContext context) =>
        {
            var properties = new AuthenticationProperties();
            if (hasIdToken)
            {
                properties.StoreTokens([new AuthenticationToken { Name = "id_token", Value = "test-id-token" }]);
            }
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-user")], "test")), properties);
        });
        app.MapPost("/test-signout", () => Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(address) };
            using var login = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            Assert.Equal("/auth", login.Headers.Location!.AbsolutePath);
            var loginQuery = QueryHelpers.ParseQuery(login.Headers.Location.Query);
            Assert.Equal("mulse-ui", loginQuery["client_id"].ToString());
            Assert.Equal("code", loginQuery["response_type"].ToString());
            Assert.Equal("S256", loginQuery["code_challenge_method"].ToString());

            using var signin = await client.GetAsync("/test-signin");
            Assert.Equal(HttpStatusCode.OK, signin.StatusCode);
            using var beforeLogout = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, beforeLogout.StatusCode);
            using var logout = await client.PostAsync("/test-signout", null);
            Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
            Assert.Equal("/logout", logout.Headers.Location!.AbsolutePath);
            var query = QueryHelpers.ParseQuery(logout.Headers.Location.Query);
            Assert.Equal("mulse-ui", query["client_id"].ToString());
            Assert.Equal(hasIdToken, query.TryGetValue("id_token_hint", out var idTokenHint));
            Assert.Equal(hasIdToken ? "test-id-token" : "", idTokenHint.ToString());
            Assert.Equal(new Uri(client.BaseAddress!, "/signout-callback-oidc").AbsoluteUri,
                query["post_logout_redirect_uri"].ToString());
            using var afterLogout = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
            Assert.Equal("/auth", afterLogout.Headers.Location!.AbsolutePath);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
