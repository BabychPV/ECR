import { useEffect, useRef } from 'react';

/** Лише потрібне з рядка календаря проєкту (`PeriodCalendarDto.periods`). */
interface PeriodLike {
  periodKey: number;
  isCurrent?: boolean;
}

/** Поточний період проєкту — той, що позначив сервер (`isCurrent`, D-77); `null` — позначки немає. */
export function currentProjectPeriodKey(periods: readonly PeriodLike[] | undefined): number | null {
  return periods?.find((period) => period.isCurrent === true)?.periodKey ?? null;
}

/**
 * T2-12: сторінка документа без `?periodKey` відкривалась на КАЛЕНДАРНОМУ місяці (UTC), а не на
 * поточному періоді проєкту, — оператор опинявся в порожньому періоді (посилання з пошуку, «Моїх
 * задач», створення документа періоду не несуть).
 *
 * ⚠ Календарний місяць лишається лише тимчасовим значенням до відповіді календаря проєкту; щойно
 * сервер назвав поточний період, він дописується в адресу (замінює запис історії). Один раз:
 * наступні зміни періоду — рішення людини, і хук їх не перебиває. Якщо періоду в адресі вже є
 * (посилання його несе), хук нічого не робить. Немає позначки поточного — лишається як було.
 */
export function useProjectCurrentPeriodDefault(
  urlPeriod: number | null,
  periods: readonly PeriodLike[] | undefined,
  setPeriod: (value: number | null) => void,
): void {
  const decided = useRef(false);

  useEffect(() => {
    if (decided.current) return;

    if (urlPeriod !== null) {
      decided.current = true;

      return;
    }

    const key = currentProjectPeriodKey(periods);
    if (key === null) return;

    decided.current = true;
    setPeriod(key);
  }, [urlPeriod, periods, setPeriod]);
}
