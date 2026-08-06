import type { ReactNode } from "react";

/** A bordered panel. One card is one thing the reader is asked to look at. */
export function Card({
  children,
  className = "",
}: {
  children: ReactNode;
  className?: string;
}) {
  return (
    <section
      className={`rounded-lg border border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-950 ${className}`}
    >
      {children}
    </section>
  );
}

/**
 * A card's title row, with room for a control on the right.
 *
 * `hint` carries the caveat that belongs with the numbers — which window they cover, which unit
 * they are in. Kept beside the title rather than in a footnote, because a figure read without
 * its unit is a figure read wrong.
 */
export function CardHeader({
  title,
  hint,
  action,
}: {
  title: ReactNode;
  hint?: ReactNode;
  action?: ReactNode;
}) {
  return (
    <div className="flex flex-wrap items-start justify-between gap-3 border-b border-zinc-200 px-5 py-4 dark:border-zinc-800">
      <div className="min-w-0">
        <h2 className="text-sm font-semibold tracking-tight text-zinc-900 dark:text-zinc-100">
          {title}
        </h2>
        {hint ? (
          <p className="mt-1 text-xs text-zinc-500 dark:text-zinc-400">{hint}</p>
        ) : null}
      </div>
      {action ? <div className="shrink-0">{action}</div> : null}
    </div>
  );
}

export function CardBody({
  children,
  className = "",
}: {
  children: ReactNode;
  className?: string;
}) {
  return <div className={`px-5 py-4 ${className}`}>{children}</div>;
}

/** A page's heading block. */
export function PageHeader({
  title,
  description,
  action,
}: {
  title: string;
  description?: ReactNode;
  action?: ReactNode;
}) {
  return (
    <header className="flex flex-wrap items-end justify-between gap-4">
      <div className="min-w-0">
        <h1 className="text-2xl font-semibold tracking-tight text-zinc-900 dark:text-zinc-50">
          {title}
        </h1>
        {description ? (
          <p className="mt-2 max-w-3xl text-sm leading-6 text-zinc-600 dark:text-zinc-400">
            {description}
          </p>
        ) : null}
      </div>
      {action ? <div className="shrink-0">{action}</div> : null}
    </header>
  );
}

/** A label-over-value pair, for the small facts that surround a chart. */
export function Stat({
  label,
  value,
  hint,
}: {
  label: string;
  value: ReactNode;
  hint?: ReactNode;
}) {
  return (
    <div>
      <dt className="text-xs font-medium uppercase tracking-wide text-zinc-500 dark:text-zinc-400">
        {label}
      </dt>
      <dd className="mt-1 text-lg font-semibold tabular-nums text-zinc-900 dark:text-zinc-100">
        {value}
      </dd>
      {hint ? (
        <p className="mt-0.5 text-xs text-zinc-500 dark:text-zinc-400">{hint}</p>
      ) : null}
    </div>
  );
}
