"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";

import type { PlatformRole } from "@/types/api";

/**
 * Primary navigation.
 *
 * A Client Component only because marking the current section needs the pathname, which a Server
 * Component cannot read. It renders into the initial HTML like anything else and is downloaded
 * once, so the cost is the highlight and nothing more.
 *
 * Links are filtered by the caller's roles. That is a courtesy to the person who cannot use them,
 * not a control: the roles arrive as a prop from a Server Component that read the session cookie,
 * and what actually refuses an unauthorised request is the API. Hiding /admin/users would be
 * worthless on its own — the page itself checks, and every call it makes is checked again.
 */
const LINKS: readonly {
  href: string;
  label: string;
  /** Match the path exactly. Only the dashboard needs it — "/" prefixes everything. */
  exact?: boolean;
  /** Roles that may see the link. Absent means every signed-in user. */
  roles?: readonly PlatformRole[];
}[] = [
  { href: "/", label: "Dashboard", exact: true },
  // A Viewer's role is read-only dashboards, and the API answers their assistant calls with a 403.
  { href: "/assistant", label: "Assistant", roles: ["Administrator", "Analyst"] },
  { href: "/admin/users", label: "Users", roles: ["Administrator"] },
  // /series, /collection and /sources are intentionally absent: the routes still exist and stay
  // reachable by URL and by the links inside the dashboard, they are just not top-level
  // destinations any more.
];

export function SiteNav({ roles }: { roles: readonly PlatformRole[] }) {
  const pathname = usePathname();

  const visible = LINKS.filter(
    (link) => !link.roles || link.roles.some((role) => roles.includes(role)),
  );

  return (
    <nav aria-label="Primary" className="flex items-center gap-1">
      {visible.map((link) => {
        const active = link.exact
          ? pathname === link.href
          : pathname === link.href || pathname.startsWith(`${link.href}/`);

        return (
          <Link
            key={link.href}
            href={link.href}
            aria-current={active ? "page" : undefined}
            className={`rounded-md px-3 py-1.5 text-sm font-medium transition-colors ${
              active
                ? "bg-zinc-900 text-white dark:bg-zinc-100 dark:text-zinc-900"
                : "text-zinc-600 hover:bg-zinc-100 hover:text-zinc-900 dark:text-zinc-400 dark:hover:bg-zinc-900 dark:hover:text-zinc-100"
            }`}
          >
            {link.label}
          </Link>
        );
      })}
    </nav>
  );
}
