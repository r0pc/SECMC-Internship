import type { ReactNode } from "react";

/**
 * Table primitives.
 *
 * The wrapper scrolls horizontally rather than letting the table set the page width — the
 * collection log has enough columns to overflow a laptop, and a page that scrolls sideways as a
 * whole loses its navigation off the left edge.
 */
export function Table({
  children,
  caption,
}: {
  children: ReactNode;
  caption?: string;
}) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-full border-collapse text-left text-sm">
        {caption ? <caption className="sr-only">{caption}</caption> : null}
        {children}
      </table>
    </div>
  );
}

export function Th({
  children,
  numeric = false,
  className = "",
}: {
  children: ReactNode;
  numeric?: boolean;
  className?: string;
}) {
  return (
    <th
      scope="col"
      className={`whitespace-nowrap border-b border-zinc-200 px-4 py-2.5 text-xs font-semibold uppercase tracking-wide text-zinc-500 dark:border-zinc-800 dark:text-zinc-400 ${
        numeric ? "text-right" : ""
      } ${className}`}
    >
      {children}
    </th>
  );
}

export function Td({
  children,
  numeric = false,
  className = "",
}: {
  children: ReactNode;
  numeric?: boolean;
  className?: string;
}) {
  return (
    <td
      className={`border-b border-zinc-100 px-4 py-2.5 align-top text-zinc-700 dark:border-zinc-900 dark:text-zinc-300 ${
        numeric ? "text-right tabular-nums" : ""
      } ${className}`}
    >
      {children}
    </td>
  );
}

export function Tr({ children }: { children: ReactNode }) {
  return (
    <tr className="hover:bg-zinc-50 dark:hover:bg-zinc-900/60">{children}</tr>
  );
}
