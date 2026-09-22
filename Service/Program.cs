using Service;
using Service.Endpoints;
using Service.Middleware;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapDefaultEndpoints();
app.MapModuleEndpoints();
app.MapFlowEndpoints();
app.MapDesignerEndpoints();
app.MapBizTalkMigrationEndpoints();
app.MapConfigValueEndpoints();
app.MapHttpInboundEndpoints();

app.Run();

/// <summary>
/// Exposes the top-level-statement-generated <c>Program</c> class so <c>Service.Tests</c> can host this
/// application in-memory via <c>WebApplicationFactory&lt;Program&gt;</c> for integration tests, without
/// needing to rely on <c>InternalsVisibleTo</c> for the entry point itself.
/// </summary>
public partial class Program;

