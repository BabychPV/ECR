import { useRef } from 'react';

/**
 * Чи однакові два зрізи, КРІМ користувацьких кольорів (`cellStyles`): усі інші поля — за посиланням.
 * Зріз із кешу React Query ділить незмінні піддерева між версіями (`structuralSharing`, оптимістичне оновлення
 * `applyStylePatches` теж розгортає `...slice`), тож «та сама форма» — це рівність посилань полів.
 */
export function sameExceptCellStyles<T extends object>(a: T, b: T): boolean {
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]);

  for (const key of keys) {
    if (key === 'cellStyles') continue;
    if ((a as Record<string, unknown>)[key] !== (b as Record<string, unknown>)[key]) return false;
  }

  return true;
}

/**
 * Зріз, чия ідентичність НЕ змінюється, поки мінялись лише користувацькі кольори (`cellStyles`).
 *
 * ⛔ P1 (Land, «заливка рядка гальмує»): колонки сітки й модель рядків (`gridRows`) залежали від усього зрізу, тож кожна
 * зміна кольору (оптимістика + перечитання) перебудовувала й ЗАМІНЮВАЛА їх цілком — повне перемальовування сітки, скид
 * виділення й сотні мілісекунд скрипту на кожне застосування. Кольори читаються окремо (через посилання), тож для
 * решти залежностей достатньо зрізу без них.
 */
export function useStyleFreeSlice<T extends object>(data: T | undefined): T | undefined {
  const kept = useRef<T | undefined>(undefined);

  if (data === undefined) {
    kept.current = undefined;

    return undefined;
  }

  if (kept.current !== undefined && sameExceptCellStyles(kept.current, data)) return kept.current;

  kept.current = data;

  return data;
}
