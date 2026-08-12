using System.Security.Claims;
using DataIntelligence.Api.Security;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Core.Security;

namespace DataIntelligence.Api.Endpoints;

/// <summary>
/// User administration (FR-9). Administrator only, in full — including the list.
/// </summary>
/// <remarks>
/// There is no self-registration. Accounts are created by someone who already has one, which is
/// the whole access-control model for an internal platform: reaching the login page entitles a
/// visitor to nothing.
/// </remarks>
public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/users")
            .WithTags("Users")
            .RequireAuthorization(AuthorizationPolicies.Administer);

        group.MapGet("/", async (IUserService users, CancellationToken cancellationToken) =>
                Results.Ok(await users.ListAsync(cancellationToken)))
            .WithName("GetUsers")
            .WithSummary("Every account, oldest first.")
            .WithDescription(
                "Includes deactivated accounts. They are how a departed colleague is retired — "
                + "their questions are audit records with a foreign key to their account, so the "
                + "row cannot be deleted without deleting the record of what they asked.")
            .Produces<IReadOnlyList<UserDto>>();

        group.MapGet("/{userId:int}", async (
                int userId,
                IUserService users,
                CancellationToken cancellationToken) =>
            {
                var user = await users.GetAsync(userId, cancellationToken);

                return user is null
                    ? ApiEndpoints.NotFound($"No user with id {userId}.")
                    : Results.Ok(user);
            })
            .WithName("GetUser")
            .WithSummary("Reads one account.")
            .Produces<UserDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", async (
                CreateUserRequest request,
                IUserService users,
                CancellationToken cancellationToken) =>
            {
                var invalid = ApiEndpoints.Validate(request);

                if (invalid is not null)
                {
                    return invalid;
                }

                var result = await users.CreateAsync(request, cancellationToken);

                return result.ToHttpResult(user =>
                    Results.Created($"/api/users/{user.UserId}", user));
            })
            .WithName("CreateUser")
            .WithSummary("Creates an account.")
            .WithDescription(
                $"Passwords are at least {PasswordRules.MinLength} characters and are stored only "
                + "as a PBKDF2 hash. An omitted or empty role list grants Viewer — the least this "
                + "platform has, and the safe reading of a field somebody forgot to fill in. The "
                + "new user should change the password they were given at their first sign-in.")
            .Produces<UserDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPatch("/{userId:int}", async (
                int userId,
                UpdateUserRequest request,
                ClaimsPrincipal caller,
                IUserService users,
                CancellationToken cancellationToken) =>
            {
                var invalid = ApiEndpoints.Validate(request);

                if (invalid is not null)
                {
                    return invalid;
                }

                var result = await users.UpdateAsync(
                    userId, request, caller.UserId(), cancellationToken);

                return result.ToHttpResult(Results.Ok);
            })
            .WithName("UpdateUser")
            .WithSummary("Changes a display name, roles or active state.")
            .WithDescription(
                "The email is not editable: it is the login, and changing it is indistinguishable "
                + "from locking someone out of their own account. Omitted fields are left "
                + "unchanged. Two edits are refused — deactivating or demoting yourself, and "
                + "removing the last active administrator — because both leave a platform nobody "
                + "can administer. Changing roles or deactivating an account ends that user's open "
                + "sessions immediately rather than when their token would have expired.")
            .Produces<UserDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{userId:int}/password", async (
                int userId,
                ResetPasswordRequest request,
                IUserService users,
                CancellationToken cancellationToken) =>
            {
                var invalid = ApiEndpoints.Validate(request);

                if (invalid is not null)
                {
                    return invalid;
                }

                var result = await users.ResetPasswordAsync(
                    userId, request.NewPassword, cancellationToken);

                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("ResetUserPassword")
            .WithSummary("Sets another user's password, for someone who has forgotten theirs.")
            .WithDescription(
                "There is no email-based reset flow — this platform sends no mail — so a "
                + "forgotten password is an administrator setting a new one and telling the person "
                + "out of band. It ends every session that account had open.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/roles", () => Results.Ok(PlatformRoles.All))
            .WithName("GetRoles")
            .WithSummary("The role names a user may be granted.")
            .Produces<IReadOnlyList<string>>();

        return group;
    }
}
