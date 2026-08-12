using DataIntelligence.Core;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Core.Security;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// Identity ships a SignInResult of its own, and this file uses Identity's password hasher. The
// alias keeps the platform's own outcome type unambiguous rather than renaming it to dodge a clash.
using SignInResult = DataIntelligence.Core.Security.SignInResult;

namespace DataIntelligence.Infrastructure.Security;

/// <summary>
/// Accounts and sign-in over <c>sec.AppUser</c> (FR-9).
/// </summary>
/// <remarks>
/// Passwords are hashed by <see cref="IPasswordHasher{TUser}"/> — ASP.NET Identity's PBKDF2
/// implementation, in the v3 format <c>docs/database-schema.sql</c> specifies for the column.
/// Nothing here computes a hash itself.
/// </remarks>
public sealed class UserService : IUserService
{
    /// <summary>
    /// A hash of a password nobody has, verified against when the email matches no account.
    /// </summary>
    /// <remarks>
    /// Without it, a sign-in for an unknown address returns as soon as the lookup misses while a
    /// known one pays for PBKDF2 first — a difference large enough to measure over the network,
    /// and enough to turn the login endpoint into a way to ask whether a given person has an
    /// account here. Computed once per process, on first use.
    /// </remarks>
    private static readonly Lazy<string> TimingEqualisationHash = new(() =>
        new PasswordHasher<AppUser>().HashPassword(
            new AppUser(), "not-a-real-password-only-here-to-cost-the-same"));

    private readonly DataIntelligenceDbContext _db;
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UserService> _logger;

    public UserService(
        DataIntelligenceDbContext db,
        IPasswordHasher<AppUser> hasher,
        TimeProvider timeProvider,
        ILogger<UserService> logger)
    {
        _db = db;
        _hasher = hasher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<SignInResult> SignInAsync(
        LoginRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();

        var user = await _db.Users
            .Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (user is null)
        {
            // Same work, same shape, no account. See TimingEqualisationHash.
            _hasher.VerifyHashedPassword(new AppUser(), TimingEqualisationHash.Value, request.Password);

            _logger.LogInformation("Sign-in refused: no account for {Email}.", email);

            return SignInResult.InvalidCredentials;
        }

        var verification = Verify(user, user.PasswordHash, request.Password);

        if (verification == PasswordVerificationResult.Failed)
        {
            _logger.LogInformation("Sign-in refused: wrong password for user {UserId}.", user.UserId);

            return SignInResult.InvalidCredentials;
        }

        // Checked after the password, not before. Answering "that account is disabled" to someone
        // who has not proved they own it tells a stranger the address is registered here.
        if (!user.IsActive)
        {
            _logger.LogWarning(
                "Sign-in refused: user {UserId} is deactivated but presented a valid password.",
                user.UserId);

            return SignInResult.Deactivated;
        }

        // The hash was made with older parameters than the current library uses. Rewriting it now
        // is the only moment the plaintext is available to do it with.
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, request.Password);

            _logger.LogInformation("Rehashed the password of user {UserId}.", user.UserId);
        }

        user.LastLoginAtPkt = PakistanTime.Now(_timeProvider);

        await _db.SaveChangesAsync(cancellationToken);

