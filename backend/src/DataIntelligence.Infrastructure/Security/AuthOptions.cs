using System.ComponentModel.DataAnnotations;

namespace DataIntelligence.Infrastructure.Security;

/// <summary>
/// The <c>Auth</c> configuration section: how access tokens are signed, how long they live, and
/// the account the platform bootstraps with.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// The HMAC key every token is signed with. Supplied outside source control (SOW 3 — Security).
    /// </summary>
    /// <remarks>
    /// The minimum length is not a style preference: HMAC-SHA256 keys shorter than 32 bytes are
    /// rejected outright by <c>Microsoft.IdentityModel</c>, so a shorter key would fail at the
    /// first sign-in rather than here.
    /// <para>
    /// Changing it signs everyone out, which is the emergency lever: a leaked key is replaced and
    /// every token minted with it becomes unverifiable on the next request.
    /// </para>
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Written to the token as <c>iss</c> and required to match when one is presented.</summary>
    public string Issuer { get; set; } = "DataIntelligence.Api";

    /// <summary>Written as <c>aud</c>. One audience: this platform's own frontend.</summary>
    public string Audience { get; set; } = "DataIntelligence.Frontend";

    /// <summary>
    /// How long a token is accepted for, in minutes. Eight hours — a working day, so an analyst
    /// signs in once in the morning rather than being interrupted mid-investigation.
    /// </summary>
    /// <remarks>
    /// Long-lived tokens are usually a bad trade, and are affordable here only because they are
    /// revocable: every request re-reads the account and compares its security stamp, so disabling
    /// someone or changing their password ends their session on the next call rather than eight
    /// hours later. Without that check this number would have to be minutes, and there would have
    /// to be refresh tokens.
    /// </remarks>
    [Range(5, 1440)]
    public int TokenLifetimeMinutes { get; set; } = 480;

    /// <summary>
    /// The first administrator, created at startup when the platform has no accounts at all.
    /// </summary>
    /// <remarks>
    /// Null once the platform is running: it exists to solve the bootstrap problem — accounts are
    /// created by administrators, and the first administrator has nobody to create them.
    /// </remarks>
    public SeedAdministratorOptions? SeedAdministrator { get; set; }
}

/// <summary>Credentials for the bootstrap administrator. Never committed.</summary>
public sealed class SeedAdministratorOptions
{
    [Required(AllowEmptyStrings = false)]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [MaxLength(150)]
    public string DisplayName { get; set; } = "Administrator";

    [Required(AllowEmptyStrings = false)]
    [MinLength(Core.Dtos.PasswordRules.MinLength)]
    [MaxLength(Core.Dtos.PasswordRules.MaxLength)]
    public string Password { get; set; } = string.Empty;
}
