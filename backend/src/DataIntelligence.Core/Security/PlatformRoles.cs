namespace DataIntelligence.Core.Security;

/// <summary>
/// The three roles (NFR Security), their ids, and what each one is allowed to reach.
/// </summary>
/// <remarks>
/// The ids match the order <c>docs/database-schema.sql</c> inserts them in, because that script
/// lets <c>sec.Role.RoleId</c> be an IDENTITY column and a database built from it will have
/// allocated 1, 2, 3 in that order. A migration-built database seeds the same three ids explicitly,
/// so the two agree rather than nearly agreeing.
/// <para>
/// Roles are cumulative in practice but not by inheritance: an Analyst is not stored as also
/// holding Viewer. The policies express the containment instead — see <c>AuthorizationPolicies</c> —
/// so what a role can do is read in one place rather than assembled from role rows.
/// </para>
/// </remarks>
public static class PlatformRoles
{
    /// <summary>Full access: configuration, user management, all data.</summary>
    public const string Administrator = "Administrator";

    /// <summary>Dashboards, drill-down and the AI query assistant.</summary>
    public const string Analyst = "Analyst";

    /// <summary>Read-only dashboards.</summary>
    public const string Viewer = "Viewer";

    public const byte AdministratorId = 1;
    public const byte AnalystId = 2;
    public const byte ViewerId = 3;

    /// <summary>Every role name, for validating a grant before it reaches the database.</summary>
    public static readonly IReadOnlyList<string> All = [Administrator, Analyst, Viewer];

    /// <summary>Maps a role name to its id, case-insensitively. Null when the name is not a role.</summary>
    public static byte? IdFor(string? name) => name?.ToLowerInvariant() switch
    {
        "administrator" => AdministratorId,
        "analyst" => AnalystId,
        "viewer" => ViewerId,
        _ => null
    };

    /// <summary>Maps an id back to its canonical name. Null when the id is not a role.</summary>
    public static string? NameFor(byte roleId) => roleId switch
    {
        AdministratorId => Administrator,
        AnalystId => Analyst,
        ViewerId => Viewer,
        _ => null
    };
}