        return SignInResult.Success(ToPrincipal(user));
    }

    public async Task<UserPrincipal?> ResolveAsync(
        int userId, Guid securityStamp, CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .AsNoTracking()
            .Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.UserId == userId, cancellationToken);

        // Any of these means the token outlived what it was issued against: the account is gone,
        // it has been disabled, or its stamp was rotated by a password change or a role edit.
        if (user is null || !user.IsActive || user.SecurityStamp != securityStamp)
        {
            return null;
        }

        return ToPrincipal(user);
    }

    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken cancellationToken)
    {
        // Materialised before mapping: ToDto walks the loaded role collection, which is a thing
        // objects can do and a thing SQL cannot be asked to.
        var users = await _db.Users
            .AsNoTracking()
            .Include(u => u.Roles)
            .OrderBy(u => u.UserId)
            .ToListAsync(cancellationToken);

        return users.Select(ToDto).ToList();
    }

    public async Task<UserDto?> GetAsync(int userId, CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .AsNoTracking()
            .Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.UserId == userId, cancellationToken);

        return user is null ? null : ToDto(user);
    }

    public async Task<WriteResult<UserDto>> CreateAsync(
        CreateUserRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();

        if (!TryResolveRoles(request.Roles, out var roleIds, out var roleError))
        {
            return WriteResult<UserDto>.InvalidReference(roleError);
        }

        if (await _db.Users.AnyAsync(u => u.Email == email, cancellationToken))
        {
            return WriteResult<UserDto>.Conflict($"An account already exists for {email}.");
        }

        var user = new AppUser
        {
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            SecurityStamp = Guid.NewGuid(),
            IsActive = true,
            CreatedAtPkt = PakistanTime.Now(_timeProvider)
        };

        user.PasswordHash = _hasher.HashPassword(user, request.Password);

        foreach (var roleId in roleIds)
        {
            user.Roles.Add(new UserRole
            {
                RoleId = roleId,
                GrantedAtPkt = PakistanTime.Now(_timeProvider)
            });
        }

        _db.Users.Add(user);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The AnyAsync above is a check, not a lock. Two administrators creating the same
            // address at once is rare and the unique index is what actually decides it.
            return WriteResult<UserDto>.Conflict($"An account already exists for {email}.");
        }

        _logger.LogInformation("Created user {UserId} ({Email}).", user.UserId, user.Email);

        return WriteResult<UserDto>.Success(ToDto(user));
    }

    public async Task<WriteResult<UserDto>> UpdateAsync(
        int userId,
        UpdateUserRequest request,
        int actingUserId,
        CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.UserId == userId, cancellationToken);

        if (user is null)
        {
            return WriteResult<UserDto>.NotFound($"No user with id {userId}.");
        }

        var deactivating = request.IsActive == false && user.IsActive;

        List<byte>? newRoleIds = null;

        if (request.Roles is not null)
        {
            if (!TryResolveRoles(request.Roles, out var resolved, out var roleError))
            {
                return WriteResult<UserDto>.InvalidReference(roleError);
            }

            newRoleIds = resolved;
        }

        var losingAdministrator =
            user.Roles.Any(r => r.RoleId == PlatformRoles.AdministratorId)
            && newRoleIds is not null
            && !newRoleIds.Contains(PlatformRoles.AdministratorId);

        // Two edits an administrator is not allowed to make to their own account, because both
        // take effect on their very next request and there is no undo from the outside.
        if (userId == actingUserId && deactivating)
        {
            return WriteResult<UserDto>.Conflict(
                "You cannot deactivate your own account. Ask another administrator to do it.");
        }

        if (userId == actingUserId && losingAdministrator)
        {
            return WriteResult<UserDto>.Conflict(
                "You cannot remove your own Administrator role. Ask another administrator to do it.");
        }

        // The platform must keep one way in. Without this, disabling the last administrator leaves
        // an installation nobody can administer and no endpoint that can fix it.
        if (deactivating || losingAdministrator)
        {
            var otherAdministrators = await _db.Users.CountAsync(
                u => u.UserId != userId
                     && u.IsActive
                     && u.Roles.Any(r => r.RoleId == PlatformRoles.AdministratorId),
                cancellationToken);

            if (otherAdministrators == 0
                && user.Roles.Any(r => r.RoleId == PlatformRoles.AdministratorId))
            {
                return WriteResult<UserDto>.Conflict(
                    "This is the last active administrator. Grant the Administrator role to "
                    + "someone else first.");
            }
        }

        if (request.DisplayName is not null)
        {
            user.DisplayName = request.DisplayName.Trim();
        }

        if (request.IsActive is { } isActive)
        {
            user.IsActive = isActive;
        }

        var rolesChanged = false;

        if (newRoleIds is not null)
        {
            var current = user.Roles.Select(r => r.RoleId).OrderBy(id => id).ToList();
            var wanted = newRoleIds.OrderBy(id => id).ToList();

            rolesChanged = !current.SequenceEqual(wanted);

            if (rolesChanged)
            {
                var granted = PakistanTime.Now(_timeProvider);

                foreach (var removed in user.Roles.Where(r => !wanted.Contains(r.RoleId)).ToList())
                {
                    user.Roles.Remove(removed);
                }

                foreach (var added in wanted.Where(id => !current.Contains(id)))
                {
                    user.Roles.Add(new UserRole { RoleId = added, GrantedAtPkt = granted });
                }
            }
        }

        // Roles ride in the token, so a demotion that left the stamp alone would leave the demoted
        // user holding their old permissions until it expired. Deactivation is the same argument
        // with a sharper edge.
        if (rolesChanged || deactivating)
        {
            user.SecurityStamp = Guid.NewGuid();

            _logger.LogInformation(
                "Rotated the security stamp of user {UserId}: roles or active state changed.",
                user.UserId);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return WriteResult<UserDto>.Success(ToDto(user));
    }

    public async Task<WriteResult<UserDto>> ResetPasswordAsync(
        int userId, string newPassword, CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.UserId == userId, cancellationToken);

        if (user is null)
        {
            return WriteResult<UserDto>.NotFound($"No user with id {userId}.");
        }

        user.PasswordHash = _hasher.HashPassword(user, newPassword);
        user.SecurityStamp = Guid.NewGuid();

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogWarning("An administrator reset the password of user {UserId}.", user.UserId);

        return WriteResult<UserDto>.Success(ToDto(user));
    }

    public async Task<PasswordChangeOutcome> ChangePasswordAsync(
        int userId, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId, cancellationToken);

        if (user is null)
        {
            return PasswordChangeOutcome.NotFound;
        }

        var verification = Verify(user, user.PasswordHash, request.CurrentPassword);

        if (verification == PasswordVerificationResult.Failed)
        {
            _logger.LogInformation(
                "Password change refused for user {UserId}: current password did not verify.",
                user.UserId);

            return PasswordChangeOutcome.IncorrectPassword;
        }

        user.PasswordHash = _hasher.HashPassword(user, request.NewPassword);

        // Ends every session opened with the old password, including the one making this request.
        // The alternative is a password change that leaves the compromised session it was probably
        // made in response to still working.
        user.SecurityStamp = Guid.NewGuid();

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {UserId} changed their password.", user.UserId);

        return PasswordChangeOutcome.Success;
    }

    /// <summary>
    /// Verifies a password, treating a stored value that is not a hash at all as a failure.
    /// </summary>
    /// <remarks>
    /// <see cref="PasswordHasher{TUser}"/> base64-decodes the stored string before doing anything
    /// with it, so a column holding a marker rather than a hash makes it throw
    /// <see cref="FormatException"/> instead of returning
    /// <see cref="PasswordVerificationResult.Failed"/>.
    /// <para>
    /// Those markers exist on purpose. <c>docs/database-schema.sql</c> seeds a placeholder account
    /// whose <c>PasswordHash</c> is deliberately undecodable so that no password can ever verify
    /// against it, and the FR-9 migration writes the same kind of row for user ids inherited from
    /// the assistant's pre-authentication history. Without this, an attempt to sign in as one of
    /// them would be answered with a 500 — an unhandled exception reported as a server fault, when
    /// the correct answer is the one every other bad credential gets.
    /// </para>
    /// </remarks>
    private PasswordVerificationResult Verify(AppUser user, string hash, string password)
    {
        try
        {
            return _hasher.VerifyHashedPassword(user, hash, password);
        }
        catch (FormatException)
        {
            _logger.LogWarning(
                "User {UserId} has a PasswordHash that is not a valid hash. Treating it as a "
                + "no-login account, which is what such a value means.",
                user.UserId);

            return PasswordVerificationResult.Failed;
        }
    }

    /// <summary>
    /// Maps role names to ids, defaulting an empty list to Viewer and rejecting anything that is
    /// not one of the three.
    /// </summary>
    private static bool TryResolveRoles(
        IReadOnlyList<string> names, out List<byte> roleIds, out string error)
    {
        roleIds = [];
        error = string.Empty;

        foreach (var name in names)
        {
            var roleId = PlatformRoles.IdFor(name);

            if (roleId is null)
            {
                error =
                    $"'{name}' is not a role. Valid roles are "
                    + $"{string.Join(", ", PlatformRoles.All)}.";

                return false;
            }

            if (!roleIds.Contains(roleId.Value))
            {
                roleIds.Add(roleId.Value);
            }
        }

        if (roleIds.Count == 0)
        {
            roleIds.Add(PlatformRoles.ViewerId);
        }

        return true;
    }

    /// <summary>SQL Server's unique-constraint and unique-index violation numbers.</summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };

    private static UserPrincipal ToPrincipal(AppUser user) =>
        new(user.UserId, user.Email, user.DisplayName, RoleNames(user), user.SecurityStamp);

    private static UserDto ToDto(AppUser user) => new()
    {
        UserId = user.UserId,
        Email = user.Email,
        DisplayName = user.DisplayName,
        Roles = RoleNames(user),
        IsActive = user.IsActive,
        CreatedAtPkt = user.CreatedAtPkt,
        LastLoginAtPkt = user.LastLoginAtPkt
    };

    /// <summary>
    /// Role names, most privileged first — the ids ascend in that order, so ordering by id is the
    /// same thing and does not depend on the strings.
    /// </summary>
    private static IReadOnlyList<string> RoleNames(AppUser user) =>
        user.Roles
            .OrderBy(r => r.RoleId)
            .Select(r => PlatformRoles.NameFor(r.RoleId))
            .Where(name => name is not null)
            .Select(name => name!)
            .ToList();
}
