import { Skeleton, SkeletonCard } from "@/components/states";

/**
 * Covers every route that has not declared its own.
 *
 * The shapes match the pages behind them — a heading, a tile row, a wide panel — so a navigation
 * settles into place rather than reflowing once the data lands.
 */
export default function Loading() {
  return (
    <div className="space-y-8">
      <div className="space-y-3">
        <Skeleton className="h-7 w-72" />
        <Skeleton className="h-4 w-full max-w-2xl" />
      </div>

      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <SkeletonCard rows={4} />
        <SkeletonCard rows={4} />
        <SkeletonCard rows={4} />
      </div>

      <SkeletonCard rows={8} />
    </div>
  );
}
