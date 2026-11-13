using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Mulse.ServiceDefaults.Security;
using Mulse.Ui.Components;
using Mulse.Ui.Services;

var builder = WebApplication.CreateBuilder(args);

var authority = builder.Configuration["Authentication:Authority"]
    ?? throw new InvalidOperationException("Authentication:Authority is required.");
var clientId = builder.Configuration["Authentication:ClientId"]
    ?? throw new InvalidOperationException("Authentication:ClientId is required.");
var clientSecret = builder.Configuration["Authentication:ClientSecret"]
    ?? throw new InvalidOperationException("Authentication:ClientSecret is required. Store it in user secrets or a production secret store.");
var adminRole = builder.Configuration["Authentication:AdminRole"] ?? "mulse-admin";
var rolesClaim = builder.Configuration["Authentication:RolesClaim"] ?? "realm_access";
var rolesProperty = builder.Configuration["Authentication:RolesProperty"] ?? "roles";

builder.AddServiceDefaults();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.SlidingExpiration = true;
    options.Events.OnRedirectToAccessDenied = AccessDeniedResponse.WriteAsync;
})
.AddOpenIdConnect(options =>
{
    options.Authority = authority;
    options.ClientId = clientId;
    options.ClientSecret = clientSecret;
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    options.ResponseType = OpenIdConnectResponseType.Code;
    options.UsePkce = true;
    OidcSession.Configure(options);
    options.MapInboundClaims = false;
    options.TokenValidationParameters.NameClaimType = "preferred_username";
    options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
    options.Events.OnTokenValidated = context =>
    {
        OidcRoleClaims.AddJsonArrayRoles(context.Principal, rolesClaim, rolesProperty);
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("MulseAdmin", policy => policy.RequireAuthenticatedUser().RequireRole(adminRole));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpClient("oidc-token");
builder.Services.AddSingleton<MulseApiAccessTokenProvider>();
builder.Services.AddTransient<MulseApiAccessTokenHandler>();
builder.Services.AddHttpClient<IMulseApiClient, MulseApiClient>((serviceProvider, httpClient) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var baseUrl = configuration["MulseApi:BaseUrl"] ?? "http://localhost:5210";
    httpClient.BaseAddress = new Uri(baseUrl);
})
.AddHttpMessageHandler<MulseApiAccessTokenHandler>();
var migrationClient = builder.Services.AddHttpClient("mulse-storage-migration", (serviceProvider, httpClient) =>
{
    httpClient.BaseAddress = new Uri(serviceProvider.GetRequiredService<IConfiguration>()["MulseApi:BaseUrl"] ?? "http://localhost:5210");
    httpClient.Timeout = TimeSpan.FromMinutes(30);
});
#pragma warning disable EXTEXP0001 // Migration must not inherit automatic retries or short default resilience timeouts.
migrationClient.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
migrationClient.AddHttpMessageHandler<MulseApiAccessTokenHandler>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/auth/login", () => Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/" },
        [OpenIdConnectDefaults.AuthenticationScheme]))
    .AllowAnonymous();
app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
    {
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
    })
    .RequireAuthorization();

app.MapDefaultEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization("MulseAdmin");

app.Run();
