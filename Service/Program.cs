using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Mulse.ServiceDefaults.Security;
using Service;
using Service.Endpoints;
using Service.Middleware;

var builder = WebApplication.CreateBuilder(args);

var authority = builder.Configuration["Authentication:Authority"]
    ?? throw new InvalidOperationException("Authentication:Authority is required.");
var apiAudience = builder.Configuration["Authentication:ApiAudience"] ?? "mulse-api";
var apiClientId = builder.Configuration["Authentication:ApiClientId"] ?? "mulse-service";
var apiAccessRole = builder.Configuration["Authentication:ApiAccessRole"] ?? "mulse-api-access";
var rolesClaim = builder.Configuration["Authentication:RolesClaim"] ?? "realm_access";
var rolesProperty = builder.Configuration["Authentication:RolesProperty"] ?? "roles";

builder.AddServiceDefaults();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = apiAudience;
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "preferred_username";
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                OidcRoleClaims.AddJsonArrayRoles(context.Principal, rolesClaim, rolesProperty);
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireRole(apiAccessRole)
        .RequireClaim("azp", apiClientId)
        .Build();
});
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddValidation();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddMulsePlatform(builder.Configuration, builder.Environment.ContentRootPath);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapDefaultEndpoints();
app.MapModuleEndpoints();
app.MapFlowEndpoints();
app.MapWorkspaceEndpoints();
app.MapDesignerEndpoints();
app.MapBizTalkMigrationEndpoints();
app.MapConfigValueEndpoints();
app.MapEncryptionEndpoints();
app.MapStorageEndpoints();
app.MapHistoryEndpoints();
app.MapProcessingEndpoints();
app.MapHttpInboundEndpoints();

app.Run();

/// <summary>
/// Exposes the top-level-statement-generated <c>Program</c> class so <c>Service.Tests</c> can host this
/// application in-memory via <c>WebApplicationFactory&lt;Program&gt;</c> for integration tests, without
/// needing to rely on <c>InternalsVisibleTo</c> for the entry point itself.
/// </summary>
public partial class Program;

