using System.ComponentModel.DataAnnotations;

namespace DataIntelligence.Core.Dtos;

/// <summary>Credentials presented at <c>/api/auth/login</c>.</summary>
public sealed record LoginRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [MaxLength(PasswordRules.MaxLength)]
    public string Password { get; init; } = string.Empty;
}

/// <summary>
/// What the caller gets for a correct password: the token, when it dies, and who they are.
/// </summary>
/// <remarks>
/// The profile is returned with the token rather than left to a follow-up <c>/auth/me</c> call.
/// The frontend needs the display name and roles to draw its first page, and a second round trip
/// to learn what it was just told is a round trip on every sign-in.
/// </remarks>
public sealed record LoginResponse
{
    public required string AccessToken { get; init; }

    /// <summary>UTC, and the moment the API stops accepting the token — not a suggestion.</summary>
    public required DateTime ExpiresAtUtc { get; init; }

    public required AuthenticatedUserDto User { get; init; }
}

/// <summary>The signed-in caller, as the API sees them.</summary>
public sealed record AuthenticatedUserDto
{
    public required int UserId { get; init; }

    public required string Email { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Role names, in the order of <c>PlatformRoles.All</c>.</summary>
    public required IReadOnlyList<string> Roles { get; init; }
}

/// <summary>One account, for the administrator's user list.</summary>
public sealed record UserDto
{
    public required int UserId { get; init; }

    public required string Email { get; init; }

    public required string DisplayName { get; init; }

    public required IReadOnlyList<string> Roles { get; init; }

    public required bool IsActive { get; init; }

    public required DateTime CreatedAtPkt { get; init; }

    public DateTime? LastLoginAtPkt { get; init; }
}

/// <summary>A new account, created by an administrator (there is no self-registration).</summary>
public sealed record CreateUserRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [MaxLength(150)]
    public string DisplayName { get; init; } = string.Empty;

    [Required]
    [MinLength(PasswordRules.MinLength)]
    [MaxLength(PasswordRules.MaxLength)]
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Role names. Empty grants Viewer — the least this platform has, and the safe reading of an
    /// omitted field.
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = [];
}

/// <summary>
/// An edit to an existing account. Omitted fields are left unchanged; the email is not editable,
/// because it is the login and changing it silently would lock someone out of their own account.
/// </summary>
public sealed record UpdateUserRequest
{
    [MaxLength(150)]
    public string? DisplayName { get; init; }

    public IReadOnlyList<string>? Roles { get; init; }

    public bool? IsActive { get; init; }
}

/// <summary>An administrator setting someone else's password, having been asked to.</summary>
public sealed record ResetPasswordRequest
{
    [Required]
    [MinLength(PasswordRules.MinLength)]
    [MaxLength(PasswordRules.MaxLength)]
    public string NewPassword { get; init; } = string.Empty;
}

/// <summary>A user changing their own password, which requires proving they know the old one.</summary>
public sealed record ChangePasswordRequest
{
    [Required]
    [MaxLength(PasswordRules.MaxLength)]
    public string CurrentPassword { get; init; } = string.Empty;

    [Required]
    [MinLength(PasswordRules.MinLength)]
    [MaxLength(PasswordRules.MaxLength)]
    public string NewPassword { get; init; } = string.Empty;
}

/// <summary>
/// The length bounds on a password, in one place so the API, its OpenAPI document and the login
/// form cannot drift apart.
/// </summary>
/// <remarks>
/// Length only. Composition rules ("one digit, one symbol") push people towards
/// <c>Password1!</c> — short, predictable, and weaker than the passphrase the rule was meant to
/// force — so the floor is raised instead and nothing is mandated about what goes in it. The
/// ceiling exists because the hash is computed over whatever arrives, and an unbounded password is
/// an unbounded amount of PBKDF2 work for one unauthenticated request.
/// </remarks>
public static class PasswordRules
{
    public const int MinLength = 12;

    public const int MaxLength = 256;
}
