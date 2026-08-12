using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Core.Security;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Infrastructure.Security;

/// <summary>
/// Creates the first administrator at startup, if the platform has none and one is configured.
/// </summary>
/// <remarks>
/// The bootstrap problem, and only that: accounts are created by administrators (there is no
/// self-registration), so a fresh deployment has nobody who can create the first one. The roles
/// themselves are seeded by the migration; an account cannot be, because it needs a password and a
/// password in a migration is a password in source control, identical in every deployment.
/// <para>
/// It runs when there is no <em>active administrator</em> rather than when the table is empty. A
/// database built from <c>docs/database-schema.sql</c> already holds the pre-FR-9 placeholder user,
/// and "no accounts at all" would read that as a populated platform and refuse to bootstrap it.
/// </para>
/// <para>
/// A failure here is logged and swallowed. The API is expected to start with SQL Server
/// unreachable — <c>/health</c> exists to report exactly that — and a seeder that took the process
/// down with it would turn a database outage into a deployment that will not boot.
/// </para>
/// </remarks>
public sealed class AdminAccountSeeder : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AuthOptions _options;
    private readonly ILogger<AdminAccountSeeder> _logger;

    public AdminAccountSeeder(
        IServiceScopeFactory scopeFactory,
        IOptions<AuthOptions> options,
        ILogger<AdminAccountSeeder> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seed = _options.SeedAdministrator;

        if (seed is null || string.IsNullOrWhiteSpace(seed.Email)
                         || string.IsNullOrWhiteSpace(seed.Password))
        {
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            var db = scope.ServiceProvider.GetRequiredService<DataIntelligenceDbContext>();

            var administratorExists = await db.Users.AnyAsync(
                u => u.IsActive && u.Roles.Any(r => r.RoleId == PlatformRoles.AdministratorId),
                cancellationToken);

            if (administratorExists)
            {
                return;
            }

            var email = seed.Email.Trim();

            // An account on that address with no Administrator role is somebody's real account,
            // and granting it administration — or overwriting its password — is not this
            // component's decision to make silently.
            if (await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
            {
                _logger.LogWarning(
                    "Auth:SeedAdministrator names {Email}, which already has an account without "
                    + "the Administrator role. No administrator was created. Grant the role in the "
                    + "database, or point the setting at a different address.",
                    email);

                return;
            }

            var users = scope.ServiceProvider.GetRequiredService<IUserService>();

            var result = await users.CreateAsync(
                new CreateUserRequest
                {
                    Email = email,
                    DisplayName = seed.DisplayName,
                    Password = seed.Password,
                    Roles = [PlatformRoles.Administrator]
                },
                cancellationToken);

            if (result.Succeeded)
            {
                _logger.LogWarning(
                    "Created the first administrator ({Email}) from Auth:SeedAdministrator. Sign "
                    + "in, change that password, and remove the setting.",
                    email);
            }
            else
            {
                _logger.LogError(
                    "Could not create the seeded administrator: {Reason}", result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not seed the first administrator. The API is still starting; check "
                + "/health for database reachability.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
