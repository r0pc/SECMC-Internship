import { getSession } from "@/lib/session";

/**
 * Who is signed in, and the way out (FR-9).
 *
 * A Server Component that reads the session cookie, which is why the header wraps it in
 * `<Suspense>`: awaiting request-time data in the shell would hold the whole page's first byte
 * behind it, and the nav and content have nothing to learn from this.
 *
 * Renders nothing at all when signed out, so the login page does not carry a sign-out button.
 */
export async function UserMenu() {
  const session = await getSession();

  if (!session) {
    return null;
  }

  const { displayName, email, roles } = session.user;

  return (
    <div className="flex items-center gap-2">
      <div className="hidden text-right leading-tight sm:block">
        <p
          className="text-xs font-medium text-zinc-700 dark:text-zinc-300"
          title={email}
        >
          {displayName || email}
        </p>
        <p className="text-[11px] text-zinc-500 dark:text-zinc-400">
          {/* The highest role held, not all of them. Roles are cumulative in practice, so a list
              would read as a longer way of writing the first one. */}
          {roles[0] ?? "No role"}
        </p>
      </div>

      {/* A form rather than a link: a GET logout can be triggered by any page that can make this
          browser fetch a URL. See app/logout/route.ts. */}
      <form action="/logout" method="post">
        <button
          type="submit"
          className="rounded-md px-3 py-1.5 text-sm font-medium text-zinc-600 transition-colors hover:bg-zinc-100 hover:text-zinc-900 dark:text-zinc-400 dark:hover:bg-zinc-900 dark:hover:text-zinc-100"
        >
          Sign out
        </button>
      </form>
    </div>
  );
}
