import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { cache } from "react";

import { readSession, type Session } from "@/lib/token";
import type { LoginResponse, PlatformRole } from "@/types/api";

/**
 * The signed-in session, held as the API's own access token in an HttpOnly cookie.
 *
 * This app is the only thing that ever sees the token. It is written here, on the server, read
 * here, on the server, and attached to API calls by `lib/api.ts` — which are themselves only ever
 * made from the server (SOW 4.2). It never reaches the browser as script-readable state, so an XSS
 * bug in a chart component cannot walk off with a valid credential.
 *
 * Server-only by construction rather than by convention: everything here imports `next/headers`,
 * which fails the build if a Client Component ever pulls it in.
 *
 * See `lib/token.ts` for why what is decoded here is not, and cannot be, verified.
 */

export type { Session };

/** The cookie. Prefixed so it is obvious in a browser inspector which app owns it. */
export const SESSION_COOKIE = "di_session";

/**
 * Writes the session cookie.
 *
 * Server Action or Route Handler only — HTTP cannot set a cookie once a response has begun
 * streaming, which is what a Server Component's render is.
 */
export async function createSession(login: LoginResponse): Promise<void> {
  const cookieStore = await cookies();

  cookieStore.set(SESSION_COOKIE, login.accessToken, {
    httpOnly: true,

    // Off over plain HTTP, or the cookie is set, silently never sent back, and the login appears
    // to succeed and then not to have happened. Deployments terminate TLS (SOW 4.2), so this is
    // on everywhere except a developer's machine.
    secure: process.env.NODE_ENV === "production",

    // Lax, not Strict: a session has to survive following a link into the platform from an email
    // or a chat message, which Strict breaks by withholding the cookie on that first navigation.
    // Nothing here is a state-changing GET, which is what Strict would otherwise be guarding.
    sameSite: "lax",
    path: "/",

    // The token's own expiry, to the second. A cookie outliving the token inside it produces
    // requests that look signed in and are not; a cookie dying first costs only an early re-login.
    expires: new Date(login.expiresAtUtc),
  });
}

/** Ends the session by removing the cookie. Server Action or Route Handler only. */
export async function deleteSession(): Promise<void> {
  const cookieStore = await cookies();

  cookieStore.delete(SESSION_COOKIE);
}

/**
 * The current session, or null when there is no usable cookie.
 *
 * Memoised for the render pass with React's `cache`, so a page, a nav and a user menu can each ask
 * who the caller is without decoding the same token three times.
 */
export const getSession = cache(async (): Promise<Session | null> => {
  const token = (await cookies()).get(SESSION_COOKIE)?.value;

  return token ? readSession(token) : null;
});

/** The access token to attach to an API call, or null when signed out. */
export async function getAccessToken(): Promise<string | null> {
  return (await getSession())?.accessToken ?? null;
}

/**
 * The session, or a redirect to the login page.
 *
 * Called by pages rather than by the root layout. A layout does not re-render on client-side
 * navigation, so a check placed there would pass once and then never run again for the rest of
 * the visit — the trap the Next.js authentication guide calls out.
 */
export async function requireSession(): Promise<Session> {
  const session = await getSession();

  if (!session) {
    redirect("/login");
  }

  return session;
}

/** The session, or a redirect — to the login page when signed out, home when under-privileged. */
export async function requireRole(...roles: PlatformRole[]): Promise<Session> {
  const session = await requireSession();

  if (!roles.some((role) => session.user.roles.includes(role))) {
    // Home, not a 403 page. The API is what actually refuses them; being shown a page you cannot
    // use is a worse answer than being shown one of the pages you can.
    redirect("/");
  }

  return session;
}

/**
 * True when the caller holds any of these roles.
 *
 * For deciding what to draw, never for deciding what to serve. Hiding a link is a courtesy to the
 * person who cannot use it, not a control — the control is the API refusing the request.
 */
export function hasRole(
  session: Session | null,
  ...roles: PlatformRole[]
): boolean {
  return (
    session !== null && roles.some((role) => session.user.roles.includes(role))
  );
}
