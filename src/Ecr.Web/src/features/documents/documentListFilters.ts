import { useCallback } from 'react';
import { useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import type { DocumentStateFilter } from './api';

/**
 * Значення фільтра стану — у порядку життєвого шляху аркуша.
 *
 * ⚠ Той самий перелік, що приймає сервер (`BE-09b`); інше він відхиляє `422`.
 */
export const DocumentStateFilters: readonly DocumentStateFilter[] = ['Draft', 'Submitted', 'Approved', 'Rejected'];

/** Значення з адреси → фільтр; невідоме — «без фільтра», а не `422`. */
export function parseStateFilter(raw: string | null): DocumentStateFilter | null {
  return DocumentStateFilters.find((state) => state === raw) ?? null;
}

/** Фільтри переліку документів, прочитані з адреси. */
export interface DocumentListFilters {
  /**
   * Стан, який іде в запит.
   *
   * ⛔ `null` без періоду, хоч би що стояло в адресі: стан документа поза
   * періодом не визначений (`D-93`), і сервер відповів би `422`.
   */
  readonly state: DocumentStateFilter | null;

  /** Лише «мої» — створені або подані мною. */
  readonly mine: boolean;

  /** Чи звужує перелік хоч один фільтр — те, що відрізняє «нічого не знайшлося» від «немає». */
  readonly active: boolean;

  readonly setState: (state: DocumentStateFilter | null) => void;
  readonly setMine: (mine: boolean) => void;

  /** Знімає всі фільтри; період лишається — він належить екрану. */
  readonly reset: () => void;
}

/**
 * Фільтри переліку — в АДРЕСІ (`ФВ-14.29`), як і період.
 *
 * ⚠ Кожна зміна скидає `cursor` тим самим переходом (`useUrlParamsSetter`):
 * курсор належить попередньому набору, і з ним перша сторінка нового набору
 * почалася б із середини.
 */
export function useDocumentListFilters(periodKey: number | null): DocumentListFilters {
  const [rawState] = useUrlState('state');
  const [rawMine] = useUrlState('mine');
  const setParams = useUrlParamsSetter();

  const state = periodKey === null ? null : parseStateFilter(rawState);
  const mine = rawMine === 'true';

  const setState = useCallback(
    (next: DocumentStateFilter | null) => {
      setParams({ state: next, cursor: null });
    },
    [setParams],
  );

  const setMine = useCallback(
    (next: boolean) => {
      setParams({ mine: next ? 'true' : null, cursor: null });
    },
    [setParams],
  );

  const reset = useCallback(() => {
    setParams({ state: null, mine: null, cursor: null });
  }, [setParams]);

  return { state, mine, active: state !== null || mine, setState, setMine, reset };
}
