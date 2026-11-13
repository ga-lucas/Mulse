using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Mulse.Ui.Services;

public static class OidcSession
{
    public static void Configure(OpenIdConnectOptions options)
    {
        options.SaveTokens = true;
        options.Events.OnTicketReceived = context =>
        {
            var properties = context.Properties ??= new AuthenticationProperties();
            // Logout needs the ID token; downstream API access uses a separate service identity.
            var idToken = properties.GetTokenValue("id_token");
            properties.StoreTokens(string.IsNullOrEmpty(idToken)
                ? []
                : [new AuthenticationToken { Name = "id_token", Value = idToken }]);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            // Older sessions have no saved ID token. Keycloak can identify the client instead.
            context.ProtocolMessage.ClientId = context.Options.ClientId;
            return Task.CompletedTask;
        };
    }
}
