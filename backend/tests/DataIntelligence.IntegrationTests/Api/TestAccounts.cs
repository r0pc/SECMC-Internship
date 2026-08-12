using System.Net.Http.Headers;
using System.Net.Http.Json;
using DataIntelligence.Core;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Security;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DataIntelligence.IntegrationTests.Api;

/// <summary>
/// Accounts for the API tests, written straight to <c>sec.AppUser</c> (FR-9).
/// </summary>
/// <remarks>
/// Seeded through the DbContext rather than through <c>/api/users</c>, because that endpoint
/// requires an administrator and the first one cannot create itself. The same bootstrap problem
/// <c>AdminAccountSeeder</c> solves in a real deployment.
/// </remarks>
internal static class TestAccounts
{
    /// <summary>Double underscore is the configuration-section separator for environment variables.</summary>
    public const string SigningKeyVariable = "Auth__SigningKey";

    /// <summary>
    /// The key the hosted API signs test tokens with. Not a secret and not reused anywhere: it
    /// exists so the tests exercise real signature validation rather than a bypassed handler.
    /// </summary>
    public const string SigningKey = "integration-tests-only-signing-key-0123456789abcdef";

    /// <summary>One password for every seeded account. Long enough to satisfy the API's own rule.</summary>
    public const string Password = "integration-test-password";

    public const string AdministratorEmail = "administrator@test.local";
    public const string AnalystEmail = "analyst@test.local";
    public const string ViewerEmail = "viewer@test.local";

    /// <summary>Creates the three accounts the role tests need, if they are not already there.</summary>
    public static async Task SeedAsync(DataIntelligenceDbContext db)
    {
        await EnsureAsync(db, AdministratorEmail, "Test Administrator", PlatformRoles.Administrator);
        await EnsureAsync(db, AnalystEmail, "Test Analyst", PlatformRoles.Analyst);
        await EnsureAsync(db, ViewerEmail, "Test Viewer", PlatformRoles.Viewer);
    }

    /// <summary>
    /// Returns the id of the account with this email, creating it first if it does not exist.
    /// </summary>
    public static async Task<int> EnsureAsync(
        DataIntelligenceDbContext db,
        string email,
        string displayName,
        params string[] roleNames)
    {
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email);

        if (existing is not null)
        {
            return existing.UserId;
        }

        var user = new AppUser
        {
            Email = email,
            DisplayName = displayName,
            SecurityStamp = Guid.NewGuid(),
            IsActive = true,
            CreatedAtPkt = PakistanTime.Now(TimeProvider.System)
        };

        // The same hasher the API verifies with, so a test password that works here works there.
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, Password);

        foreach (var roleName in roleNames)
        {
            user.Roles.Add(new UserRole
            {
                RoleId = PlatformRoles.IdFor(roleName)
                         ?? throw new ArgumentException($"'{roleName}' is not a role.", nameof(roleNames)),
                GrantedAtPkt = PakistanTime.Now(TimeProvider.System)
            });
        }

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user.UserId;
    }

    /// <summary>
    /// Creates an account with a specific id, for tests that assert against that id directly.
    /// </summary>
    /// <remarks>
    /// <c>UserId</c> is an IDENTITY column, so the value has to be forced rather than allocated —
    /// the same manoeuvre <c>docs/database-schema.sql</c> performs for its placeholder row, and for
    /// the same reason: something elsewhere names the number specifically. Identity resumes after
    /// the highest inserted value, so accounts created normally afterwards are unaffected.
    /// <para>
    /// Nothing to do with production behaviour. It exists because <c>ai.AssistantSession.UserId</c>
    /// now has a foreign key to this table, so a test that seeds a transcript for user 1 needs user
    /// 1 to exist.
    /// </para>
    /// </remarks>
    public static async Task EnsureWithIdAsync(
        DataIntelligenceDbContext db, int userId, string displayName)
    {
        if (await db.Users.AnyAsync(u => u.UserId == userId))
        {
            return;
        }

        // {0} and {1} are EF's own placeholders, replaced with SqlParameters rather than with
        // formatted text.
        await db.Database.ExecuteSqlRawAsync(
            """
            SET IDENTITY_INSERT sec.AppUser ON;

            INSERT INTO sec.AppUser (UserId, Email, DisplayName, PasswordHash, IsActive)
            VALUES ({0}, CONCAT(N'user-', {0}, N'@test.local'), {1}, N'!NO-LOGIN!', 1);

            SET IDENTITY_INSERT sec.AppUser OFF;
            """,
            userId,
            displayName);
    }

    /// <summary>
    /// Signs in over HTTP and returns the token, exactly as the frontend does.
    /// </summary>
    public static async Task<LoginResponse> SignInAsync(
        HttpClient client, string email, string password = Password)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest { Email = email, Password = password });

        Assert.True(
            response.IsSuccessStatusCode,
            $"Sign-in as {email} returned {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>(DashboardApiFixture.Json);

        Assert.NotNull(body);

        return body!;
    }

    /// <summary>Attaches a token to every request this client makes.</summary>
    public static HttpClient Authenticated(this HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return client;
    }
}
