import Link from "next/link";

import { formatCount } from "@/lib/format";
import { type SearchParams, buildHref } from "@/lib/url";
import type { PagedResult } from "@/types/api";

/**
 * A pager, built from links.
 *
 * Every list endpoint returns the same `PagedResult` shape, so this one component serves all of
 * them. Links rather than buttons: the page is part of the URL, which means the browser's back
 * button works and a row someone found on page 4 can be linked to.
 */
export function Pagination({
  result,
  pathname,
  searchParams,
  itemLabel,
  pageKey = "page",
}: {
  result: Pick<
    PagedResult<unknown>,
    "page" | "pageSize" | "totalCount" | "totalPages" | "hasPreviousPage" | "hasNextPage"
  >;
  pathname: string;
  searchParams: SearchParams;
  /** Plural noun for the count, e.g. "runs". */
  itemLabel: string;
  pageKey?: string;
}) {
  const { page, pageSize, totalCount, totalPages } = result;

  if (totalCount === 0) {
    return null;
  }

  const firstOnPage = (page - 1) * pageSize + 1;
  const lastOnPage = Math.min(page * pageSize, totalCount);

  return (
    <nav
      aria-label="Pagination"
      className="flex flex-wrap items-center justify-between gap-3 border-t border-zinc-200 px-5 py-3 text-sm dark:border-zinc-800"
    >
      <p className="text-zinc-500 dark:text-zinc-400">
        {formatCount(firstOnPage)}–{formatCount(lastOnPage)} of{" "}
        {formatCount(totalCount)} {itemLabel}
        {totalPages > 1 ? (
          <span className="ml-2 text-zinc-400 dark:text-zinc-500">
            (page {formatCount(page)} of {formatCount(totalPages)})
          </span>
        ) : null}
      </p>

      {totalPages > 1 ? (
        <div className="flex items-center gap-2">
          <PageLink
            href={buildHref(pathname, searchParams, { [pageKey]: page - 1 })}
            enabled={result.hasPreviousPage}
          >
            Previous
          </PageLink>
          <PageLink
            href={buildHref(pathname, searchParams, { [pageKey]: page + 1 })}
            enabled={result.hasNextPage}
          >
            Next
          </PageLink>
        </div>
      ) : null}
    </nav>
  );
}

/** A disabled pager control is rendered as text, not a dead link. */
function PageLink({
  href,
  enabled,
  children,
}: {
  href: string;
  enabled: boolean;
  children: string;
}) {
  const base = "rounded-md border px-3 py-1.5 text-sm font-medium";

  if (!enabled) {
    return (
      <span
        aria-disabled
        className={`${base} border-zinc-200 text-zinc-400 dark:border-zinc-800 dark:text-zinc-600`}
      >
        {children}
      </span>
    );
  }

  return (
    <Link
      href={href}
      className={`${base} border-zinc-300 text-zinc-700 hover:bg-zinc-50 dark:border-zinc-700 dark:text-zinc-200 dark:hover:bg-zinc-900`}
    >
      {children}
    </Link>
  );
}
