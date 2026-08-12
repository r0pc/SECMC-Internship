using DataIntelligence.Core.Security;

namespace DataIntelligence.Core.Interfaces;

/// <summary>An issued access token and the moment it stops being accepted.</summary>
public sealed record AccessToken(string Value, DateTime ExpiresAtUtc);

/// <summary>Mints the bearer token a signed-in caller presents on every subsequent request.</summary>
public interface IAccessTokenIssuer
{
    AccessToken Issue(UserPrincipal principal);
}
