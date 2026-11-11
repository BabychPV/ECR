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
