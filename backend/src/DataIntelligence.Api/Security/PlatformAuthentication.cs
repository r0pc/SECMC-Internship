using System.Security.Claims;
using System.Text;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DataIntelligence.Api.Security;

/// <summary>
/// Bearer-token authentication (FR-9): how a presented token is validated, and how the caller
/// behind it is read.
/// </summary>
public static class PlatformAuthentication
{
    public static IServiceCollection AddPlatformAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured from AuthOptions rather than from IConfiguration directly, and deliberately
        // deferred: this delegate runs the first time the handler is resolved, which is after
        // AddSecurity's ValidateOnStart has had its say. Building the signing key eagerly here
        // would throw on a missing key during service registration — before startup validation
        // runs — and replace a message naming the setting with an ArgumentNullException.
        services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                var options = auth.Value;

                var signingKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(options.SigningKey));

                // Claims arrive named as they were written. Without this the handler rewrites
                // 'sub' to the WS-Federation nameidentifier URI and 'role' to its equivalent,
                // which means the constant this API signs with and the constant it reads with are
                // different strings — an inconsistency that shows up as an authenticated caller
                // holding no roles.
                jwt.MapInboundClaims = false;

                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidateLifetime = true,

                    // The default is five minutes, which would keep an expired token working for
                    // five more. Both ends of this system are the same machine or two machines
                    // with NTP; 30 seconds covers clock drift without extending anyone's session.
                    ClockSkew = TimeSpan.FromSeconds(30),

                    NameClaimType = JwtAccessTokenIssuer.NameClaim,
                    RoleClaimType = JwtAccessTokenIssuer.RoleClaim
                };

                jwt.Events = new JwtBearerEvents
                {
                    // A signature and an expiry prove the token was ours and is recent. They say
                    // nothing about whether the account still exists, is still enabled, or still
                    // holds the roles printed inside it — a token is a photograph of the moment it
                    // was issued. This is where that photograph is checked against the present.
                    //
                    // One primary-key lookup per request. That is the standing cost of being able
                    // to revoke a token that has not expired, and it is what lets tokens live for
                    // eight hours instead of five minutes with a refresh dance.
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal;

                        if (principal is null
                            || !TryReadUserId(principal, out var userId)
                            || !TryReadSecurityStamp(principal, out var stamp))
                        {
                            context.Fail("The token is missing the claims required to identify it.");

                            return;
                        }

                        var users = context.HttpContext.RequestServices
                            .GetRequiredService<IUserService>();

                        var current = await users.ResolveAsync(
                            userId, stamp, context.HttpContext.RequestAborted);

                        if (current is null)
                        {
                            context.Fail(
                                "The account behind this token has been disabled, or its password "
                                + "or roles changed after the token was issued.");
                        }
                    }
                };
            });

        return services;
    }

    /// <summary>
    /// The signed-in caller's id, from the token's <c>sub</c> claim.
    /// </summary>
    /// <remarks>
    /// Throws rather than returning a default. Every caller of this is inside an endpoint that
    /// authorization has already required a token for, so a missing or unparseable subject is not
    /// an anonymous request — it is a bug in token issuance, and recording questions against user
    /// 0 would hide it in the audit log rather than surface it.
    /// </remarks>
    public static int UserId(this ClaimsPrincipal principal) =>
        TryReadUserId(principal, out var userId)
            ? userId
            : throw new InvalidOperationException(
                "The authenticated principal carries no usable 'sub' claim.");

    private static bool TryReadUserId(ClaimsPrincipal principal, out int userId) =>
        int.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out userId);

    private static bool TryReadSecurityStamp(ClaimsPrincipal principal, out Guid stamp) =>
        Guid.TryParse(
            principal.FindFirstValue(JwtAccessTokenIssuer.SecurityStampClaim), out stamp);
}
