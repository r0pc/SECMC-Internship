using System.Net;
using System.Net.Http.Json;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Security;

namespace DataIntelligence.IntegrationTests.Api;

/// <summary>
/// Who may reach what (FR-9, NFR Security — "role-based access on the API").
/// </summary>
/// <remarks>
/// The tiers are asserted from both sides. A test that only checks an administrator can reach the
/// audit log would pass just as happily if everyone could.
/// </remarks>
[Collection(DashboardApiCollection.Name)]
public sealed class AuthorizationApiTests
{
    private readonly DashboardApiFixture _fixture;

    public AuthorizationApiTests(DashboardApiFixture fixture)
    {
        _fixture = fixture;
        Assert.True(fixture.IsAvailable, fixture.UnavailableReason);
    }

    // ------------------------------------------------- everyone reads the data

    [Theory]
    [InlineData(TestAccounts.ViewerEmail)]
    [InlineData(TestAccounts.AnalystEmail)]
    [InlineData(TestAccounts.AdministratorEmail)]
    public async Task EveryRoleCanReadTheDashboards(string email)
    {
        var client = await _fixture.CreateClientAsAsync(email);

        Assert.Equal(
            HttpStatusCode.OK, (await client.GetAsync("/api/dashboard/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/series")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sources")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK, (await client.GetAsync("/api/collection/runs")).StatusCode);
    }

    // ------------------------------------------------ the assistant is not for viewers

    [Fact]
    public async Task AViewerCannotReachTheAssistant()
    {
        // A question costs model tokens and writes an audit record naming who asked, which is more
        // than "read-only dashboards" grants.
        var viewer = await _fixture.CreateClientAsAsync(TestAccounts.ViewerEmail);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await viewer.GetAsync("/api/assistant/sessions")).StatusCode);
    }

    [Fact]
    public async Task AnAnalystCanReachTheAssistant()
    {
        var analyst = await _fixture.CreateClientAsAsync(TestAccounts.AnalystEmail);

        Assert.Equal(
            HttpStatusCode.OK, (await analyst.GetAsync("/api/assistant/sessions")).StatusCode);
    }

    // ------------------------------------------------------ administration

    [Theory]
    [InlineData(TestAccounts.ViewerEmail)]
    [InlineData(TestAccounts.AnalystEmail)]
    public async Task OnlyAnAdministratorReadsTheAssistantAuditLog(string email)
    {
        // The audit log exposes every question every user asked and the SQL it became — more than
        // the rest of the API does, and the first thing FR-9 was asked to restrict.
        var client = await _fixture.CreateClientAsAsync(email);

        Assert.Equal(
            HttpStatusCode.Forbidden, (await client.GetAsync("/api/assistant/queries")).StatusCode);
    }

    [Fact]
    public async Task AnAdministratorReadsTheAssistantAuditLog()
    {
        Assert.Equal(
            HttpStatusCode.OK,
            (await _fixture.Client.GetAsync("/api/assistant/queries")).StatusCode);
    }

    [Theory]
    [InlineData(TestAccounts.ViewerEmail)]
    [InlineData(TestAccounts.AnalystEmail)]
    public async Task OnlyAnAdministratorManagesUsers(string email)
    {
        var client = await _fixture.CreateClientAsAsync(email);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);

        var created = await client.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest
            {
                Email = "should-never-exist@test.local",
                DisplayName = "Should never exist",
                Password = TestAccounts.Password
            });

        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
    }

    [Fact]
    public async Task OnlyAnAdministratorChangesASourcesPollingSettings()
    {
        // Everything else on /api/sources is a read. This one can switch off collection from a
        // publisher, which is the whole platform quietly going stale.
        var analyst = await _fixture.CreateClientAsAsync(TestAccounts.AnalystEmail);

        var response = await analyst.PatchAsJsonAsync(
            "/api/sources/1", new DataSourceUpdateRequest { IsEnabled = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --------------------------------------------- the guards on user editing

    [Fact]
    public async Task RefusesToLetAnAdministratorDeactivateThemselves()
    {
        var me = await _fixture.Client.GetJsonAsync<AuthenticatedUserDto>("/api/auth/me");

        var response = await _fixture.Client.PatchAsJsonAsync(
            $"/api/users/{me.UserId}", new UpdateUserRequest { IsActive = false });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task RefusesToLetAnAdministratorRemoveTheirOwnAdministratorRole()
    {
        var me = await _fixture.Client.GetJsonAsync<AuthenticatedUserDto>("/api/auth/me");

        var response = await _fixture.Client.PatchAsJsonAsync(
            $"/api/users/{me.UserId}",
            new UpdateUserRequest { Roles = [PlatformRoles.Analyst] });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // The third guard — refusing to remove the last active administrator — is asserted in
    // UserServiceTests instead. Exercising it means leaving a platform with no administrators at
    // all, and this collection shares one hosted API and one administrator between every test in
    // it; a test that demoted them would break the ones that ran afterwards rather than the one
    // that broke.
}
