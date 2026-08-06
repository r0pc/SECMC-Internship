import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import Link from "next/link";

import { SiteNav } from "@/components/site-nav";

import "./globals.css";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  title: {
    default: "Data Intelligence Platform",
    template: "%s — Data Intelligence Platform",
  },
  description:
    "Automated collection of US economic data, with analytics dashboards and an AI query assistant.",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html
      lang="en"
      className={`${geistSans.variable} ${geistMono.variable} h-full antialiased`}
    >
      <body className="flex min-h-full flex-col bg-zinc-50 dark:bg-zinc-950">
        <header className="sticky top-0 z-10 border-b border-zinc-200 bg-white/90 backdrop-blur dark:border-zinc-800 dark:bg-zinc-950/90">
          <div className="mx-auto flex max-w-7xl flex-wrap items-center justify-between gap-4 px-6 py-3">
            <Link href="/" className="flex items-center gap-2">
              <span className="text-sm font-semibold tracking-tight text-zinc-900 dark:text-zinc-50">
                Data Intelligence
              </span>
              <span className="rounded bg-zinc-100 px-1.5 py-0.5 text-[10px] font-medium uppercase tracking-wide text-zinc-500 dark:bg-zinc-900 dark:text-zinc-400">
                Phase 4
              </span>
            </Link>
            <SiteNav />
          </div>
        </header>

        <main className="mx-auto w-full max-w-7xl flex-1 px-6 py-8">
          {children}
        </main>

        <footer className="border-t border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-950">
          <div className="mx-auto max-w-7xl px-6 py-6 text-xs leading-6 text-zinc-500 dark:text-zinc-400">
            {/* Attribution is a compliance obligation, not a courtesy (SOW 3). Both publishers are
                named on every page, and each source's terms-of-use link is on /sources. */}
            <p>
              Consumer Price Index data published by the{" "}
              <a
                className="underline hover:text-zinc-700 dark:hover:text-zinc-200"
                href="https://www.bls.gov/cpi/"
                target="_blank"
                rel="noreferrer"
              >
                U.S. Bureau of Labor Statistics
              </a>
              . SOFR published by the{" "}
              <a
                className="underline hover:text-zinc-700 dark:hover:text-zinc-200"
                href="https://www.newyorkfed.org/markets/reference-rates/sofr"
                target="_blank"
                rel="noreferrer"
              >
                Federal Reserve Bank of New York
              </a>
              . Values are stored and displayed exactly as published.
            </p>
            <p className="mt-2">
              All timestamps are UTC. Reference dates are the period a figure
              describes; collection timestamps are when this platform learned it.
            </p>
          </div>
        </footer>
      </body>
    </html>
  );
}
