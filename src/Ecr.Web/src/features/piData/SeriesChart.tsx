import { useMemo, useState, type JSX, type KeyboardEvent, type MouseEvent } from 'react';
import { Box, Text } from '@mantine/core';
import { useElementSize } from '@mantine/hooks';
import { formatDate, formatDateTime } from '@/shared/format/datetime';
import { formatNumber } from '@/shared/format/number';
import { t } from '@/shared/i18n';
import type { RawSeriesView } from './api';

/**
 * Графік згорнутого ряду PI (`D-463`): смуга «мін–макс» кошика і лінія середнього.
 *
 * ⛔ Малює рівно те, що прийшло з сервера: ≤ 2000 кошиків незалежно від довжини вікна (рік щохвилини — це
 * 525 600 точок, які браузер не мав би ні отримувати, ні малювати). Власної бібліотеки графіків у застосунку
 * немає, і заради однієї лінії її не заведено (бюджет `D-132`): чистий SVG у лінивому чанку.
 *
 * ⚠ Порожні кошики РОЗРИВАЮТЬ лінію: з'єднати точки через прогалину означало б намалювати дані, яких PI не
 * віддавав. Прогалини збору окремо називає секція покриття.
 *
 * ⚠ Доступність: графік — `role="img"` з підсумком словами; значення кошика під курсором (миша) чи стрілками
 * (клавіатура) читає живий регіон. Точні значення — у таблиці точок під графіком.
 */
export interface SeriesChartProps {
  readonly series: RawSeriesView;
  /** Одиниця джерела для підпису осі; `null` — не відома. */
  readonly unit?: string | null | undefined;
  readonly height?: number | undefined;
  /** Ширина до першого виміру контейнера (тести без розмітки); у браузері її одразу замінює вимір. */
  readonly initialWidth?: number | undefined;
}

const Pad = { left: 56, right: 12, top: 10, bottom: 26 } as const;

export function SeriesChart({ series, unit = null, height = 220, initialWidth = 0 }: SeriesChartProps): JSX.Element {
  const { ref, width: measured } = useElementSize();
  const width = measured > 0 ? measured : initialWidth;
  const [active, setActive] = useState<number | null>(null);

  const model = useMemo(() => build(series), [series]);
  const plotWidth = Math.max(0, width - Pad.left - Pad.right);
  const plotHeight = height - Pad.top - Pad.bottom;

  const x = (ms: number): number => Pad.left + ((ms - model.from) / Math.max(1, model.to - model.from)) * plotWidth;
  const y = (v: number): number =>
    Pad.top + plotHeight - ((v - model.min) / Math.max(Number.EPSILON, model.max - model.min)) * plotHeight;

  const summary =
    model.buckets.length === 0
      ? t('piData.chartEmpty')
      : t('piData.chartSummary', {
          points: series.numericPoints,
          min: format(model.min),
          max: format(model.max),
          unit: unit ?? '',
        });

  const activeBucket = active === null ? undefined : model.buckets[active];
  const halfBucket = (series.bucketSeconds * 1000) / 2;

  const onMove = (event: MouseEvent<SVGSVGElement>): void => {
    if (model.buckets.length === 0 || plotWidth <= 0) return;
    const box = event.currentTarget.getBoundingClientRect();
    const ms = model.from + ((event.clientX - box.left - Pad.left) / plotWidth) * (model.to - model.from);
    setActive(nearest(model.buckets, ms - halfBucket));
  };

  const onKey = (event: KeyboardEvent<SVGSVGElement>): void => {
    if (model.buckets.length === 0) return;
    const last = model.buckets.length - 1;
    const step = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0;
    if (event.key === 'Home') setActive(0);
    else if (event.key === 'End') setActive(last);
    else if (step !== 0) setActive((current) => Math.min(last, Math.max(0, (current ?? (step > 0 ? -1 : last + 1)) + step)));
    else return;
    event.preventDefault();
  };

  return (
    <Box ref={ref} data-pi-chart="" pos="relative">
      {width > 0 && (
        <svg
          width={width}
          height={height}
          role="img"
          aria-label={summary}
          tabIndex={model.buckets.length === 0 ? -1 : 0}
          onMouseMove={onMove}
          onMouseLeave={() => setActive(null)}
          onKeyDown={onKey}
          style={{ display: 'block', outlineOffset: 2 }}
        >
          {model.yTicks.map((tick) => (
            <g key={`y${String(tick)}`}>
              <line
                x1={Pad.left}
                x2={Pad.left + plotWidth}
                y1={y(tick)}
                y2={y(tick)}
                stroke="var(--mantine-color-default-border)"
                strokeDasharray="3 3"
              />
              <text x={Pad.left - 6} y={y(tick)} textAnchor="end" dominantBaseline="middle" fontSize={11} fill="var(--mantine-color-dimmed)">
                {format(tick)}
              </text>
            </g>
          ))}
          {model.xTicks.map((tick) => (
            <text
              key={`x${String(tick)}`}
              x={x(tick)}
              y={height - 8}
              textAnchor="middle"
              fontSize={11}
              fill="var(--mantine-color-dimmed)"
            >
              {formatTime(tick, model.to - model.from)}
            </text>
          ))}
          {model.segments.map((segment) => (
            <g key={`s${String(segment[0]?.start ?? 0)}`}>
              <path d={bandPath(segment, x, y, halfBucket)} fill="var(--mantine-primary-color-light)" stroke="none" />
              <path
                d={linePath(segment, x, y, halfBucket)}
                fill="none"
                stroke="var(--mantine-primary-color-filled)"
                strokeWidth={1.5}
                strokeLinejoin="round"
              />
            </g>
          ))}
          {activeBucket !== undefined && (
            <line
              x1={x(activeBucket.start + halfBucket)}
              x2={x(activeBucket.start + halfBucket)}
              y1={Pad.top}
              y2={Pad.top + plotHeight}
              stroke="var(--mantine-color-text)"
              strokeOpacity={0.5}
            />
          )}
        </svg>
      )}
      <Text size="xs" c="dimmed" aria-live="polite" mih={18} data-pi-chart-readout="">
        {activeBucket === undefined
          ? summary
          : t('piData.chartBucket', {
              at: formatTime(activeBucket.start, 0),
              avg: format(activeBucket.avg),
              min: format(activeBucket.min),
              max: format(activeBucket.max),
              points: activeBucket.points,
              unit: unit ?? '',
            })}
      </Text>
    </Box>
  );
}

