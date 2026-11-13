using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Mulse.Ui.Services;

public sealed class MulseApiAccessTokenProvider(IHttpClientFactory httpClientFactory, IConfiguration configuration, IHostEnvironment environment)
{
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt;
    private string? _tokenEndpoint;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && _expiresAt > DateTimeOffset.UtcNow)
        {
            return _accessToken;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && _expiresAt > DateTimeOffset.UtcNow)
            {
                return _accessToken;
            }

            var authority = configuration["Authentication:Authority"]
                ?? throw new InvalidOperationException("Authentication:Authority is required to acquire a Mulse API access token.");
            var clientId = configuration["MulseApi:ClientId"]
                ?? throw new InvalidOperationException("MulseApi:ClientId is required to acquire a Mulse API access token.");
            var clientSecret = configuration["MulseApi:ClientSecret"]
                ?? throw new InvalidOperationException("MulseApi:ClientSecret is required. Store it in user secrets or a production secret store.");

            _tokenEndpoint ??= await DiscoverTokenEndpointAsync(authority, cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials"
                })
            };
            var clientCredentials = $"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(clientSecret)}";
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(clientCredentials)));

            using var response = await httpClientFactory.CreateClient("oidc-token")
                .SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The identity provider returned an empty access-token response.");
            if (string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresIn <= 0)
            {
                throw new InvalidOperationException("The identity provider returned an invalid access-token response.");
            }

            _accessToken = token.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, token.ExpiresIn - 30));
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<string> DiscoverTokenEndpointAsync(string authority, CancellationToken cancellationToken)
    {
        var discoveryUri = $"{authority.TrimEnd('/')}/.well-known/openid-configuration";
        var metadata = await httpClientFactory.CreateClient("oidc-token")
            .GetFromJsonAsync<OpenIdProviderMetadata>(discoveryUri, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The identity provider returned empty OpenID Connect metadata.");

        if (!Uri.TryCreate(metadata.TokenEndpoint, UriKind.Absolute, out var tokenEndpoint)
            || (tokenEndpoint.Scheme != Uri.UriSchemeHttps && !(environment.IsDevelopment() && tokenEndpoint.IsLoopback)))
        {
            throw new InvalidOperationException("The identity provider token endpoint must use HTTPS outside local development.");
        }

        return tokenEndpoint.AbsoluteUri;
    }

    private sealed record OpenIdProviderMetadata([property: JsonPropertyName("token_endpoint")] string TokenEndpoint);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
