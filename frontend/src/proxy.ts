import { NextResponse, type NextRequest } from "next/server";

import { SESSION_COOKIE } from "@/lib/session";
import { readSession } from "@/lib/token";

/**
 * The signed-out redirect, applied before anything renders (FR-9).
 *
 * `proxy.ts`, not `middleware.ts`: Next 16 renamed the convention, and a file under the old name
 * is deprecated rather than merged with this one.
 *
 * This is an *optimistic* check in the sense the Next.js authentication guide uses — it reads the
 * cookie and nothing else. It runs on every request including prefetches, so it must not call the
 * API or the database; and it cannot verify the token's signature, because the signing key belongs
 * to the API alone.
 *
 * That makes it a redirect, not a guard. What it buys is that a signed-out visitor gets the login
 * page instead of a dashboard skeleton that fills with error panels. What actually protects the
 * data is the API, which verifies every token, re-reads the account behind it and compares its
 * security stamp on every single request — and the pages themselves, which call `requireSession`
 * or `requireRole` close to where they fetch.
 */

/**
 * Reachable without a session. Everything else redirects to the login page.
 *
 * `/logout` is here because it is the Route Handler that clears the cookie: sending a visitor
 * whose session has just been refused to a page that redirects them away from clearing it is how
 * a redirect loop is built.
 */
const PUBLIC_PATHS = ["/login", "/logout"];

export default function proxy(request: NextRequest) {
  const { pathname } = request.nextUrl;

  const token = request.cookies.get(SESSION_COOKIE)?.value;
  const session = token ? readSession(token) : null;

  const isPublic = PUBLIC_PATHS.some(
    (path) => pathname === path || pathname.startsWith(`${path}/`),
  );

  if (!session && !isPublic) {
    const login = new URL("/login", request.nextUrl);

    // Where they were going, so signing in resumes it rather than dumping everyone on the
    // dashboard. Only the path and query — an absolute URL here would be an open redirect, and
    // `nextUrl` keeps it same-origin by construction.
    if (pathname !== "/") {
      login.searchParams.set("next", `${pathname}${request.nextUrl.search}`);
    }

    return NextResponse.redirect(login);
  }

  // Already signed in and asking for the login page: send them where they were going. Without
  // this, the browser's back button lands on a form that has nothing left to do.
  if (session && pathname === "/login") {
    return NextResponse.redirect(new URL("/", request.nextUrl));
  }

  return NextResponse.next();
}

export const config = {
  // Everything except Next's own assets and the favicon. Without an exclusion the redirect would
  // apply to stylesheets and scripts too, and a signed-out visitor would be served an unstyled
  // login page assembled from a chain of redirects.
  matcher: ["/((?!_next/static|_next/image|favicon.ico|.*\\.svg$).*)"],
};
