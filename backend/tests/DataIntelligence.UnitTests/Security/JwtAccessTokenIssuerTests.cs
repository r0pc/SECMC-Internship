using System.Text;
using DataIntelligence.Core.Security;
using DataIntelligence.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DataIntelligence.UnitTests.Security;

/// <summary>
/// What goes into an access token, and what a verifier gets out of it (FR-9).
/// </summary>
/// <remarks>
/// The tokens are read back with the same validation parameters the API applies, rather than by
/// decoding the payload as JSON. Decoding would assert what the token says; validating asserts what
/// it is worth, which is the thing that has to be right.
/// </remarks>
public sealed class JwtAccessTokenIssuerTests
{
    private const string SigningKey = "unit-tests-only-signing-key-0123456789abcdef";
    private const string OtherKey = "a-completely-different-key-0123456789abcdef";

    private static readonly DateTimeOffset Now = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly UserPrincipal Analyst = new(
        UserId: 42,
        Email: "analyst@example.test",
        DisplayName: "An Analyst",
        Roles: [PlatformRoles.Analyst],
        SecurityStamp: Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"));

    private static JwtAccessTokenIssuer Issuer(int lifetimeMinutes = 480) =>
        new(Options.Create(new AuthOptions
            {
                SigningKey = SigningKey,
                Issuer = "DataIntelligence.Api",
                Audience = "DataIntelligence.Frontend",
                TokenLifetimeMinutes = lifetimeMinutes
            }),
            new FakeTimeProvider(Now));

    [Fact]
    public async Task IssuesATokenThatValidatesAgainstItsOwnKey()
    {
        var token = Issuer().Issue(Analyst);

        var result = await Validate(token.Value, SigningKey);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task IssuesATokenNothingElseCanForge()
    {
        var token = Issuer().Issue(Analyst);

        // The whole basis of the scheme: change the key and every token minted with the old one
        // becomes unverifiable. It is also the emergency lever for a leaked key.
        var result = await Validate(token.Value, OtherKey);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task CarriesTheUserIdAsSubject()
    {
        var token = Issuer().Issue(Analyst);

        var claims = await ClaimsOf(token.Value);

        Assert.Equal("42", claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
    }

    [Fact]
    public async Task CarriesTheSecurityStampThatMakesItRevocable()
    {
        var token = Issuer().Issue(Analyst);

        var claims = await ClaimsOf(token.Value);

        Assert.Equal(
            Analyst.SecurityStamp.ToString(),
            claims.First(c => c.Type == JwtAccessTokenIssuer.SecurityStampClaim).Value);
    }

    [Fact]
    public async Task CarriesEveryRoleAsItsOwnClaim()
    {
        var administrator = Analyst with
        {
            Roles = [PlatformRoles.Administrator, PlatformRoles.Analyst]
        };

        var claims = await ClaimsOf(Issuer().Issue(administrator).Value);

        var roles = claims
            .Where(c => c.Type == JwtAccessTokenIssuer.RoleClaim)
            .Select(c => c.Value)
            .ToList();

        Assert.Equal([PlatformRoles.Administrator, PlatformRoles.Analyst], roles);
    }

    [Fact]
    public void ExpiresAfterTheConfiguredLifetime()
    {
        var token = Issuer(lifetimeMinutes: 90).Issue(Analyst);

        Assert.Equal(Now.UtcDateTime.AddMinutes(90), token.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReportsTheSameExpiryItWroteIntoTheToken()
    {
        // The frontend expires its session cookie on the reported value. A cookie that outlived
        // the token inside it would produce requests that look signed in and are not.
        var token = Issuer().Issue(Analyst);

        var claims = await ClaimsOf(token.Value);

        var exp = long.Parse(claims.First(c => c.Type == JwtRegisteredClaimNames.Exp).Value);

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime, token.ExpiresAtUtc);
    }

    [Fact]
    public async Task IsRejectedOnceItHasExpired()
    {
        var token = Issuer(lifetimeMinutes: 5).Issue(Analyst);

        var result = await Validate(
            token.Value,
            SigningKey,
            // Past the expiry and past the 30 seconds of clock skew the API allows.
            readAt: Now.UtcDateTime.AddMinutes(10));

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task IsRejectedByAVerifierExpectingADifferentAudience()
    {
        var token = Issuer().Issue(Analyst);

        var result = await Validate(token.Value, SigningKey, audience: "SomeoneElsesFrontend");

        Assert.False(result.IsValid);
    }

    private static async Task<TokenValidationResult> Validate(
        string token,
        string key,
        string audience = "DataIntelligence.Frontend",
        DateTime? readAt = null)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "DataIntelligence.Api",
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        // The handler reads the clock through LifetimeValidator when one is supplied, which is how
        // an expiry is asserted without waiting for it. One is always supplied: the issuer mints
        // against a fixed clock, so a verifier left on the machine clock would start rejecting
        // every token the moment the wall clock walked past Now — a failure of the calendar rather
        // than of the code. Tests asserting an expiry move this instant instead.
        var instant = readAt ?? Now.UtcDateTime;

        parameters.LifetimeValidator = (notBefore, expires, _, _) =>
            (notBefore is null || notBefore <= instant) && (expires is null || expires > instant);

        return await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
    }

    private static async Task<IEnumerable<System.Security.Claims.Claim>> ClaimsOf(string token)
    {
        var result = await Validate(token, SigningKey);

        Assert.True(result.IsValid);

        return result.ClaimsIdentity.Claims;
    }

    /// <summary>A clock that does not move, so an expiry is arithmetic rather than a wait.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
