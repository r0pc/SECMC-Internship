using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Security;

namespace DataIntelligence.Core.Interfaces;

/// <summary>
/// Accounts and sign-in (FR-9): the one place a password is verified, hashed or replaced.
/// </summary>
public interface IUserService
{
    /// <summary>
    /// Verifies credentials and, on success, stamps the sign-in time.
    /// </summary>
    /// <remarks>
    /// The hash is verified even when the email matches nothing. Skipping the work for an unknown
    /// address makes the two cases take measurably different times, which turns this endpoint into
    /// a way to enumerate who has an account here.
    /// </remarks>
    Task<SignInResult> SignInAsync(LoginRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Re-reads the account behind a presented token, returning null when it has been disabled,
    /// deleted, or had its security stamp rotated since the token was issued.
    /// </summary>
    /// <remarks>
    /// Called on every authenticated request. That is the price of being able to revoke a token
    /// that has not expired, and it is one indexed lookup on the primary key.
    /// </remarks>
    Task<UserPrincipal?> ResolveAsync(
        int userId, Guid securityStamp, CancellationToken cancellationToken);

    /// <summary>Every account, oldest first. Small by construction — this is an internal platform.</summary>
    Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken cancellationToken);

    Task<UserDto?> GetAsync(int userId, CancellationToken cancellationToken);

    /// <summary>Creates an account. Conflicts when the email is already taken.</summary>
    Task<WriteResult<UserDto>> CreateAsync(
        CreateUserRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Edits display name, roles and active state.
    /// </summary>
    /// <param name="userId">The account being edited.</param>
    /// <param name="request">The fields to change; omitted ones are left alone.</param>
    /// <param name="cancellationToken">Cancels the work with the request that asked for it.</param>
    /// <param name="actingUserId">
    /// The administrator making the change. Used to refuse the two edits that would lock
    /// administration out of the platform: disabling or demoting yourself, and removing the last
    /// remaining active administrator.
    /// </param>
    Task<WriteResult<UserDto>> UpdateAsync(
        int userId,
        UpdateUserRequest request,
        int actingUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sets a password on someone else's behalf, rotating their security stamp so any session they
    /// had open stops working.
    /// </summary>
    Task<WriteResult<UserDto>> ResetPasswordAsync(
        int userId, string newPassword, CancellationToken cancellationToken);

    /// <summary>Changes the caller's own password, which requires the current one.</summary>
    Task<PasswordChangeOutcome> ChangePasswordAsync(
        int userId, ChangePasswordRequest request, CancellationToken cancellationToken);
}
