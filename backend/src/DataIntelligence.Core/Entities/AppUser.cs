namespace DataIntelligence.Core.Entities;

/// <summary>
/// A person who can sign in (FR-9). Mirrors <c>sec.AppUser</c> in
/// <c>docs/database-schema.sql</c>.
/// </summary>
/// <remarks>
/// Accounts are created by an administrator rather than by self-registration: this is an internal
/// platform, and anyone reaching the login page is not thereby entitled to an account on it.
/// <para>
/// Deactivating is the deletion story. A user's questions are audit records — <c>ai.AssistantSession</c>
/// carries a foreign key to this table — so removing the row would either take that history with it
/// or be refused by the constraint. <see cref="IsActive"/> ends the access without ending the record
/// of what was asked.
/// </para>
/// </remarks>
public class AppUser
{
    /// <summary>
    /// The id the assistant recorded against every question asked before FR-9 existed.
    /// </summary>
    /// <remarks>
    /// <c>AssistantEndpoints</c> hard-coded <c>userId: 1</c>, and <c>docs/database-schema.sql</c>
    /// seeded a placeholder row for it to point at. Named here because the migration that adds the
    /// foreign key has to account for that history rather than orphan it — see
    /// <c>AuthenticationTables</c>.
    /// </remarks>
    public const int PlaceholderUserId = 1;

    public int UserId { get; set; }

    /// <summary>The login. Unique, and compared case-insensitively by SQL Server's collation.</summary>
    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// ASP.NET Identity v3 format — base64 of (format marker, PBKDF2 parameters, salt, subkey).
    /// </summary>
    /// <remarks>
    /// Produced and verified by <c>Microsoft.AspNetCore.Identity.PasswordHasher&lt;T&gt;</c> rather
    /// than by anything written here. Rolling our own is the one thing in this file that could be
    /// wrong in a way no test would notice.
    /// </remarks>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// Rotated whenever the password changes or the account is disabled, so tokens issued before
    /// that stop being accepted.
    /// </summary>
    /// <remarks>
    /// The one piece of revocation a stateless token has. An access token is valid until it expires
    /// and nothing can call it back, so the stamp is written into the token and re-checked against
    /// this column on every request: change the password and every session opened with the old one
    /// is dead on its next call, rather than good for the rest of its lifetime.
    /// </remarks>
    public Guid SecurityStamp { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtPkt { get; set; }

    /// <summary>Null until the first successful sign-in.</summary>
    public DateTime? LastLoginAtPkt { get; set; }

    public ICollection<UserRole> Roles { get; set; } = [];
}
