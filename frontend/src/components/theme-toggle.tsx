"use client";

/**
 * Light/dark switch for the header.
 *
 * Deliberately stateless. The theme lives in one place — `data-theme` on <html>, set before first
 * paint by the inline script in the root layout — so React state would only be a second copy that
 * the server cannot render correctly anyway. The button reads the attribute when clicked, and
 * which icon shows is decided by CSS (`dark:`), which follows the same attribute. That means no
 * hydration mismatch and no wrong-icon flash on load.
 */
const STORAGE_KEY = "theme";

export function ThemeToggle() {
  function toggle() {
    const next =
      document.documentElement.dataset.theme === "dark" ? "light" : "dark";
    document.documentElement.dataset.theme = next;
    try {
      window.localStorage.setItem(STORAGE_KEY, next);
    } catch {
      // Private mode or a blocked-storage policy. The theme still applies for this page view;
      // it just will not be remembered, which is not worth failing the click over.
    }
  }

  return (
    <button
      type="button"
      onClick={toggle}
      title="Switch between light and dark mode"
      className="rounded-md p-1.5 text-zinc-600 transition-colors hover:bg-zinc-100 hover:text-zinc-900 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-zinc-900 dark:text-zinc-400 dark:hover:bg-zinc-900 dark:hover:text-zinc-100 dark:focus-visible:outline-zinc-100"
    >
      {/* Sun — shown in dark mode, where the button's job is to go back to light. */}
      <svg
        aria-hidden="true"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.75"
        strokeLinecap="round"
        strokeLinejoin="round"
        className="hidden size-5 dark:block"
      >
        <circle cx="12" cy="12" r="4" />
        <path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41" />
      </svg>
      {/* Moon — shown in light mode. */}
      <svg
        aria-hidden="true"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.75"
        strokeLinecap="round"
        strokeLinejoin="round"
        className="size-5 dark:hidden"
      >
        <path d="M21 12.79A9 9 0 1 1 11.21 3a7 7 0 0 0 9.79 9.79Z" />
      </svg>
      {/* The label has to name the destination, and the destination depends on the current theme.
          Same CSS trick as the icons so screen readers hear the accurate one. */}
      <span className="sr-only hidden dark:inline">Switch to light mode</span>
      <span className="sr-only dark:hidden">Switch to dark mode</span>
    </button>
  );
}
