/**
 * Series constants and the grouping rule charts depend on.
 *
 * The keys mirror `DataIntelligence.Core.Analytics.SeriesCatalog`, which is the authority. They
 * are repeated here only so a page can name a default without first fetching the catalogue to
 * find out what exists; anything a user picks comes from the API.
 */

import type { TrendSeriesDto } from "@/types/api";

export const CPI_SERIES_KEY = "cpi";
export const SOFR_SERIES_KEY = "sofr";
export const SOFR_VOLUME_SERIES_KEY = "sofr.volume";

/** SOFR's distribution, in the order a chart should stack them. */
export const SOFR_PERCENTILE_KEYS = [
  "sofr.p1",
  "sofr.p25",
  "sofr.p75",
  "sofr.p99",
] as const;

/** The headline tiles on the landing page: one measure from each dataset, plus SOFR's volume. */
export const DASHBOARD_KPI_KEYS = [
  CPI_SERIES_KEY,
  SOFR_SERIES_KEY,
  SOFR_VOLUME_SERIES_KEY,
] as const;

/**
 * Line colours, assigned by position within a chart.
 *
 * Chosen to stay distinguishable on both the light and dark background, and to survive the
 * common forms of colour blindness — a chart legend that only works for some readers is a chart
 * with no legend.
 */
export const SERIES_COLORS = [
  "#2563eb",
  "#db2777",
  "#059669",
  "#d97706",
  "#7c3aed",
  "#0891b2",
  "#dc2626",
  "#4d7c0f",
  "#c026d3",
  "#475569",
] as const;

export function seriesColor(index: number): string {
  return SERIES_COLORS[index % SERIES_COLORS.length];
}

/**
 * Splits trend lines into one group per unit.
 *
 * Values are stored exactly as published and never rescaled, so SOFR volume (billions of dollars)
 * and the CPI index share no axis that means anything. Drawing them together produces a chart
 * that looks fine and says nothing, which is worse than two charts.
 */
export function groupByUnit(
  series: readonly TrendSeriesDto[],
): { unit: string; series: TrendSeriesDto[] }[] {
  const groups = new Map<string, TrendSeriesDto[]>();

  for (const line of series) {
    const existing = groups.get(line.unit);

    if (existing) {
      existing.push(line);
    } else {
      groups.set(line.unit, [line]);
    }
  }

  return [...groups].map(([unit, lines]) => ({ unit, series: lines }));
}

/** Range presets offered on the trend controls, as whole months back from today. */
export const RANGE_PRESETS = [
  { label: "3M", months: 3 },
  { label: "6M", months: 6 },
  { label: "1Y", months: 12 },
  { label: "2Y", months: 24 },
  { label: "5Y", months: 60 },
] as const;

export const DEFAULT_RANGE_MONTHS = 12;
