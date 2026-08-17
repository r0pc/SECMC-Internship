import { formatValue } from "@/lib/format";
import { seriesColor } from "@/lib/series";
import type { TrendPointDto, TrendSeriesDto } from "@/types/api";

import { EmptyState } from "./states";

/**
 * A trend chart, drawn as plain SVG on the server.
 *
 * No charting library and no client JavaScript: the whole chart is in the first HTML response,
 * which is most of the 3-second dashboard budget (NFR Performance) given away for free, and it
 * keeps the frontend's dependency surface at Next and React.
 *
 * Every series passed in must share a unit — see `groupByUnit`. The component cannot check this
 * for the caller in any useful way, but it does label the axis with the unit it was given, so a
 * mixed-unit chart is at least visibly mislabelled rather than quietly wrong.
 */

const WIDTH = 900;
const PADDING = { top: 14, right: 18, bottom: 30, left: 66 } as const;

export function TrendChart({
  series,
  unit,
  height = 300,
}: {
  series: readonly TrendSeriesDto[];
  unit: string;
  height?: number;
}) {
  const drawable = series.filter((line) => line.points.length > 0);

  if (drawable.length === 0) {
    return (
      <EmptyState
        title="No observations in this range"
        description="Widen the date range, or check the collection log to see whether the source has reported yet."
      />
    );
  }

  const plot = {
    x: PADDING.left,
    y: PADDING.top,
    width: WIDTH - PADDING.left - PADDING.right,
    height: height - PADDING.top - PADDING.bottom,
  };

  const times = drawable.flatMap((line) =>
    line.points.map((point) => toTime(point.bucketStart)),
  );
  const minTime = Math.min(...times);
  const maxTime = Math.max(...times);
  const timeSpan = maxTime - minTime;

  const lows = drawable.flatMap((line) =>
    line.points.map((point) => point.minimum),
  );
  const highs = drawable.flatMap((line) =>
    line.points.map((point) => point.maximum),
  );

  const scale = niceScale(Math.min(...lows), Math.max(...highs));
  const valueSpan = scale.max - scale.min || 1;

  // A chart with one observation, or with several sharing a bucket, has no span to place anything
  // along. Centring it reads as the single reading it is; dividing by a span of 1ms pinned it to
  // the left edge, where it looked like the start of a line whose rest had failed to load.
  const toX = (time: number) =>
    timeSpan === 0
      ? plot.x + plot.width / 2
      : plot.x + ((time - minTime) / timeSpan) * plot.width;
  const toY = (value: number) =>
    plot.y + plot.height - ((value - scale.min) / valueSpan) * plot.height;

  const xTicks = buildTimeTicks(minTime, maxTime);
  const decimals = axisDecimals(scale.step, drawable[0].decimalPlaces);

  // One marker per observation is legible up to a point, and past it turns the line into a
  // caterpillar. Beyond that the line alone carries the shape.
  const showMarkers = drawable.every((line) => line.points.length <= 60);

  const label =
    drawable.length === 1
      ? `${drawable[0].title}, ${unit}`
      : `${drawable.map((line) => line.title).join("; ")} — ${unit}`;

  return (
    <figure className="m-0">
      <svg
        viewBox={`0 0 ${WIDTH} ${height}`}
        role="img"
        aria-label={label}
        className="h-auto w-full"
      >
        <title>{label}</title>

        {scale.ticks.map((tick) => (
          <g key={tick}>
            <line
              x1={plot.x}
              x2={plot.x + plot.width}
              y1={round(toY(tick))}
              y2={round(toY(tick))}
              className="stroke-zinc-200 dark:stroke-zinc-800"
              strokeWidth={1}
            />
            <text
              x={plot.x - 10}
              y={round(toY(tick)) + 4}
              textAnchor="end"
              className="fill-zinc-500 text-[11px] tabular-nums dark:fill-zinc-400"
            >
              {formatValue(tick, decimals)}
            </text>
          </g>
        ))}

        {xTicks.map((tick) => (
          <text
            key={tick.time}
            x={round(toX(tick.time))}
            y={height - 10}
            textAnchor="middle"
            className="fill-zinc-500 text-[11px] dark:fill-zinc-400"
          >
            {tick.label}
          </text>
        ))}

        {drawable.map((line, index) => {
          const color = seriesColor(index);
          const banded = line.points.some(
            (point) => point.observationCount > 1,
          );

          return (
            <g key={line.seriesKey}>
              {/* Where a bucket aggregates several observations, the band is the spread the mean
                  is hiding. Without it, a monthly average of business-daily SOFR reads as though
                  the rate held steady all month. */}
              {banded ? (
                <path
                  d={bandPath(line.points, toX, toY)}
                  fill={color}
                  fillOpacity={0.12}
                  stroke="none"
                />
              ) : null}

              <path
                d={linePath(line.points, toX, toY)}
                fill="none"
                stroke={color}
                strokeWidth={2}
                strokeLinecap="round"
                strokeLinejoin="round"
              />

              {showMarkers
                ? line.points.map((point) => (
                    <circle
                      key={point.bucketStart}
                      cx={round(toX(toTime(point.bucketStart)))}
                      cy={round(toY(point.value))}
                      r={2.5}
                      fill={color}
                    >
                      <title>
                        {`${line.title} — ${point.bucketStart}: ${formatValue(
                          point.value,
                          line.decimalPlaces,
                        )} ${line.unit}`}
                      </title>
                    </circle>
                  ))
                : null}
            </g>
          );
        })}

        <line
          x1={plot.x}
          x2={plot.x + plot.width}
          y1={plot.y + plot.height}
          y2={plot.y + plot.height}
          className="stroke-zinc-300 dark:stroke-zinc-700"
          strokeWidth={1}
        />
      </svg>

      <figcaption className="mt-3 flex flex-wrap items-center gap-x-5 gap-y-2 text-xs text-zinc-600 dark:text-zinc-400">
        {drawable.map((line, index) => (
          <span key={line.seriesKey} className="inline-flex items-center gap-2">
            <span
              aria-hidden
              className="inline-block h-2.5 w-2.5 rounded-full"
              style={{ backgroundColor: seriesColor(index) }}
            />
            {line.title}
          </span>
        ))}
        <span className="ml-auto">{unit}</span>
      </figcaption>
    </figure>
  );
}

