import type { PeriodCalendarDto } from '@/api/types';
import { zoneClock } from '@/shared/siteMoment';

/*
 * ✎ Приймання 08.11, рядок f5 («policy with a date preview»): дати, які дасть політика, — ЖИВЦЕМ, поки адміністратор
 * міняє зсуви, без запиту на сервер.
 *
 * ⛔ Правило — рівно серверне, `Period.RecomputeBoundaries` (Ecr.Domain/Entities/Documents/Period.cs):
 *   ComputedOpenAt  = опівніч (PeriodStart + OpenOffsetDays)      у поясі майданчика;
 *   ComputedGraceAt = опівніч (PeriodEnd   + GraceOffsetDays)     — з цієї миті правка позначається пізньою;
 *   ComputedCloseAt = опівніч (PeriodEnd   + HardCloseOffsetDays) — з цієї миті місяць закрито.
 * Вікно року — `YearGraceWindow.For` (Ecr.Domain/Services/PeriodStateCalculator.cs): останній день правок —
 * `Project.PeriodEnd + max(0, YearGraceOffsetDays)` включно.
 *
 * ⚠ Кожна межа — ОПІВНІЧ КАЛЕНДАРНОЇ ДАТИ в поясі майданчика, тож на екрані вона — сама ця дата. Тому тут рахуються
 * лише дати (`yyyy-MM-dd`, арифметика в UTC без переходів часу), а пояс потрібен один раз — щоб прочитати дату
 * початку першого періоду з `startsAt`. Показ — `formatDate` з `timeZone: 'UTC'` над тією самою датою: та сама
 * дата, що й «Locked since …» на вкладці Periods (`siteMomentText`, пояс майданчика).
 */

/** Чотири зсуви політики (чернетка в діалозі або збережена політика). */
export interface PolicyOffsets {
  readonly openOffsetDays: number;
  readonly graceOffsetDays: number;
  readonly hardCloseOffsetDays: number;
  readonly yearGraceOffsetDays: number;
}

/** Період для прев'ю: ключ і календарні межі (`yyyy-MM-dd`, обидві включно). */
export interface PreviewPeriod {
  readonly periodKey: number;
  readonly periodStart: string;
  readonly periodEnd: string;
}

/** Дати однієї політики для одного періоду (`yyyy-MM-dd` за майданчиком). */
export interface PreviewDates {
  readonly periodKey: number;
  /** Відкривається (стан Open) — `PeriodStart + OpenOffsetDays`. */
  readonly opensOn: string;
  /** Правки позначаються пізніми (стан Grace) — `PeriodEnd + GraceOffsetDays`. */
  readonly lateFrom: string;
  /** Закривається (стан Closed) — `PeriodEnd + HardCloseOffsetDays`. */
  readonly locksOn: string;
}

const IsoDate = /^(\d{4})-(\d{2})-(\d{2})/u;

function pad(part: number, width: number): string {
  return String(part).padStart(width, '0');
}

function toIso(at: Date): string {
  return `${pad(at.getUTCFullYear(), 4)}-${pad(at.getUTCMonth() + 1, 2)}-${pad(at.getUTCDate(), 2)}`;
}

/** Опівніч UTC дати `yyyy-MM-dd`; `null` — не дата. */
export function utcMidnight(isoDate: string): Date | null {
  const parts = IsoDate.exec(isoDate);
  if (parts === null) return null;

  return new Date(Date.UTC(Number(parts[1]), Number(parts[2]) - 1, Number(parts[3])));
}

/** `DateOnly.AddDays`: календарна дата плюс `days` діб; не дата — як є. */
export function addDays(isoDate: string, days: number): string {
  const at = utcMidnight(isoDate);
  if (at === null) return isoDate;

  at.setUTCDate(at.getUTCDate() + Math.trunc(days));

  return toIso(at);
}

