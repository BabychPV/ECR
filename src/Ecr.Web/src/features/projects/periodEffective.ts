/**
 * ✎ UAT 08.11 (e2): ЕФЕКТИВНИЙ стан місяця на клієнті — той самий, що бере рішення про запис на сервері (F-08).
 *
 * ⛔ Сторінка Periods показувала «Open · closes today» для місяців, заблокованих ще в березні: збережений стан
 * лишався `Open`, доки задача станів не відпрацює, а число днів до минулої межі обрізалось до нуля. Сервер тепер
 * доганяє стан при збереженні політики, але між прогонами задачі (і на старих даних) вирішує дата: межа блокування
 * минула, а перевідкриття немає або теж минуло — місяць заблоковано.
 */
export interface PeriodMoment {
  readonly state: string;
  /** Жорстке закриття (виключна межа). */
  readonly endsAt: string;
  readonly reopenedUntil?: string | null | undefined;
}

/** Стан для показу: збережений, або `Closed`, якщо межа блокування вже минула. */
export function effectivePeriodState(period: PeriodMoment, now: number): string {
  if (period.state !== 'Open' && period.state !== 'Grace') return period.state;

  const reopened = period.reopenedUntil ?? null;
  const lockAt = Date.parse(reopened ?? period.endsAt);

  return !Number.isNaN(lockAt) && lockAt <= now ? 'Closed' : period.state;
}

/**
 * Мить, з якої місяць заблоковано (для «Locked since …»): межа блокування самого періоду (`endsAt`).
 *
 * ✎ debt-1109 F13 (P3 B): тут стояв строк перевідкриття (`reopenedUntil ?? endsAt`), і після ДОСТРОКОВОГО
 * закриття перевідкриття плитка року казала «Locked since Nov 10» — дату закриття винятку, а не періоду.
 * Перевідкриття — окремий рядок плитки («reopened until …»), поки воно діє.
 */
export function lockedSinceOf(period: PeriodMoment): string {
  return period.endsAt;
}
