import Link from "next/link";

import { type SearchParams, buildHref } from "@/lib/url";

export interface FilterOption {
  label: string;
  /** `null` clears the parameter — the "all" or "default" choice. */
  value: string | null;
  title?: string;
  /**
   * Parameters to set instead of `paramKey`, for a choice that is not one parameter.
   *
   * The collection log's "Failures" is the case that needs it: the API expresses it as
   * `failuresOnly=true` rather than as a status, so picking it has to set one parameter and clear
   * another. `value` still identifies the option for the active check.
   */
  params?: Record<string, string | null>;
}

/**
 * A segmented control made of links.
 *
 * Filters belong in the URL (see `lib/url`), so the control that sets one is a set of links and
 * needs no client JavaScript. Changing a filter resets the page number: staying on page 7 of a
 * result set that just shrank to two pages shows the reader an empty table and no reason why.
 */
export function FilterLinks({
  label,
  paramKey,
  options,
  active,
  pathname,
  searchParams,
  resetKeys = ["page"],
}: {
  label: string;
  paramKey: string;
  options: readonly FilterOption[];
  /** The current value, or null when the parameter is absent. */
  active: string | null;
  pathname: string;
  searchParams: SearchParams;
  resetKeys?: readonly string[];
}) {
  const cleared = Object.fromEntries(resetKeys.map((key) => [key, null]));

  return (
    <div className="flex flex-wrap items-center gap-2">
      <span className="text-xs font-medium uppercase tracking-wide text-zinc-500 dark:text-zinc-400">
        {label}
      </span>
      <div className="flex flex-wrap items-center gap-1">
        {options.map((option) => {
          const selected = option.value === active;

          return (
            <Link
              key={option.label}
              href={buildHref(pathname, searchParams, {
                ...cleared,
                [paramKey]: null,
                ...(option.params ?? { [paramKey]: option.value }),
              })}
              title={option.title}
              aria-current={selected ? "true" : undefined}
              className={`rounded-md border px-2.5 py-1 text-xs font-medium transition-colors ${
                selected
                  ? "border-zinc-900 bg-zinc-900 text-white dark:border-zinc-100 dark:bg-zinc-100 dark:text-zinc-900"
                  : "border-zinc-200 text-zinc-600 hover:bg-zinc-50 dark:border-zinc-800 dark:text-zinc-400 dark:hover:bg-zinc-900"
              }`}
            >
              {option.label}
            </Link>
          );
        })}
      </div>
    </div>
  );
}
