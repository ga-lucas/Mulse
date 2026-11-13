using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Mulse.Ui.Services;

public static class AccessDeniedResponse
{
    public static Task WriteAsync(RedirectContext<CookieAuthenticationOptions> context)
    {
        var tokens = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>()
            .GetAndStoreTokens(context.HttpContext);
        var role = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()
            ["Authentication:AdminRole"] ?? "mulse-admin";
        var encoder = HtmlEncoder.Default;
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync($"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><title>Mulse - Access denied</title></head>
            <body>
                <h1>Access denied</h1>
                <p>You are signed in, but this session does not contain the required {encoder.Encode(role)} role.</p>
                <p>Ask your administrator to check your assigned role and the identity provider's ID-token role mapping.
                Sign out and sign in again after correcting the configuration.</p>
                <form method="post" action="/auth/logout">
                    <input type="hidden" name="{encoder.Encode(tokens.FormFieldName)}" value="{encoder.Encode(tokens.RequestToken!)}">
                    <button type="submit">Sign out</button>
                </form>
            </body>
            </html>
            """, context.HttpContext.RequestAborted);
    }
}
