"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";

/**
 * Primary navigation.
 *
 * A Client Component only because marking the current section needs the pathname, which a Server
 * Component cannot read. It renders into the initial HTML like anything else and is downloaded
 * once, so the cost is the highlight and nothing more.
 */
const LINKS: readonly {
  href: string;
  label: string;
  /** Match the path exactly. Only the dashboard needs it — "/" prefixes everything. */
  exact?: boolean;
}[] = [
  { href: "/", label: "Dashboard", exact: true },
  { href: "/assistant", label: "Assistant" },
  // /series, /collection and /sources are intentionally absent: the routes still exist and stay
  // reachable by URL and by the links inside the dashboard, they are just not top-level
  // destinations any more.
];

export function SiteNav() {
  const pathname = usePathname();

  return (
    <nav aria-label="Primary" className="flex items-center gap-1">
      {LINKS.map((link) => {
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
