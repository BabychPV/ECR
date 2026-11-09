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

/** Мить, з якої місяць заблоковано (для «Locked since …»): строк перевідкриття, інакше жорстке закриття. */
export function lockedSinceOf(period: PeriodMoment): string {
  return period.reopenedUntil ?? period.endsAt;
}
