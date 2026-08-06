/**
 * Reading and rewriting the query string.
 *
 * Filters live in the URL rather than in component state, so every view of a dashboard is a link
 * someone can paste into a ticket. That makes the search parameters part of this app's contract
 * with its users, and these helpers the one place they are parsed.
 *
 * `searchParams` arrives as a plain object whose values may be a string, an array (the same key
 * repeated), or absent — so every read has to narrow before it can be used.
 */

/** The shape Next resolves a page's `searchParams` promise to. */
export type SearchParams = Record<string, string | string[] | undefined>;

/** The first value for a key. A repeated parameter is a malformed link, not a list. */
export function readParam(
  params: SearchParams,
  key: string,
): string | undefined {
  const value = params[key];
  const first = Array.isArray(value) ? value[0] : value;

  return first === undefined || first === "" ? undefined : first;
}

/** A 1-based page number. Anything unparseable falls back to page 1 rather than erroring. */
export function readPage(params: SearchParams, key = "page"): number {
  const raw = readParam(params, key);
  const parsed = raw === undefined ? Number.NaN : Number.parseInt(raw, 10);

  return Number.isFinite(parsed) && parsed >= 1 ? parsed : 1;
}

/** A flag. Present-and-`true` only — `?failuresOnly=false` means false, not "mentioned". */
export function readFlag(params: SearchParams, key: string): boolean {
  return readParam(params, key)?.toLowerCase() === "true";
}

/**
 * A value constrained to a known set, e.g. an enum the API accepts. Returns undefined for
 * anything else, so a stale bookmark degrades to the default view instead of a 400.
 */
export function readEnum<T extends string>(
  params: SearchParams,
  key: string,
  allowed: readonly T[],
): T | undefined {
  const raw = readParam(params, key);

  return allowed.find((option) => option === raw);
}

/** A `yyyy-MM-dd` date. Rejects anything else so a bad link cannot reach the API. */
export function readDate(
  params: SearchParams,
  key: string,
): string | undefined {
  const raw = readParam(params, key);

  if (!raw || !/^\d{4}-\d{2}-\d{2}$/.test(raw)) {
    return undefined;
  }

  return Number.isNaN(new Date(raw).getTime()) ? undefined : raw;
}

/**
 * Builds a link that keeps the current filters and changes only what is named.
 *
 * `null` removes a parameter — which is how a control clears itself without having to know what
 * else is in the URL.
 */
export function buildHref(
  pathname: string,
  current: SearchParams,
  overrides: Record<string, string | number | boolean | null | undefined> = {},
): string {
  const search = new URLSearchParams();

  for (const [key, value] of Object.entries(current)) {
    const single = Array.isArray(value) ? value[0] : value;

    if (single !== undefined && single !== "") {
      search.set(key, single);
    }
  }

  for (const [key, value] of Object.entries(overrides)) {
    if (value === null || value === undefined || value === "") {
      search.delete(key);
    } else {
      search.set(key, String(value));
    }
  }

  const query = search.toString();

  return query ? `${pathname}?${query}` : pathname;
}
