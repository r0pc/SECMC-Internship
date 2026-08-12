import type { Metadata } from "next";

import { LoginForm } from "./login-form";

/**
 * The sign-in page (FR-9).
 *
 * The only page in the app reachable without a session, and the only one that says nothing about
 * the platform's contents — no series names, no figures, no account addresses. What a stranger can
 * see here is that this is a Data Intelligence Platform and that it wants a password.
 */
export const metadata: Metadata = {
  title: "Sign in",
};

export default async function LoginPage({
  searchParams,
}: {
  searchParams: Promise<{ next?: string; reason?: string }>;
}) {
  const { next, reason } = await searchParams;

  return (
    <div className="mx-auto flex min-h-[60vh] w-full max-w-sm flex-col justify-center">
      <h1 className="text-2xl font-semibold tracking-tight text-zinc-900 dark:text-zinc-50">
        Sign in
      </h1>
      <p className="mt-2 text-sm leading-6 text-zinc-600 dark:text-zinc-400">
        {reason === "expired"
          ? // Said plainly, because the alternative reads as a bug. A session ends when it expires,
            // when an administrator disables the account, and when the password or roles change —
            // and from here there is no way to tell which, so the message does not pretend to.
            "That session has ended. Sign in again to continue."
          : "This platform is for named accounts. Ask an administrator if you need one."}
      </p>

      <div className="mt-8">
        <LoginForm next={next ?? "/"} />
      </div>

      <p className="mt-8 text-xs leading-5 text-zinc-500 dark:text-zinc-400">
        Consumer Price Index data published by the U.S. Bureau of Labor
        Statistics; SOFR published by the Federal Reserve Bank of New York.
      </p>
    </div>
  );
}
