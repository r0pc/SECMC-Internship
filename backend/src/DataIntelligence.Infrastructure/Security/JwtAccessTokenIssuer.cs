using System.Security.Claims;
using System.Text;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Core.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DataIntelligence.Infrastructure.Security;

/// <summary>
/// Signs the bearer token a caller presents on every request after signing in (FR-9).
/// </summary>
/// <remarks>
/// Symmetric HMAC-SHA256, because there is exactly one issuer and one verifier and they are the
/// same process. Asymmetric keys buy the ability to let something else verify without being able
/// to mint, and nothing here needs that.
/// </remarks>
public sealed class JwtAccessTokenIssuer : IAccessTokenIssuer
{
    /// <summary>
    /// The claim carrying <see cref="UserPrincipal.SecurityStamp"/>.
    /// </summary>
    /// <remarks>
    /// Not a registered claim name, so it is spelled out here and read by the same constant in the
    /// API's token validation. The two are one contract: a rename on one side alone would not fail
    /// to compile, it would silently stop revoking anything.
    /// </remarks>
    public const string SecurityStampClaim = "stamp";

    /// <summary>Role claims, spelled short. See the note on claim naming in <c>Program.cs</c>.</summary>
    public const string RoleClaim = "role";

    /// <summary>The display name claim.</summary>
    public const string NameClaim = "name";

    private readonly AuthOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SigningCredentials _credentials;

    public JwtAccessTokenIssuer(IOptions<AuthOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    public AccessToken Issue(UserPrincipal principal)
    {
        // Whole seconds, because that is the resolution exp and iat have. Rounding down rather
        // than letting the handler truncate keeps the expiry this method reports identical to the
        // one inside the token — the frontend expires its cookie on the value returned here, and
        // a cookie outliving its token would produce requests that look signed in and are not.
        var issuedAt = TruncateToSecond(_timeProvider.GetUtcNow().UtcDateTime);
        var expiresAt = issuedAt.AddMinutes(_options.TokenLifetimeMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, principal.UserId.ToString()),
            new(JwtRegisteredClaimNames.Email, principal.Email),
            new(NameClaim, principal.DisplayName),
            new(SecurityStampClaim, principal.SecurityStamp.ToString()),

            // A unique id per token, so one can be named in a log without quoting the token itself.
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        claims.AddRange(principal.Roles.Select(role => new Claim(RoleClaim, role)));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = expiresAt,
            SigningCredentials = _credentials
        };

        return new AccessToken(new JsonWebTokenHandler().CreateToken(descriptor), expiresAt);
    }

    private static DateTime TruncateToSecond(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Kind);
}
