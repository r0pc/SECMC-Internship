using System.Security.Claims;
using DataIntelligence.Api.Security;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Core.Security;
using Microsoft.AspNetCore.Authorization;

namespace DataIntelligence.Api.Endpoints;

/// <summary>Signing in, and what the caller can do about their own account (FR-9).</summary>
public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Authentication");

        group.MapPost("/login", async (
                LoginRequest request,
                IUserService users,
                IAccessTokenIssuer tokens,
                ILoggerFactory loggerFactory,
                HttpContext http,
                CancellationToken cancellationToken) =>
            {
                var invalid = ApiEndpoints.Validate(request);

                if (invalid is not null)
                {
                    return invalid;
                }

                var result = await users.SignInAsync(request, cancellationToken);

                if (result.Outcome != SignInOutcome.Success || result.Principal is null)
                {
                    loggerFactory.CreateLogger(typeof(AuthEndpoints)).LogInformation(
                        "Failed sign-in for {Email} from {ClientIp}: {Outcome}.",
                        request.Email,
                        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        result.Outcome);

                    // One answer for both failures. "Wrong password" and "that account is
                    // disabled" are different facts, and telling a stranger which one applies
                    // tells them whether the address has an account here. The distinction is in
                    // the log, where the person who can act on it will see it.
                    return Results.Problem(
                        title: "Sign-in failed",
                        detail: "That email and password combination was not accepted.",
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                var token = tokens.Issue(result.Principal);

                return Results.Ok(new LoginResponse
                {
                    AccessToken = token.Value,
                    ExpiresAtUtc = token.ExpiresAtUtc,
                    User = ToDto(result.Principal)
                });
            })
            .AllowAnonymous()
            .WithName("Login")
            .WithSummary("Exchanges an email and password for an access token.")
            .WithDescription(
                "The only endpoint on this API that does not require a token. Present the returned "
                + "token as 'Authorization: Bearer <token>' on every other call. It expires at the "
                + "returned time, and stops being accepted earlier than that if the account is "
                + "disabled or its password or roles change.")
            .Produces<LoginResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/me", (ClaimsPrincipal caller) => Results.Ok(FromClaims(caller)))
            .WithName("GetCurrentUser")
            .WithSummary("The caller, as the token says they are.")
            .WithDescription(
                "Read from the presented token rather than the database, which is what makes it "
                + "cheap enough to call on every page render. The token is only accepted at all "
                + "once the account behind it has been confirmed to still exist, still be enabled, "
                + "and still carry the roles printed in it, so the two cannot disagree.")
            .Produces<AuthenticatedUserDto>();

        group.MapPost("/password", async (
                ChangePasswordRequest request,
                ClaimsPrincipal caller,
                IUserService users,
                CancellationToken cancellationToken) =>
            {
                var invalid = ApiEndpoints.Validate(request);

                if (invalid is not null)
                {
                    return invalid;
                }

                if (request.CurrentPassword == request.NewPassword)
                {
                    return ApiEndpoints.BadRequest(
                        "The new password is the same as the current one.");
                }

                var outcome = await users.ChangePasswordAsync(
                    caller.UserId(), request, cancellationToken);

                return outcome switch
                {
                    PasswordChangeOutcome.Success => Results.NoContent(),
                    PasswordChangeOutcome.IncorrectPassword => Results.Problem(
                        title: "Incorrect password",
                        detail: "The current password is not correct.",
                        statusCode: StatusCodes.Status400BadRequest),
                    _ => ApiEndpoints.NotFound("This account no longer exists.")
                };
            })
            .WithName("ChangeOwnPassword")
            .WithSummary("Changes the caller's own password.")
            .WithDescription(
                "Requires the current password. Succeeding invalidates every token issued before "
                + "the change — including the one used to make the request, which is the point: a "
                + "password changed because a session was compromised has to end that session. The "
                + "caller signs in again afterwards.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    /// <summary>Reads the caller out of their token's claims.</summary>
    internal static AuthenticatedUserDto FromClaims(ClaimsPrincipal caller) => new()
    {
        UserId = caller.UserId(),
        Email = caller.FindFirstValue(
            Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Email) ?? string.Empty,
        DisplayName = caller.Identity?.Name ?? string.Empty,

        // Ordered against PlatformRoles rather than however the claims happen to be arranged, so
        // "the caller's first role" means the same thing everywhere it is read.
        Roles = PlatformRoles.All.Where(caller.IsInRole).ToList()
    };

    private static AuthenticatedUserDto ToDto(UserPrincipal principal) => new()
    {
        UserId = principal.UserId,
        Email = principal.Email,
        DisplayName = principal.DisplayName,
        Roles = PlatformRoles.All.Where(principal.Roles.Contains).ToList()
    };
}
