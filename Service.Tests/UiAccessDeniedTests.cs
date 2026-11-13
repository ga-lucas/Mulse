using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class UiAccessDeniedTests
{
    [Theory]
    [InlineData("reader", HttpStatusCode.Forbidden)]
    [InlineData("mulse-admin", HttpStatusCode.OK)]
    public async Task CookieAuthorization_DeniesWithoutRedirectLoopAndPreservesAdminAccess(string role, HttpStatusCode expectedStatus)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => options.Events.OnRedirectToAccessDenied = AccessDeniedResponse.WriteAsync);
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("MulseAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("mulse-admin"));

        await using var app = builder.Build();
        app.UseStatusCodePagesWithReExecute("/not-found");
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/test-signin", async (HttpContext context) =>
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "test-user"), new Claim(ClaimTypes.Role, role)],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            await context.Response.WriteAsync("Signed in");
        });
        app.MapGet("/", () => "Administrator workspace").RequireAuthorization("MulseAdmin");
        app.MapGet("/not-found", () => "Not found").RequireAuthorization("MulseAdmin");
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(address) };
            using var anonymous = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);

            using var signIn = await client.GetAsync("/test-signin");
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
            using var response = await client.GetAsync("/");
            Assert.Equal(expectedStatus, response.StatusCode);
            Assert.Null(response.Headers.Location);
            var body = await response.Content.ReadAsStringAsync();
            if (expectedStatus == HttpStatusCode.Forbidden)
            {
                Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.Contains("Access denied", body);
                Assert.Contains("mulse-admin", body);
                Assert.Contains("action=\"/auth/logout\"", body);
                Assert.Contains("__RequestVerificationToken", body);
                Assert.DoesNotContain("Administrator workspace", body);
            }
            else
            {
                Assert.Equal("Administrator workspace", body);
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
