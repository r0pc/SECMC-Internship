import Link from "next/link";

export default function NotFound() {
  return (
    <div className="mx-auto max-w-xl py-16 text-center">
      <h1 className="text-xl font-semibold text-zinc-900 dark:text-zinc-50">
        Not found
      </h1>
      <p className="mt-3 text-sm leading-6 text-zinc-600 dark:text-zinc-400">
        That page does not exist. If you followed a saved link to a series, the
        series key it names may no longer be in the catalogue.
      </p>
      <div className="mt-6 flex justify-center gap-3">
        <Link
          href="/"
          className="rounded-md bg-zinc-900 px-4 py-2 text-sm font-medium text-white hover:bg-zinc-700 dark:bg-zinc-100 dark:text-zinc-900 dark:hover:bg-zinc-300"
        >
          Dashboard
        </Link>
        <Link
          href="/series"
          className="rounded-md border border-zinc-300 px-4 py-2 text-sm font-medium text-zinc-700 hover:bg-zinc-50 dark:border-zinc-700 dark:text-zinc-200 dark:hover:bg-zinc-900"
        >
          Browse series
        </Link>
      </div>
    </div>
  );
}
