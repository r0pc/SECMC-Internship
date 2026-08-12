"use server";

/**
 * Signing in, as a Server Function.
 *
 * The credentials go from the form to this server and on to the API; the browser never calls the
 * API itself, which is this app's one rule (SOW 4.2) and is also what lets the token be stored in
 * a cookie no script can read.
 *
 * Returns an outcome rather than throwing. A wrong password is an ordinary result on a login form
 * — the most ordinary one there is — and it has to render as a message above the fields rather
 * than as an error page.
 */

import { redirect } from "next/navigation";

import { ApiError, login } from "@/lib/api";
import { createSession } from "@/lib/session";

export interface LoginState {
  readonly error?: string;
  /** Kept so the field is not cleared under someone who mistyped their password. */
  readonly email?: string;
}

export async function signIn(
  _state: LoginState,
  formData: FormData,
): Promise<LoginState> {
  const email = String(formData.get("email") ?? "").trim();
  const password = String(formData.get("password") ?? "");

  // Where to go afterwards, carried from the proxy's redirect. Rejected unless it is a path on
  // this site: a login form that will forward you to any URL in a query parameter is an open
  // redirect, and a convincing one, because the victim really did just sign in.
  const requested = String(formData.get("next") ?? "");
  const next = requested.startsWith("/") && !requested.startsWith("//")
    ? requested
    : "/";

  if (email.length === 0 || password.length === 0) {
    return { email, error: "Enter your email and password." };
  }

  try {
    const session = await login(email, password);

    await createSession(session);
  } catch (error) {
    if (error instanceof ApiError) {
      return {
        email,
        // The API's own wording. It answers a wrong password and an unknown address identically
        // and on purpose, so that a stranger cannot use this form to find out who has an account
        // here — rewriting it more helpfully here would undo that.
        error: error.isUnreachable
          ? "The platform is not reachable right now. Try again in a moment."
          : (error.problem.detail ?? "That sign-in was not accepted."),
      };
    }

    throw error;
  }

  // Outside the try: redirect works by throwing, and catching it here would turn a successful
  // sign-in into "that sign-in was not accepted".
  redirect(next);
}
