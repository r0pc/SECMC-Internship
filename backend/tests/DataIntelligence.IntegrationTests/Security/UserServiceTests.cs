using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Security;
using DataIntelligence.Infrastructure.Security;
using DataIntelligence.IntegrationTests.Collection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using AppUser = DataIntelligence.Core.Entities.AppUser;

namespace DataIntelligence.IntegrationTests.Security;

/// <summary>
/// Accounts, passwords and the guards on editing them (FR-9).
/// </summary>
/// <remarks>
/// Its own database, not the shared API fixture: the last-administrator guard can only be
/// exercised by arranging for there to be no other administrator, and doing that to a database
/// other tests are signed in against would break them rather than this.
/// <para>
/// A real database rather than a fake, for the usual reason in this suite: the unique index on
/// Email is what actually decides a duplicate, and the cascade from <c>sec.UserRole</c> is what
/// actually removes grants.
/// </para>
/// </remarks>
public sealed class UserServiceTests : IAsyncLifetime
{
    private readonly CollectionDatabaseFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private UserService Service(DataIntelligence.Infrastructure.Persistence.DataIntelligenceDbContext db) =>
        new(db, new PasswordHasher<AppUser>(), TimeProvider.System,
            NullLogger<UserService>.Instance);

    // ------------------------------------------------------------- creating

    [Fact]
    public async Task CreatesAnAccountThatCanThenSignIn()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();
        var users = Service(db);

        var created = await users.CreateAsync(
            new CreateUserRequest
            {
                Email = "analyst@example.test",
                DisplayName = "An Analyst",
                Password = "a-perfectly-fine-password",
                Roles = [PlatformRoles.Analyst]
            },
            default);

        Assert.True(created.Succeeded);

        var signIn = await users.SignInAsync(
            new LoginRequest
            {
                Email = "analyst@example.test",
                Password = "a-perfectly-fine-password"
            },
            default);