/** Момент із сервера → календарна дата майданчика (`yyyy-MM-dd`); спершу дата з самого рядка зі зсувом. */
export function siteDateOf(value: string, timeZoneId: string): string | null {
  // Зсув у рядку — пояс, яким межу порахував сервер (як у `siteMomentText`): дата — його настінна.
  if (/T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?[+-]\d{2}:\d{2}$/u.test(value)) return IsoDate.exec(value)?.[0] ?? null;

  const at = Date.parse(value);
  if (Number.isNaN(at)) return null;

  const parts = Object.fromEntries(zoneClock(timeZoneId).formatToParts(new Date(at)).map((p) => [p.type, p.value]));
  return `${parts.year ?? ''}-${parts.month ?? ''}-${parts.day ?? ''}`;
}

/** Дати політики для періоду — `Period.RecomputeBoundaries`. */
export function policyDates(period: PreviewPeriod, offsets: PolicyOffsets): PreviewDates {
  return {
    periodKey: period.periodKey,
    opensOn: addDays(period.periodStart, offsets.openOffsetDays),
    lateFrom: addDays(period.periodEnd, offsets.graceOffsetDays),
    locksOn: addDays(period.periodEnd, offsets.hardCloseOffsetDays),
  };
}

/** Останній день правок після кінця року, включно — `YearGraceWindow.For` (`LastDay`). */
export function yearEditsUntil(projectEnd: string, offsets: PolicyOffsets): string {
  return addDays(projectEnd, Math.max(0, offsets.yearGraceOffsetDays));
}

/**
 * Поточний і наступний періоди проєкту з календаря.
 *
 * ⚠ `PeriodDto` не має `PeriodStart`. Періоди календаря суміжні (`PeriodCalendar.Bounds`), тож початок — день після
 * кінця попереднього; початок ПЕРШОГО — дата `startsAt` (`ComputedOpenAt`) мінус зсув відкриття ЧИННОЇ політики
 * проєкту, яким сервер її й рахував (`calendar.policy`).
 * Поточний — `isCurrent` (D-77); без нього — перший незакритий, інакше останній.
 */
export function previewPeriods(calendar: PeriodCalendarDto): PreviewPeriod[] {
  const sorted = [...calendar.periods].sort((a, b) => a.periodKey - b.periodKey);
  if (sorted.length === 0) return [];

  const startOf = (index: number): string | null => {
    if (index > 0) return addDays(sorted[index - 1]!.periodEnd, 1);
    const opened = siteDateOf(sorted[0]!.startsAt, calendar.timeZoneId);
    return opened === null ? null : addDays(opened, -calendar.policy.openOffsetDays);
  };

  let current = sorted.findIndex((period) => period.isCurrent);
  if (current < 0) current = sorted.findIndex((period) => period.state !== 'Closed');
  if (current < 0) current = sorted.length - 1;

  const result: PreviewPeriod[] = [];

  for (const index of [current, current + 1]) {
    const period = sorted[index];
    const start = period === undefined ? null : startOf(index);
    if (period !== undefined && start !== null) {
      result.push({ periodKey: period.periodKey, periodStart: start, periodEnd: IsoDate.exec(period.periodEnd)?.[0] ?? period.periodEnd });
    }
  }

  return result;
}

/** Кінець року проєкту — кінець останнього періоду календаря (`PeriodCalendar.Bounds` обрізає його `Project.PeriodEnd`). */
export function projectEndOf(calendar: PeriodCalendarDto): string | null {
  const last = [...calendar.periods].sort((a, b) => a.periodKey - b.periodKey).at(-1);
  return last === undefined ? null : (IsoDate.exec(last.periodEnd)?.[0] ?? null);
}

/** Зразок без проєкту: місяць, у якому `today` (`yyyy-MM-dd`), і наступний; кінець року — 31.12. */
export function samplePeriods(today: string): { readonly periods: PreviewPeriod[]; readonly projectEnd: string } {
  const at = utcMidnight(today) ?? new Date(Date.UTC(2026, 0, 1));
  const year = at.getUTCFullYear();
  const month = at.getUTCMonth();

  const monthOf = (y: number, m: number): PreviewPeriod => {
    const first = new Date(Date.UTC(y, m, 1));
    const last = new Date(Date.UTC(y, m + 1, 0));
    return { periodKey: first.getUTCFullYear() * 100 + first.getUTCMonth() + 1, periodStart: toIso(first), periodEnd: toIso(last) };
  };

  return { periods: [monthOf(year, month), monthOf(year, month + 1)], projectEnd: `${pad(year, 4)}-12-31` };
}
