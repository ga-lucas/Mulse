using System.Security.Claims;
using Mulse.ServiceDefaults.Security;

namespace Service.Tests;

public sealed class OidcRoleClaimsTests
{
    [Fact]
    public void AddJsonArrayRoles_MapsConfiguredRolesAndPreservesExistingClaims()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", "user-123"),
            new Claim("realm_access", "{\"roles\":[\"mulse-admin\",\"reader\"]}")
        ], "test");
        var principal = new ClaimsPrincipal(identity);

        OidcRoleClaims.AddJsonArrayRoles(principal, "realm_access", "roles");

        Assert.Equal("user-123", principal.FindFirst("sub")?.Value);
        Assert.Contains(principal.Claims, claim => claim.Type == ClaimTypes.Role && claim.Value == "mulse-admin");
        Assert.Contains(principal.Claims, claim => claim.Type == ClaimTypes.Role && claim.Value == "reader");
    }

    [Fact]
    public void AddJsonArrayRoles_DoesNotDuplicateRoles()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, "mulse-admin"),
            new Claim("realm_access", "{\"roles\":[\"mulse-admin\"]}")
        ], "test");
        var principal = new ClaimsPrincipal(identity);

        OidcRoleClaims.AddJsonArrayRoles(principal, "realm_access", "roles");

        Assert.Single(principal.FindAll(ClaimTypes.Role));
    }
}
