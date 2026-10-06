/** Лише потрібне з рядка календаря проєкту (`PeriodCalendarDto.periods`). */
interface PeriodLike {
  periodKey: number;
  state: string;
}

/** Відкриті періоди проєкту за зростанням ключа. */
export function openPeriodKeys(periods: readonly PeriodLike[] | undefined): number[] {
  return (periods ?? [])
    .filter((period) => period.state === 'Open')
    .map((period) => period.periodKey)
    .sort((a, b) => a - b);
}

/**
 * A2-05: період, у якому відкривається щойно створений документ, — НАЙНОВІШИЙ відкритий період
 * проєкту, а не «поточний» (`isCurrent`, D-77), який може відставати від відкритого (поточний
 * 202609 при відкритому 202610). `null` — відкритих періодів немає (лишається типова поведінка
 * сторінки документа).
 */
export function newestOpenPeriodKey(periods: readonly PeriodLike[] | undefined): number | null {
  return openPeriodKeys(periods).at(-1) ?? null;
}
