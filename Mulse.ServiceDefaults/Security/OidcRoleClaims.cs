using System.Security.Claims;
using System.Text.Json;

namespace Mulse.ServiceDefaults.Security;

public static class OidcRoleClaims
{
    public static void AddJsonArrayRoles(ClaimsPrincipal? principal, string sourceClaimType, string rolesProperty)
    {
        if (principal is null) return;

        foreach (var identity in principal.Identities)
        {
            foreach (var sourceClaim in identity.FindAll(sourceClaimType).ToArray())
            {
                using var document = JsonDocument.Parse(sourceClaim.Value);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty(rolesProperty, out var roles)
                    || roles.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var role in roles.EnumerateArray())
                {
                    if (role.ValueKind != JsonValueKind.String) continue;
                    var roleName = role.GetString();
                    if (!string.IsNullOrWhiteSpace(roleName) && !identity.HasClaim(ClaimTypes.Role, roleName))
                    {
                        identity.AddClaim(new Claim(ClaimTypes.Role, roleName));
                    }
                }
            }
        }
    }
}