interface Bucket {
  readonly start: number;
  readonly min: number;
  readonly max: number;
  readonly avg: number;
  readonly points: number;
}

interface Model {
  readonly from: number;
  readonly to: number;
  readonly min: number;
  readonly max: number;
  readonly buckets: readonly Bucket[];
  readonly segments: readonly (readonly Bucket[])[];
  readonly yTicks: readonly number[];
  readonly xTicks: readonly number[];
}

/** Числові кошики, сегменти без прогалин і поділки осей. Експортовано для тестів. */
export function build(series: RawSeriesView): Model {
  const from = Date.parse(series.fromUtc);
  const to = Date.parse(series.toUtc);
  const step = series.bucketSeconds * 1000;

  const buckets: Bucket[] = [];
  for (const raw of series.buckets) {
    if (raw.min == null || raw.max == null || raw.avg == null) continue;
    buckets.push({
      start: Date.parse(raw.startUtc),
      min: Number(raw.min),
      max: Number(raw.max),
      avg: Number(raw.avg),
      points: raw.points,
    });
  }

  const segments: Bucket[][] = [];
  for (const bucket of buckets) {
    const current = segments[segments.length - 1];
    const previous = current?.[current.length - 1];
    if (current !== undefined && previous !== undefined && bucket.start - previous.start <= step * 1.5) current.push(bucket);
    else segments.push([bucket]);
  }

  let min = Math.min(...buckets.map((b) => b.min));
  let max = Math.max(...buckets.map((b) => b.max));
  if (!Number.isFinite(min) || !Number.isFinite(max)) {
    min = 0;
    max = 1;
  } else if (min === max) {
    const pad = Math.abs(min) * 0.05 || 1;
    min -= pad;
    max += pad;
  }

  const yTicks = [0, 1, 2, 3, 4].map((i) => min + ((max - min) * i) / 4);
  const xTicks = [0, 1, 2, 3, 4].map((i) => from + ((to - from) * i) / 4);

  return { from, to, min, max, buckets, segments, yTicks, xTicks };
}

/** Найближчий кошик до моменту (за початком). */
export function nearest(buckets: readonly Bucket[], ms: number): number {
  let best = 0;
  let distance = Number.POSITIVE_INFINITY;
  buckets.forEach((bucket, index) => {
    const d = Math.abs(bucket.start - ms);
    if (d < distance) {
      distance = d;
      best = index;
    }
  });

  return best;
}

function linePath(segment: readonly Bucket[], x: (ms: number) => number, y: (v: number) => number, half: number): string {
  return segment.map((b, i) => `${i === 0 ? 'M' : 'L'}${x(b.start + half).toFixed(1)},${y(b.avg).toFixed(1)}`).join(' ');
}

function bandPath(segment: readonly Bucket[], x: (ms: number) => number, y: (v: number) => number, half: number): string {
  if (segment.length === 1) {
    const b = segment[0] as Bucket;
    const cx = x(b.start + half);
    return `M${(cx - 1).toFixed(1)},${y(b.max).toFixed(1)} L${(cx + 1).toFixed(1)},${y(b.max).toFixed(1)} L${(cx + 1).toFixed(1)},${y(b.min).toFixed(1)} L${(cx - 1).toFixed(1)},${y(b.min).toFixed(1)} Z`;
  }

  const top = segment.map((b, i) => `${i === 0 ? 'M' : 'L'}${x(b.start + half).toFixed(1)},${y(b.max).toFixed(1)}`);
  const bottom = [...segment].reverse().map((b) => `L${x(b.start + half).toFixed(1)},${y(b.min).toFixed(1)}`);

  return `${top.join(' ')} ${bottom.join(' ')} Z`;
}

function format(value: number): string {
  return formatNumber(value, { maximumSignificantDigits: 6 });
}

function formatTime(ms: number, range: number): string {
  const date = new Date(ms);

  // ⚠ Довге вікно — лише дата; коротке (≤ 2 доби) — і час: інакше п'ять поділок осі з однаковою датою.
  return range > 2 * 24 * 60 * 60 * 1000
    ? formatDate(date, { day: '2-digit', month: 'short', year: '2-digit' })
    : formatDateTime(date, { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
}
