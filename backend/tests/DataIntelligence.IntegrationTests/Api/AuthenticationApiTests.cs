using System.Net;
using System.Net.Http.Json;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Security;

namespace DataIntelligence.IntegrationTests.Api;

/// <summary>
/// Signing in, and what a token is worth afterwards (FR-9).
/// </summary>
/// <remarks>
/// Run over the hosted API rather than against <c>UserService</c> directly, because most of what is
/// asserted here is not the service's behaviour: it is whether the endpoints are wired to it. A
/// service that refuses a stale token is no use if the pipeline never asks it.
/// </remarks>
[Collection(DashboardApiCollection.Name)]
public sealed class AuthenticationApiTests
{
    private readonly DashboardApiFixture _fixture;

    public AuthenticationApiTests(DashboardApiFixture fixture)
    {
        _fixture = fixture;
        Assert.True(fixture.IsAvailable, fixture.UnavailableReason);
    }

    // ------------------------------------------------------------- signing in

    [Fact]
    public async Task ReturnsATokenAndTheCallerForACorrectPassword()
    {
        var session = await TestAccounts.SignInAsync(
            _fixture.Anonymous, TestAccounts.AnalystEmail);

        Assert.False(string.IsNullOrWhiteSpace(session.AccessToken));
        Assert.Equal(TestAccounts.AnalystEmail, session.User.Email);
        Assert.Equal([PlatformRoles.Analyst], session.User.Roles);
        Assert.True(session.ExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task RefusesTheWrongPassword()
    {
        var response = await _fixture.Anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest
            {
                Email = TestAccounts.AnalystEmail,
                Password = "not-the-right-password"
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnswersAnUnknownEmailExactlyAsItAnswersAWrongPassword()
    {
        // The two must be indistinguishable from outside. Anything that told them apart would let
        // a stranger ask this API whether a given person has an account here.
        var unknown = await _fixture.Anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = "nobody@test.local", Password = TestAccounts.Password });

        var wrongPassword = await _fixture.Anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = TestAccounts.ViewerEmail, Password = "wrong-password-here" });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);

        // Compared field by field rather than as raw bodies: ProblemDetails carries a traceId that
        // is different on every request by design, and asserting on it would only ever prove that
        // two requests are two requests.
        var first = await ProblemOf(unknown);
        var second = await ProblemOf(wrongPassword);

