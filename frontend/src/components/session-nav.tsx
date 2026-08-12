import { getSession } from "@/lib/session";

import { SiteNav } from "./site-nav";

/**
 * The navigation, with the caller's roles read from their session.
 *
 * A thin Server Component wrapper so `SiteNav` can stay a Client Component — it needs the pathname
 * to mark the current section, and a Client Component cannot read a cookie.
 *
 * Nothing at all when signed out: the login page has nowhere to navigate to.
 */
export async function SessionNav() {
  const session = await getSession();

  if (!session) {
    return null;
  }

  return <SiteNav roles={session.user.roles} />;
}
