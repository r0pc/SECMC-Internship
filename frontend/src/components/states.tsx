import type { ReactNode } from "react";

import type { ApiError } from "@/lib/api";

/**
 * A panel that failed to load, in place of the panel.
 *
 * Scoped rather than page-wide on purpose: the pages here read from several endpoints, and one
 * failing should cost that panel and no more. The API's ProblemDetails `detail` is shown verbatim
 * — it is written for this reader, and paraphrasing it would only lose the specifics.
 */
export function ApiErrorPanel({
  error,
  label,
}: {
  error: ApiError;
  label: string;
}) {
  const title = error.isUnreachable
    ? "API unreachable"
    : (error.problem.title ?? "Request failed");

  return (
    <div
      role="alert"
      className="rounded-lg border border-red-200 bg-red-50 px-5 py-4 dark:border-red-900 dark:bg-red-950/40"
    >
      <p className="text-sm font-semibold text-red-800 dark:text-red-300">
        {label} could not be loaded — {title}
        {error.status > 0 ? ` (${error.status})` : ""}
      </p>
      {error.problem.detail ? (
        <p className="mt-1 text-sm leading-6 text-red-700 dark:text-red-400">
          {error.problem.detail}
        </p>
      ) : null}
    </div>
  );
}

/** Nothing matched, or nothing has been collected yet. Distinct from an error, and says so. */
export function EmptyState({
  title,
  description,
  action,
}: {
  title: string;
  description?: ReactNode;
  action?: ReactNode;
}) {
  return (
    <div className="rounded-lg border border-dashed border-zinc-300 px-5 py-10 text-center dark:border-zinc-700">
      <p className="text-sm font-medium text-zinc-700 dark:text-zinc-300">
        {title}
      </p>
      {description ? (
        <p className="mx-auto mt-1 max-w-md text-sm text-zinc-500 dark:text-zinc-400">
          {description}
        </p>
      ) : null}
      {action ? <div className="mt-4">{action}</div> : null}
    </div>
  );
}

/** Placeholder blocks that hold a panel's shape while its data streams in. */
export function Skeleton({ className = "h-4 w-full" }: { className?: string }) {
  return (
    <div
      aria-hidden
      className={`animate-pulse rounded bg-zinc-200 dark:bg-zinc-800 ${className}`}
    />
  );
}

export function SkeletonCard({ rows = 3 }: { rows?: number }) {
  return (
    <div className="space-y-3 rounded-lg border border-zinc-200 px-5 py-4 dark:border-zinc-800">
      <Skeleton className="h-4 w-40" />
      {Array.from({ length: rows }, (_, index) => (
        <Skeleton key={index} className="h-3 w-full" />
      ))}
    </div>
  );
}
