using System.Security.Claims;
using System.Text.Encodings.Web;
using LabFlow.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LabFlow.Api.Auth;

/// <summary>
/// Development-only sign-in: the caller names a sec.AppUser by its ExternalId in the
/// X-Demo-User header and is signed in as that user with its role. This lets the React app
/// switch between Reception and Patient without an identity provider. Program.cs refuses to
/// register it outside Development; production uses Entra ID / ASP.NET Core Identity.
/// </summary>
public class DemoAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    LabFlowDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Demo";
    public const string Header = "X-Demo-User";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var externalId = Request.Headers[Header].ToString();
        if (string.IsNullOrEmpty(externalId))
            return AuthenticateResult.NoResult();

        var user = await db.AppUsers.AsNoTracking()
            .SingleOrDefaultAsync(u => u.ExternalId == externalId && u.IsActive, Context.RequestAborted);
        if (user is null)
            return AuthenticateResult.Fail("Unknown demo user.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role),
        };
        if (user.PatientId is int patientId)
            claims.Add(new Claim(LabFlowClaims.PatientId, patientId.ToString()));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

public static class LabFlowClaims
{
    public const string PatientId = "labflow:patient_id";

    public static int? UserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

public static class Policies
{
    public const string FrontDesk = nameof(FrontDesk);   // Reception, Collector, Admin
}