        Assert.Equal(first.Title, second.Title);
        Assert.Equal(first.Detail, second.Detail);
        Assert.Equal(first.Status, second.Status);
    }

    private static async Task<ProblemDetails> ProblemOf(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            DashboardApiFixture.Json);

        Assert.NotNull(problem);

        return problem!;
    }

    /// <summary>The fields of RFC 9457 this test needs; the API's own error shape.</summary>
    private sealed record ProblemDetails
    {
        public string? Title { get; init; }

        public string? Detail { get; init; }

        public int? Status { get; init; }
    }

    [Fact]
    public async Task RejectsAMalformedEmailBeforeTouchingTheDatabase()
    {
        var response = await _fixture.Anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = "not-an-email", Password = TestAccounts.Password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------- the blanket requirement

    [Theory]
    [InlineData("/api/dashboard/summary")]
    [InlineData("/api/series")]
    [InlineData("/api/sources")]
    [InlineData("/api/collection/runs")]
    [InlineData("/api/assistant/sessions")]
    [InlineData("/api/users")]
    [InlineData("/api/auth/me")]
    public async Task RefusesEveryEndpointWithoutAToken(string url)
    {
        // FR-9: "authentication/authorization for all non-public endpoints". The requirement is
        // declared once on the /api group, and this is the test that it actually covers the group
        // rather than the endpoints someone remembered.
        var response = await _fixture.Anonymous.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LeavesHealthAnonymous()
    {
        // A load balancer cannot sign in, and "the process is up and can see its database" is not
        // information worth a credential.
        var response = await _fixture.Anonymous.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RefusesATokenItDidNotSign()
    {
        var client = _fixture.Anonymous;
        var session = await TestAccounts.SignInAsync(client, TestAccounts.ViewerEmail);

        // Same token with its last character altered: the payload still parses, the signature no
        // longer verifies.
        var tampered = session.AccessToken[..^1]
                       + (session.AccessToken[^1] == 'A' ? 'B' : 'A');

        var response = await _fixture.Anonymous
            .Authenticated(tampered)
            .GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Undone, because Anonymous is shared by the whole collection.
        _fixture.Anonymous.DefaultRequestHeaders.Authorization = null;
    }

    [Fact]
    public async Task ReportsTheCallerFromTheirOwnToken()
    {
        var client = await _fixture.CreateClientAsAsync(TestAccounts.ViewerEmail);

        var me = await client.GetJsonAsync<AuthenticatedUserDto>("/api/auth/me");

        Assert.Equal(TestAccounts.ViewerEmail, me.Email);
        Assert.Equal("Test Viewer", me.DisplayName);
        Assert.Equal([PlatformRoles.Viewer], me.Roles);
    }

    // ------------------------------------------------------------- revocation

    [Fact]
    public async Task StopsAcceptingATokenOnceTheAccountIsDeactivated()
    {
        // The reason tokens can live for eight hours without a refresh dance: they are not merely
        // signed and unexpired, they are re-checked against the account on every request.
        var administrator = _fixture.Client;

        var created = await CreateUserAsync(
            administrator, "revoked-by-deactivation@test.local", PlatformRoles.Viewer);

        var theirs = await _fixture.CreateClientAsAsync(created.Email);

        Assert.Equal(HttpStatusCode.OK, (await theirs.GetAsync("/api/auth/me")).StatusCode);

        var deactivated = await administrator.PatchAsJsonAsync(
            $"/api/users/{created.UserId}", new UpdateUserRequest { IsActive = false });

        Assert.True(deactivated.IsSuccessStatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await theirs.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task StopsAcceptingATokenOnceTheAccountsRolesChange()
    {
        // Roles ride inside the token. Without rotating the security stamp on a role change, a
        // demoted administrator would keep administering until their token expired.
        var administrator = _fixture.Client;

        var created = await CreateUserAsync(
            administrator, "revoked-by-demotion@test.local", PlatformRoles.Analyst);

        var theirs = await _fixture.CreateClientAsAsync(created.Email);

        Assert.Equal(HttpStatusCode.OK, (await theirs.GetAsync("/api/auth/me")).StatusCode);

        var demoted = await administrator.PatchAsJsonAsync(
            $"/api/users/{created.UserId}",
            new UpdateUserRequest { Roles = [PlatformRoles.Viewer] });

        Assert.True(demoted.IsSuccessStatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await theirs.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task EndsTheCallersOwnSessionWhenTheyChangeTheirPassword()
    {
        var created = await CreateUserAsync(
            _fixture.Client, "changes-own-password@test.local", PlatformRoles.Viewer);

        var theirs = await _fixture.CreateClientAsAsync(created.Email);

        var changed = await theirs.PostAsJsonAsync(
            "/api/auth/password",
            new ChangePasswordRequest
            {
                CurrentPassword = TestAccounts.Password,
                NewPassword = "a-brand-new-password"
            });

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        // The token that made the change is dead too. That is the point: a password changed
        // because a session was compromised has to end that session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await theirs.GetAsync("/api/auth/me")).StatusCode);

        // And the new password is the one that works now.
        await TestAccounts.SignInAsync(_fixture.Anonymous, created.Email, "a-brand-new-password");
    }

    [Fact]
    public async Task RefusesAPasswordChangeThatCannotProveTheCurrentOne()
    {
        var created = await CreateUserAsync(
            _fixture.Client, "wrong-current-password@test.local", PlatformRoles.Viewer);

        var theirs = await _fixture.CreateClientAsAsync(created.Email);

        var response = await theirs.PostAsJsonAsync(
            "/api/auth/password",
            new ChangePasswordRequest
            {
                CurrentPassword = "not-their-password",
                NewPassword = "some-other-password"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Unchanged: a rejected change must not have written anything.
        await TestAccounts.SignInAsync(_fixture.Anonymous, created.Email);
    }

    [Fact]
    public async Task RefusesToSignInADeactivatedAccount()
    {
        var created = await CreateUserAsync(
            _fixture.Client, "deactivated-cannot-sign-in@test.local", PlatformRoles.Viewer);

        await _fixture.Client.PatchAsJsonAsync(
            $"/api/users/{created.UserId}", new UpdateUserRequest { IsActive = false });

        var response = await _fixture.Anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = created.Email, Password = TestAccounts.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    internal static async Task<UserDto> CreateUserAsync(
        HttpClient administrator, string email, params string[] roles)
    {
        var response = await administrator.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest
            {
                Email = email,
                DisplayName = email,
                Password = TestAccounts.Password,
                Roles = roles
            });

        Assert.True(
            response.IsSuccessStatusCode,
            $"Creating {email} returned {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());

        var user = await response.Content.ReadFromJsonAsync<UserDto>(DashboardApiFixture.Json);

        Assert.NotNull(user);

        return user!;
    }
}