        Assert.Equal(SignInOutcome.Success, signIn.Outcome);
        Assert.Equal([PlatformRoles.Analyst], signIn.Principal!.Roles);
    }

    [Fact]
    public async Task StoresThePasswordOnlyAsAHash()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();

        await Service(db).CreateAsync(
            new CreateUserRequest
            {
                Email = "hashed@example.test",
                DisplayName = "Hashed",
                Password = "the-plaintext-password"
            },
            default);

        var stored = await db.Users
            .AsNoTracking()
            .FirstAsync(u => u.Email == "hashed@example.test");

        Assert.DoesNotContain("the-plaintext-password", stored.PasswordHash);

        // Identity's v3 marker byte, base64-encoded: every hash it writes starts with it. Asserted
        // because the column's whole contract is "an Identity v3 hash" — docs/database-schema.sql
        // says so, and something writing a bare SHA-256 here would satisfy the assertion above.
        Assert.StartsWith("AQAAAA", stored.PasswordHash);
    }

    [Fact]
    public async Task GrantsViewerWhenNoRoleIsNamed()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();

        var created = await Service(db).CreateAsync(
            new CreateUserRequest
            {
                Email = "unspecified@example.test",
                DisplayName = "Unspecified",
                Password = "a-perfectly-fine-password"
            },
            default);

        // The least this platform has, which is the safe reading of a field somebody left empty.
        Assert.Equal([PlatformRoles.Viewer], created.Value!.Roles);
    }

    [Fact]
    public async Task RefusesADuplicateEmail()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();
        var users = Service(db);

        var request = new CreateUserRequest
        {
            Email = "taken@example.test",
            DisplayName = "First",
            Password = "a-perfectly-fine-password"
        };

        Assert.True((await users.CreateAsync(request, default)).Succeeded);

        var second = await users.CreateAsync(request with { DisplayName = "Second" }, default);

        Assert.Equal(WriteOutcome.Conflict, second.Outcome);
    }

    [Fact]
    public async Task RefusesARoleThatDoesNotExist()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();

        var created = await Service(db).CreateAsync(
            new CreateUserRequest
            {
                Email = "superuser@example.test",
                DisplayName = "Superuser",
                Password = "a-perfectly-fine-password",
                Roles = ["Superuser"]
            },
            default);

        Assert.Equal(WriteOutcome.InvalidReference, created.Outcome);
        Assert.Contains("Superuser", created.Message);
    }

    // -------------------------------------------------------------- signing in

    [Fact]
    public async Task RefusesTheWrongPassword()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();
        var users = Service(db);

        await users.CreateAsync(
            new CreateUserRequest
            {
                Email = "wrong-password@example.test",
                DisplayName = "Wrong Password",
                Password = "the-correct-password"
            },
            default);

        var result = await users.SignInAsync(
            new LoginRequest
            {
                Email = "wrong-password@example.test",
                Password = "the-incorrect-password"
            },
            default);

        Assert.Equal(SignInOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.Principal);
    }

    [Fact]
    public async Task ReportsADeactivatedAccountSeparatelyFromABadPassword()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();
        var users = Service(db);

        var created = await users.CreateAsync(
            new CreateUserRequest
            {
                Email = "retired@example.test",
                DisplayName = "Retired",
                Password = "a-perfectly-fine-password",
                Roles = [PlatformRoles.Analyst]
            },
            default);

        var administrator = await users.CreateAsync(
            new CreateUserRequest
            {
                Email = "admin@example.test",
                DisplayName = "Admin",
                Password = "a-perfectly-fine-password",
                Roles = [PlatformRoles.Administrator]
            },
            default);

        await users.UpdateAsync(
            created.Value!.UserId,
            new UpdateUserRequest { IsActive = false },
            administrator.Value!.UserId,
            default);

        var result = await users.SignInAsync(
            new LoginRequest
            {
                Email = "retired@example.test",
                Password = "a-perfectly-fine-password"
            },
            default);

        // The service distinguishes them so the log can say which it was. The endpoint merges them
        // back into one 401 so the response cannot — see AuthEndpoints.
        Assert.Equal(SignInOutcome.Deactivated, result.Outcome);
    }

    [Fact]
    public async Task RefusesAnAccountWhosePasswordHashIsAMarkerRatherThanAHash()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();

        // Exactly what docs/database-schema.sql seeds for its pre-FR-9 placeholder, and what the
        // FR-9 migration writes for user ids inherited from the assistant's old history. The
        // string cannot base64-decode, which is the point: no password can verify against it.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO sec.AppUser (Email, DisplayName, PasswordHash, IsActive)
            VALUES (N'placeholder@example.test', N'Placeholder', N'!NO-LOGIN!', 1);
            """);

        var result = await Service(db).SignInAsync(
            new LoginRequest
            {
                Email = "placeholder@example.test",
                Password = "anything-at-all-here"
            },
            default);

        // Refused, not thrown. The hasher raises FormatException on an undecodable stored value,
        // and letting that escape would answer a bad credential with a 500.
        Assert.Equal(SignInOutcome.InvalidCredentials, result.Outcome);
    }

    [Fact]
    public async Task RecordsTheSignInTime()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();
        var users = Service(db);

        var created = await users.CreateAsync(
            new CreateUserRequest
            {
                Email = "last-login@example.test",
                DisplayName = "Last Login",
                Password = "a-perfectly-fine-password"
            },
            default);

        Assert.Null(created.Value!.LastLoginAtPkt);

        await users.SignInAsync(
            new LoginRequest
            {
                Email = "last-login@example.test",
                Password = "a-perfectly-fine-password"
            },
            default);

        var after = await users.GetAsync(created.Value.UserId, default);

        Assert.NotNull(after!.LastLoginAtPkt);
    }

    // -------------------------------------------------------------- revocation

    [Fact]
    public async Task StopsResolvingATokensStampAfterAPasswordChange()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();
        var users = Service(db);

        var created = await users.CreateAsync(
            new CreateUserRequest
            {
                Email = "rotates@example.test",
                DisplayName = "Rotates",
                Password = "the-original-password"
            },
            default);

        var signedIn = await users.SignInAsync(
            new LoginRequest
            {
                Email = "rotates@example.test",
                Password = "the-original-password"
            },
            default);

        var stamp = signedIn.Principal!.SecurityStamp;

        Assert.NotNull(await users.ResolveAsync(created.Value!.UserId, stamp, default));

        var changed = await users.ChangePasswordAsync(
            created.Value.UserId,
            new ChangePasswordRequest
            {
                CurrentPassword = "the-original-password",
                NewPassword = "a-replacement-password"
            },
            default);

        Assert.Equal(PasswordChangeOutcome.Success, changed);

        // The stamp the old token carries no longer matches the stored one, so the token is dead
        // even though it is signed and unexpired.
        Assert.Null(await users.ResolveAsync(created.Value.UserId, stamp, default));
    }

    [Fact]
    public async Task RefusesAPasswordChangeWithoutTheCurrentPassword()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();
        var users = Service(db);

        var created = await users.CreateAsync(
            new CreateUserRequest
            {
                Email = "cannot-prove@example.test",
                DisplayName = "Cannot Prove",
                Password = "the-original-password"
            },
            default);

        var outcome = await users.ChangePasswordAsync(
            created.Value!.UserId,
            new ChangePasswordRequest
            {
                CurrentPassword = "a-guess",
                NewPassword = "a-replacement-password"
            },
            default);

        Assert.Equal(PasswordChangeOutcome.IncorrectPassword, outcome);

        // And nothing was written: the original still signs in.
        var signIn = await users.SignInAsync(
            new LoginRequest
            {
                Email = "cannot-prove@example.test",
                Password = "the-original-password"
            },
            default);

        Assert.Equal(SignInOutcome.Success, signIn.Outcome);
    }

    // ------------------------------------------------------------- the guards

    [Fact]
    public async Task RefusesToRemoveTheLastActiveAdministrator()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();
        var users = Service(db);

        var only = await users.CreateAsync(
            new CreateUserRequest
            {
                Email = "only-admin@example.test",
                DisplayName = "Only Admin",
                Password = "a-perfectly-fine-password",
                Roles = [PlatformRoles.Administrator]
            },
            default);

        var second = await users.CreateAsync(
            new CreateUserRequest
            {
                Email = "second-admin@example.test",
                DisplayName = "Second Admin",
                Password = "a-perfectly-fine-password",
                Roles = [PlatformRoles.Administrator]
            },
            default);

        // Allowed while there are two: demoting one leaves one.
        var demotedFirst = await users.UpdateAsync(
            only.Value!.UserId,
            new UpdateUserRequest { Roles = [PlatformRoles.Analyst] },
            second.Value!.UserId,
            default);

        Assert.True(demotedFirst.Succeeded);

        // Refused now that they are the only one. Without this, an installation could be left with
        // nobody who can administer it and no endpoint that could fix that.
        var demotedLast = await users.UpdateAsync(
            second.Value.UserId,
            new UpdateUserRequest { Roles = [PlatformRoles.Analyst] },
            only.Value.UserId,
            default);

        Assert.Equal(WriteOutcome.Conflict, demotedLast.Outcome);
        Assert.Contains("last active administrator", demotedLast.Message);
    }

    [Fact]
    public async Task RefusesToLetSomeoneDeactivateThemselves()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();
        var users = Service(db);

        var administrator = await users.CreateAsync(
            new CreateUserRequest
            {
                Email = "self-deactivate@example.test",
                DisplayName = "Self Deactivate",
                Password = "a-perfectly-fine-password",
                Roles = [PlatformRoles.Administrator]
            },
            default);

        var result = await users.UpdateAsync(
            administrator.Value!.UserId,
            new UpdateUserRequest { IsActive = false },
            administrator.Value.UserId,
            default);

        Assert.Equal(WriteOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task ReplacesRolesRatherThanAddingToThem()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();
        var users = Service(db);

        var administrator = await users.CreateAsync(
            new CreateUserRequest
            {
                Email = "editor@example.test",
                DisplayName = "Editor",
                Password = "a-perfectly-fine-password",
                Roles = [PlatformRoles.Administrator]
            },
            default);

        var edited = await users.CreateAsync(
            new CreateUserRequest
            {
                Email = "edited@example.test",
                DisplayName = "Edited",
                Password = "a-perfectly-fine-password",
                Roles = [PlatformRoles.Analyst]
            },
            default);

        var updated = await users.UpdateAsync(
            edited.Value!.UserId,
            new UpdateUserRequest { Roles = [PlatformRoles.Viewer] },
            administrator.Value!.UserId,
            default);

        Assert.True(updated.Succeeded);
        Assert.Equal([PlatformRoles.Viewer], updated.Value!.Roles);
    }

    [Fact]
    public async Task LeavesOmittedFieldsAlone()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);

        await using var db = _database.CreateContext();
        var users = Service(db);

        var administrator = await users.CreateAsync(
            new CreateUserRequest
            {
                Email = "patcher@example.test",
                DisplayName = "Patcher",
                Password = "a-perfectly-fine-password",
                Roles = [PlatformRoles.Administrator]
            },
            default);

        var edited = await users.CreateAsync(
            new CreateUserRequest
            {
                Email = "patched@example.test",
                DisplayName = "Original Name",
                Password = "a-perfectly-fine-password",
                Roles = [PlatformRoles.Analyst]
            },
            default);

        var updated = await users.UpdateAsync(
            edited.Value!.UserId,
            new UpdateUserRequest { DisplayName = "New Name" },
            administrator.Value!.UserId,
            default);

        Assert.Equal("New Name", updated.Value!.DisplayName);
        Assert.Equal([PlatformRoles.Analyst], updated.Value.Roles);
        Assert.True(updated.Value.IsActive);
    }
}
