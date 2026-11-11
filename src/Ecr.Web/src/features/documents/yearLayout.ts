import type { components } from '@/api/schema';

/**
 * ✎ D-474, п. 5: «вибір місяця в документі показується лише там, де документ має кілька копій».
 *
 * Документ, чия типова версія проєкту має розкладку `YearWithMonthColumns`, — ОДНА копія на рік: місяці живуть
 * колонками, кожна комірка належить рівно одному періоду (місячна колонка m — періоду m, не-місячний ввід — якорю
 * року). Перемикач місяця в шапці такого документа лише міняв би адресу без нової копії, тож його немає.
 *
 * ⚠ `null`/`PerMonthCopies` (і будь-яке невідоме значення) — поведінка як до D-474.
 */
export function isYearLayout(templateLayout: string | null | undefined): boolean {
  return templateLayout === 'YearWithMonthColumns';
}

type PeriodState = components['schemas']['PeriodState'];

/** Лише потрібне з рядка календаря проєкту (`PeriodCalendarDto.periods`). */
interface PeriodLike {
  periodKey: number;
  isCurrent?: boolean;
}

/**
 * Календар для вибору періоду за замовчуванням документа з розкладкою «рік»: позначка «поточний» переноситься на
 * ЯКІР року поточного періоду — найменший ключ календаря в тому ж році (`PeriodKey = рік·100 + номер`; той самий
 * якір, що в `IMonthColumnRouter` на сервері).
 *
 * ⚠ Без позначки поточного — календар як є (хук нічого не вибирає, як і раніше).
 */
export function yearAnchorCalendar<T extends PeriodLike>(periods: readonly T[] | undefined): readonly PeriodLike[] | undefined {
  const current = periods?.find((period) => period.isCurrent === true);
  if (periods === undefined || current === undefined) return periods;

  const year = Math.floor(current.periodKey / 100);
  const anchor = Math.min(
    ...periods.filter((period) => Math.floor(period.periodKey / 100) === year).map((period) => period.periodKey),
  );

  return [{ periodKey: anchor, isCurrent: true }];
}

/**
 * Стан періоду для замка ДОКУМЕНТА з розкладкою «рік» — агрегат за місяцями календаря року `periodKey` (дзеркало
 * `AnnualPeriodRule.AggregateState` на сервері): є `Open`/`Scheduled` → `Open`; інакше є `Grace` → `Grace`; усі `Closed`
 * → `Closed`.
 *
 * ⛔ D-474: документ року відкривається на якорі (найменший місяць року), і замок рахувався за станом ЯКОРЯ: закритий
 * січень робив увесь річний аркуш лише для читання з банером «період закрито», хоча сервер приймає запис — місячна
 * колонка m підлягає стану місяця m, не-місячний ввід — річному агрегату. Колонки закритих місяців замикає сам сервер
 * (`cellPermissions` зрізу), тож документ замикається лише тоді, коли закрито весь рік.
 *
 * ⚠ Місяців року в календарі немає (або календар ще не прийшов) — `undefined`: невідоме не замикає (`documentLockOf`).
 */
export function yearAggregatePeriodState(
  periods: readonly { periodKey: number; state?: PeriodState | null }[] | undefined,
  periodKey: number,
): PeriodState | undefined {
  const year = Math.floor(periodKey / 100);
  const states = (periods ?? [])
    .filter((period) => Math.floor(period.periodKey / 100) === year)
    .map((period) => period.state);
  if (states.length === 0 || states.some((state) => state === undefined || state === null)) return undefined;
  if (states.some((state) => state === 'Open' || state === 'Scheduled')) return 'Open';

  return states.includes('Grace') ? 'Grace' : 'Closed';
}
