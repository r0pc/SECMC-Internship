using DataIntelligence.Core.Security;
using Microsoft.AspNetCore.Authorization;

namespace DataIntelligence.Api.Security;

/// <summary>
/// What each role may reach (FR-9, NFR Security — "role-based access on the API").
/// </summary>
/// <remarks>
/// Policies rather than <c>[Authorize(Roles = ...)]</c> scattered over the endpoints: the three
/// tiers are then written down once, in one file, and an endpoint group names the tier it belongs
/// to instead of restating a role list that has to be kept in step with the other twelve.
/// <para>
/// The names describe access, not roles, because the two are not the same thing: every role can
/// read dashboards, so <see cref="ReadDashboards"/> lists all three. Policies combine with AND when
/// nested — the whole <c>/api</c> group requires <see cref="ReadDashboards"/> and the assistant
/// adds <see cref="UseAssistant"/> on top — which works because each tier's roles are a subset of
/// the one below it.
/// </para>
/// </remarks>
public static class AuthorizationPolicies
{
    /// <summary>Signed in as anything: the dashboards, catalogue, observations and collection log.</summary>
    public const string ReadDashboards = "ReadDashboards";

    /// <summary>
    /// The AI query assistant. Excludes Viewer deliberately — a question costs model tokens and
    /// writes an audit record naming who asked, which is more than "read-only dashboards" grants.
    /// </summary>
    public const string UseAssistant = "UseAssistant";

    /// <summary>
    /// Administration: user accounts, source configuration, and the assistant's audit log.
    /// </summary>
    /// <remarks>
    /// The audit log belongs here rather than with the assistant because it exposes more than the
    /// rest of the API does — every question every user has asked, and the SQL it became. This is
    /// the restriction the TODO in <c>AssistantEndpoints</c> was waiting for.
    /// </remarks>
    public const string Administer = "Administer";

    public static AuthorizationBuilder AddPlatformPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddPolicy(ReadDashboards, policy => policy.RequireRole(
                PlatformRoles.Administrator, PlatformRoles.Analyst, PlatformRoles.Viewer))
            .AddPolicy(UseAssistant, policy => policy.RequireRole(
                PlatformRoles.Administrator, PlatformRoles.Analyst))
            .AddPolicy(Administer, policy => policy.RequireRole(PlatformRoles.Administrator));
}
