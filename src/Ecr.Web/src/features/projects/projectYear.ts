import type { PagedProjects, PeriodCalendarDto } from '@/api/types';
import { formatMonthName } from '@/shared/format';

/**
 * ✎ Projects (макет `projects-documents-1108.html`): чисті правила «проєкт = звітний рік» для переліку проєктів,
 * вкладки Lifecycle і переліку документів. Стан місяців — ЛИШЕ з календаря сервера (`GET /projects/{id}/periods`),
 * клієнт його не вираховує з дат: збережений сервером стан і є правдою (див. `PeriodsPage`).
 */

type Period = PeriodCalendarDto['periods'][number];
export type ProjectSummary = PagedProjects['items'][number];

/** Місяць приймає дані: `Open` або `Grace` (пізні правки позначаються, `D-70`). */
export function acceptsData(period: Pick<Period, 'state'>): boolean {
  return period.state === 'Open' || period.state === 'Grace';
}

/** Роки, які покриває календар проєкту (за зростанням). */
export function calendarYears(calendar: Pick<PeriodCalendarDto, 'periods'> | undefined): number[] {
  return [...new Set((calendar?.periods ?? []).map((period) => period.year))].sort((a, b) => a - b);
}

/**
 * Роки для фільтра «Year» переліку документів: ЛИШЕ роки, у яких є проєкт (рядок g1), від новішого.
 * `selected` (рік з адреси) лишається у варіантах, навіть якщо проєкту в ньому вже немає, — інакше поле показувало б
 * не той рік, за який зроблено запит.
 */
export function yearsWithProjects(
  calendars: readonly (Pick<PeriodCalendarDto, 'periods'> | undefined)[],
  selected: number | null,
): number[] {
  const years = new Set(calendars.flatMap((calendar) => calendarYears(calendar)));
  if (selected !== null) years.add(selected);

  return [...years].sort((a, b) => b - a);
}

/** Короткий підпис місяця («Oct») — для переліків місяців у рядку. */
export function shortMonth(sequence: number): string {
  return formatMonthName(sequence).slice(0, 3);
}

/** «Oct, Nov 2026» — місяці одного року; порожньо — `''`. */
export function monthList(periods: readonly Pick<Period, 'sequence' | 'year'>[]): string {
  if (periods.length === 0) return '';
  const year = periods[periods.length - 1]!.year;

  return `${periods.map((period) => shortMonth(period.sequence)).join(', ')} ${String(year)}`;
}

/** Мить, з якої місяць блокується: перевідкритий — за строком перевідкриття (`D-68`), решта — `endsAt`. */
export function lockMoment(period: Pick<Period, 'state' | 'endsAt' | 'reopenedUntil'>): string {
  const reopened = period.reopenedUntil ?? null;

  return reopened !== null && period.state !== 'Closed' ? reopened : period.endsAt;
}

/** Підсумок стану місяців проєкту для рядка переліку (колонка «State»). */
export type ProjectMonthsState =
  | { readonly kind: 'open'; readonly months: readonly Period[] }
  | { readonly kind: 'allLocked' }
  | { readonly kind: 'notStarted' }
  | { readonly kind: 'unknown' };

export function projectMonthsState(
  project: Pick<ProjectSummary, 'status'>,
  calendar: Pick<PeriodCalendarDto, 'periods'> | undefined,
): ProjectMonthsState {
  if (project.status === 'Draft') return { kind: 'notStarted' };
  if (calendar === undefined) return { kind: 'unknown' };

  const open = calendar.periods.filter(acceptsData);
  if (project.status === 'Active' && open.length > 0) return { kind: 'open', months: open };

  return calendar.periods.every((period) => period.state === 'Closed') && calendar.periods.length > 0
    ? { kind: 'allLocked' }
    : project.status === 'Active'
      ? { kind: 'open', months: [] }
      : { kind: 'allLocked' };
}

/** Наступна мить блокування відкритого місяця (колонка «Closes»): найраніша серед місяців, що приймають дані. */
export function nextLock(calendar: Pick<PeriodCalendarDto, 'periods'> | undefined): Period | null {
  const open = (calendar?.periods ?? []).filter(acceptsData);
  if (open.length === 0) return null;

  return [...open].sort((a, b) => Date.parse(lockMoment(a)) - Date.parse(lockMoment(b)))[0] ?? null;
}

/**
 * Причина, чому архівувати не можна (рядок c3), або `null` — можна. Порядок перевірок — як у сервера
 * (`D-123`: лише активний, лише коли всі періоди закриті); невідомий календар — заборона, а не дозвіл.
 */
export type ArchiveBlock =
  | { readonly kind: 'archived' }
  | { readonly kind: 'notActive'; readonly status: string }
  | { readonly kind: 'openMonths'; readonly months: readonly Period[] }
  | { readonly kind: 'unknown' };

export function archiveBlock(
  project: Pick<ProjectSummary, 'status'>,
  calendar: Pick<PeriodCalendarDto, 'periods'> | undefined,
): ArchiveBlock | null {
  if (project.status === 'Archived') return { kind: 'archived' };
  if (project.status !== 'Active') return { kind: 'notActive', status: project.status };
  if (calendar === undefined) return { kind: 'unknown' };

  const notLocked = calendar.periods.filter((period) => period.state !== 'Closed');
  if (notLocked.length === 0) return null;
  const open = notLocked.filter(acceptsData);

  return { kind: 'openMonths', months: open.length > 0 ? open : notLocked };
}

/** Видалити можна лише чернетку без документів (рядки c1/c2); `documents` невідомо — заборона. */
export function deleteAllowed(project: Pick<ProjectSummary, 'status'>, documents: number | null): boolean {
  return project.status === 'Draft' && documents === 0;
}
