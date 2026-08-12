"use client";

import { useActionState } from "react";

import { signIn, type LoginState } from "./actions";

/**
 * The sign-in form.
 *
 * A Client Component only for `useActionState`, which is what shows the failure message without
 * losing what was typed and disables the button while the round trip is in flight. The action it
 * calls runs on the server; the password is never held in client state.
 */
export function LoginForm({ next }: { next: string }) {
  const [state, action, pending] = useActionState<LoginState, FormData>(
    signIn,
    {},
  );

  return (
    <form action={action} className="space-y-5">
      {/* Carried through the form rather than read from the URL in the action: a Server Function
          has no access to the page's query string. */}
      <input type="hidden" name="next" value={next} />

      {state.error ? (
        <p
          role="alert"
          className="rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/40 dark:text-red-300"
        >
          {state.error}
        </p>
      ) : null}

      <div>
        <label
          htmlFor="email"
          className="block text-sm font-medium text-zinc-700 dark:text-zinc-300"
        >
          Email
        </label>
        <input
          id="email"
          name="email"
          type="email"
          autoComplete="username"
          required
          autoFocus
          defaultValue={state.email}
          className="mt-1.5 w-full rounded-md border border-zinc-300 bg-white px-3 py-2 text-sm text-zinc-900 outline-none focus:border-zinc-500 focus:ring-2 focus:ring-zinc-200 dark:border-zinc-700 dark:bg-zinc-950 dark:text-zinc-100 dark:focus:ring-zinc-800"
        />
      </div>

      <div>
        <label
          htmlFor="password"
          className="block text-sm font-medium text-zinc-700 dark:text-zinc-300"
        >
          Password
        </label>
        <input
          id="password"
          name="password"
          type="password"
          autoComplete="current-password"
          required
          className="mt-1.5 w-full rounded-md border border-zinc-300 bg-white px-3 py-2 text-sm text-zinc-900 outline-none focus:border-zinc-500 focus:ring-2 focus:ring-zinc-200 dark:border-zinc-700 dark:bg-zinc-950 dark:text-zinc-100 dark:focus:ring-zinc-800"
        />
      </div>

      <button
        type="submit"
        disabled={pending}
        className="w-full rounded-md bg-zinc-900 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-zinc-700 disabled:cursor-not-allowed disabled:opacity-60 dark:bg-zinc-100 dark:text-zinc-900 dark:hover:bg-zinc-300"
      >
        {pending ? "Signing in…" : "Sign in"}
      </button>
    </form>
  );
}
