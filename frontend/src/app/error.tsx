"use client";

import { useEffect } from "react";

/**
 * The last resort.
 *
 * Panels handle their own API failures (see `attempt` and `ApiErrorPanel`), so reaching this
 * boundary means something the app did not anticipate — which is why the retry re-fetches rather
 * than merely re-rendering. `unstable_retry` replaced `reset` for exactly this: clearing the
 * error state without re-running the fetch would land straight back here.
 */
export default function Error({
  error,
  unstable_retry,
}: {
  error: Error & { digest?: string };
  unstable_retry: () => void;
}) {
  useEffect(() => {
    console.error(error);
  }, [error]);

  return (
    <div className="mx-auto max-w-xl py-16 text-center">
      <h1 className="text-xl font-semibold text-zinc-900 dark:text-zinc-50">
        Something went wrong
      </h1>
      <p className="mt-3 text-sm leading-6 text-zinc-600 dark:text-zinc-400">
        {error.message ||
          "The page could not be rendered. The API may be unavailable."}
      </p>
      {error.digest ? (
        <p className="mt-2 font-mono text-xs text-zinc-400 dark:text-zinc-500">
          Digest {error.digest}
        </p>
      ) : null}
      <button
        type="button"
        onClick={() => unstable_retry()}
        className="mt-6 rounded-md bg-zinc-900 px-4 py-2 text-sm font-medium text-white hover:bg-zinc-700 dark:bg-zinc-100 dark:text-zinc-900 dark:hover:bg-zinc-300"
      >
        Try again
      </button>
    </div>
  );
}
