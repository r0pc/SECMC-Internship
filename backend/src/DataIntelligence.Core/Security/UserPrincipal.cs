namespace DataIntelligence.Core.Security;

/// <summary>
/// Everything an access token is built from, and everything re-checked when one is presented.
/// </summary>
/// <param name="UserId">Written to the token as <c>sub</c>, and recorded against every question asked.</param>
/// <param name="Email">The login.</param>
/// <param name="DisplayName">What the UI greets them by.</param>
/// <param name="Roles">Role names, which become the token's role claims.</param>
/// <param name="SecurityStamp">
/// The value that makes a stateless token revocable. It travels in the token and is compared
/// against the stored one on every request, so a password change ends the sessions opened before it.
/// </param>
public sealed record UserPrincipal(
    int UserId,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    Guid SecurityStamp);

/// <summary>Why a sign-in attempt did or did not produce a token.</summary>
public enum SignInOutcome
{
    Success,

    /// <summary>No such account, or the wrong password. Deliberately one outcome, not two.</summary>
    InvalidCredentials,

    /// <summary>
    /// The password was right and the account is disabled.
    /// </summary>
    /// <remarks>
    /// Distinguished from <see cref="InvalidCredentials"/> here and merged back into it at the
    /// endpoint. A disabled user retyping their password all afternoon is a support call that ends
    /// with someone re-enabling the account, so the API's log should say which it was even though
    /// the response must not.
    /// </remarks>
    Deactivated
}

/// <summary>The outcome of a sign-in, with the principal on success and nothing otherwise.</summary>
public sealed record SignInResult(SignInOutcome Outcome, UserPrincipal? Principal)
{
    public static SignInResult Success(UserPrincipal principal) =>
        new(SignInOutcome.Success, principal);

    public static readonly SignInResult InvalidCredentials =
        new(SignInOutcome.InvalidCredentials, null);

    public static readonly SignInResult Deactivated = new(SignInOutcome.Deactivated, null);
}

/// <summary>Why a self-service password change did or did not happen.</summary>
public enum PasswordChangeOutcome
{
    Success,

    /// <summary>The current password did not verify — 400, and the new one was not written.</summary>
    IncorrectPassword,

    /// <summary>The account behind an otherwise valid token no longer exists — 404.</summary>
    NotFound
}