// ---------------------------------------------------------------------------
// Geometry
// ---------------------------------------------------------------------------

/** Bucket starts are `DateOnly`, so they parse as UTC midnight and need no zone handling. */
function toTime(date: string): number {
  return new Date(`${date}T00:00:00Z`).getTime();
}

function round(value: number): number {
  return Math.round(value * 100) / 100;
}

function linePath(
  points: readonly TrendPointDto[],
  toX: (time: number) => number,
  toY: (value: number) => number,
): string {
  return points
    .map(
      (point, index) =>
        `${index === 0 ? "M" : "L"}${round(toX(toTime(point.bucketStart)))} ${round(
          toY(point.value),
        )}`,
    )
    .join(" ");
}

/** Maxima left to right, then minima right to left, closed — the spread within each bucket. */
function bandPath(
  points: readonly TrendPointDto[],
  toX: (time: number) => number,
  toY: (value: number) => number,
): string {
  const top = points.map(
    (point) =>
      `${round(toX(toTime(point.bucketStart)))} ${round(toY(point.maximum))}`,
  );

  const bottom = [...points]
    .reverse()
    .map(
      (point) =>
        `${round(toX(toTime(point.bucketStart)))} ${round(toY(point.minimum))}`,
    );

  return `M${top.join(" L")} L${bottom.join(" L")} Z`;
}

/**
 * A value axis with round numbers on it.
 *
 * The domain is widened to the tick boundaries rather than clipped to the data, so the top and
 * bottom gridlines are labelled values a reader can subtract in their head.
 */
function niceScale(
  dataMin: number,
  dataMax: number,
  targetTicks = 5,
): { min: number; max: number; step: number; ticks: number[] } {
  // A flat series has no span to divide. Give it a symmetric window so the line lands mid-chart
  // instead of on the axis, which would read as though it were zero.
  if (dataMin === dataMax) {
    const pad = Math.abs(dataMin) * 0.05 || 1;
    dataMin -= pad;
    dataMax += pad;
  }

  const rawStep = (dataMax - dataMin) / targetTicks;
  const magnitude = 10 ** Math.floor(Math.log10(rawStep));
  const normalized = rawStep / magnitude;
  const step =
    (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) *
    magnitude;

  const min = Math.floor(dataMin / step) * step;
  const max = Math.ceil(dataMax / step) * step;

  const ticks: number[] = [];

  // Rounded at each step: repeated addition of a decimal step accumulates binary error, and an
  // axis labelled 2.9000000000000004 is its own bug report.
  for (let tick = min; tick <= max + step / 2; tick += step) {
    ticks.push(Number(tick.toFixed(10)));
  }

  return { min, max, step, ticks };
}

/** Enough decimals to tell one gridline from the next, and never more than the series publishes. */
function axisDecimals(step: number, seriesDecimals: number): number {
  const needed = Math.max(0, Math.ceil(-Math.log10(step)));
  return Math.min(seriesDecimals, needed);
}

const YEAR_LABEL = new Intl.DateTimeFormat("en-GB", {
  year: "numeric",
  timeZone: "UTC",
});

const MONTH_LABEL = new Intl.DateTimeFormat("en-GB", {
  month: "short",
  year: "numeric",
  timeZone: "UTC",
});

const DAY_LABEL = new Intl.DateTimeFormat("en-GB", {
  day: "numeric",
  month: "short",
  timeZone: "UTC",
});

const DAY_MS = 86_400_000;

/**
 * Up to six evenly spaced date labels, at whatever precision the span justifies. Labelling a
 * five-year chart by day, or a two-week one by year, both cost the reader the same thing.
 */
function buildTimeTicks(
  minTime: number,
  maxTime: number,
): { time: number; label: string }[] {
  const spanDays = (maxTime - minTime) / DAY_MS;
  const format =
    spanDays > 1200 ? YEAR_LABEL : spanDays > 200 ? MONTH_LABEL : DAY_LABEL;

  if (maxTime === minTime) {
    return [{ time: minTime, label: format.format(new Date(minTime)) }];
  }

  const count = 5;

  return Array.from({ length: count + 1 }, (_, index) => {
    const time = minTime + ((maxTime - minTime) * index) / count;
    return { time, label: format.format(new Date(time)) };
  });
}
