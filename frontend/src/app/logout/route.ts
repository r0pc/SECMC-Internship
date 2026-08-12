import { NextResponse, type NextRequest } from "next/server";

import { SESSION_COOKIE } from "@/lib/session";

/**
 * Ends the session and sends the visitor to the login page (FR-9).
 *
 * A Route Handler because removing a cookie means writing a response header, which a Server
 * Component's render cannot do. It is reached two ways, and they are not the same event:
 *
 *  - POST, from the sign-out button. A form post rather than a link, because a plain GET logout
 *    can be triggered by any page that can make the browser fetch a URL — an image tag is enough —
 *    and being signed out by someone else's website is a nuisance nobody asked for.
 *
 *  - GET with ?reason=expired, from `lib/api.ts`, when the API refuses a token this app still
 *    holds. Clearing the cookie is the whole point of the detour: without it the login page would
 *    see a session, believe the visitor is signed in, and bounce them back to a page that 401s.
 *    A GET here is safe by the same argument as above — the session was already dead.
 */
export async function POST(request: NextRequest) {
  return endSession(request);
}

export async function GET(request: NextRequest) {
  return endSession(request);
}

function endSession(request: NextRequest) {
  const login = new URL("/login", request.nextUrl);

  if (request.nextUrl.searchParams.get("reason") === "expired") {
    login.searchParams.set("reason", "expired");
  }

  // 303, so the browser follows a POST with a GET. A 307 would replay the POST at /login.
  const response = NextResponse.redirect(login, { status: 303 });

  // Removed on the response being returned rather than through the `cookies()` store, so the
  // Set-Cookie header travels with the redirect that is actually sent. There is no second response
  // for it to end up on.
  response.cookies.delete(SESSION_COOKIE);

  return response;
}
