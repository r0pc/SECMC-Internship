export default function Home() {
  return (
    <div className="flex flex-1 flex-col items-center justify-center bg-zinc-50 px-8 font-sans dark:bg-black">
      <main className="w-full max-w-2xl space-y-4">
        <h1 className="text-3xl font-semibold tracking-tight text-black dark:text-zinc-50">
          Data Intelligence Platform
        </h1>
        <p className="text-lg leading-8 text-zinc-600 dark:text-zinc-400">
          Frontend scaffold. Dashboards, KPI tiles, trend charts, and the AI
          query assistant are built in Phase 4.
        </p>
      </main>
    </div>
  );
}
