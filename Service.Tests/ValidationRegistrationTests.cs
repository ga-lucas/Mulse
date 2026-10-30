using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Service.Tests;

public sealed class ValidationRegistrationTests
{
    [Fact]
    public async Task SharedRegistration_ValidatesRequestsInEachHost()
    {
        for (var host = 0; host < 2; host++)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            ApiTestValidation.Register(builder.Services);
            await using var app = builder.Build();
            var handled = 0;
            app.MapPost("/validation", (ValidationRequest request) =>
            {
                handled++;
                return TypedResults.Ok(request.Name);
            });
            await app.StartAsync();
            try
            {
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var http = new HttpClient { BaseAddress = new Uri(address) };
                using var invalid = await http.PostAsJsonAsync("/validation", new ValidationRequest(null));
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                var problem = await invalid.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();
                Assert.NotNull(problem);
                Assert.Contains(nameof(ValidationRequest.Name), problem.Errors.Keys);
                Assert.Equal(0, handled);
                using var valid = await http.PostAsJsonAsync("/validation", new ValidationRequest("orders"));
                Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
                Assert.Equal("orders", await valid.Content.ReadFromJsonAsync<string>());
                Assert.Equal(1, handled);
            }
            finally { await app.StopAsync(); }
        }
    }

    public sealed record ValidationRequest([property: Required] string? Name);
}
