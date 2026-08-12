import type { AuthenticatedUserDto, PlatformRole } from "@/types/api";

/**
 * Reading the API's access token, without verifying it.
 *
 * Separate from `lib/session.ts` because `proxy.ts` needs it and cannot import that file: the
 * session helpers reach for `next/headers`, which is not available before a route is rendered.
 *
 * Nothing here checks a signature, and nothing here can. Only the holder of the signing key can do
 * that, and that is the API — giving this app a copy would mean two places able to mint tokens.
 * Everything below is therefore an *optimistic* read in the sense the Next.js authentication guide
 * uses: enough to choose which page to render and which links to draw, never enough to decide
 * whether data may be returned. Every actual answer comes from the API, which verifies the
 * signature, re-reads the account and compares its security stamp on every single request. A
 * forged cookie earns a rendered login page and 401s from everything behind it.
 */

/** What this app knows about the caller between requests. */
export interface Session {
  readonly accessToken: string;
  readonly user: AuthenticatedUserDto;
  readonly expiresAt: Date;
}

/** The three roles, most privileged first — the order the API returns them in. */
export const ROLE_ORDER: readonly PlatformRole[] = [
  "Administrator",
  "Analyst",
  "Viewer",
];

/** Reads a token into a session, returning null for anything unusable. */
export function readSession(token: string): Session | null {
  const claims = decodeClaims(token);

  if (!claims) {
    return null;
  }

  const userId = Number.parseInt(claims.sub ?? "", 10);
  const expiresAt = new Date((claims.exp ?? 0) * 1000);

  if (!Number.isFinite(userId) || Number.isNaN(expiresAt.getTime())) {
    return null;
  }

  // An expired token is treated as no session at all, so the visitor gets a login page rather than
  // a dashboard of panels that each fail with a 401.
  if (expiresAt.getTime() <= Date.now()) {
    return null;
  }

  return {
    accessToken: token,
    expiresAt,
    user: {
      userId,
      email: claims.email ?? "",
      displayName: claims.name ?? claims.email ?? "",
      roles: rolesOf(claims),
    },
  };
}

/** The claims this app reads. The token carries more; these are the ones it has a use for. */
interface TokenClaims {
  sub?: string;
  email?: string;
  name?: string;
  /** One role is a string, several are an array — how a JWT renders a repeated claim. */
  role?: string | string[];
  /** Seconds since the epoch, per RFC 7519. */
  exp?: number;
}

function rolesOf(claims: TokenClaims): PlatformRole[] {
  const raw =
    typeof claims.role === "string"
      ? [claims.role]
      : Array.isArray(claims.role)
        ? claims.role
        : [];

  // Filtered against the known roles rather than cast: this comes out of a cookie, and a role
  // nobody defined must not become one by being spelled in the right place.
  return ROLE_ORDER.filter((role) => raw.includes(role));
}

/**
 * Pulls the claim set out of a JWT.
 *
 * A JWT is three base64url segments and the middle one is the JSON payload. `atob` wants standard
 * base64, so the URL-safe alphabet is translated back and the padding restored.
 */
function decodeClaims(token: string): TokenClaims | null {
  const payload = token.split(".")[1];

  if (!payload) {
    return null;
  }

  try {
    const base64 = payload.replace(/-/g, "+").replace(/_/g, "/");
    const padded = base64.padEnd(
      base64.length + ((4 - (base64.length % 4)) % 4),
      "=",
    );

    return JSON.parse(atob(padded)) as TokenClaims;
  } catch {
    // Anything unparseable is not a session. There is nothing useful to log: the only way to get
    // here is a cookie this app did not write.
    return null;
  }
}
