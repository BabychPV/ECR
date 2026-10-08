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

  /**
   * Лише документи з пізніми правками (`IsLateEdit`, `D-70`).
   *
   * ⚠ На відміну від `state`, діє БЕЗ періоду — за будь-який період, так
   * само, як позначка `hasLateEdits` у самому рядку переліку (`BE-09b`).
   */
  readonly hasLateEdits: boolean;

  /**
   * Лише документи зі застарілими результатами методологій («Needs recalculation»).
   *
   * ⛔ Як і `state`, `false` без періоду, хоч би що стояло в адресі: застарілість належить періоду, і сервер
   * відповів би `422`.
   */
  readonly resultsStale: boolean;

  /**
   * Пошук за ключем чи назвою документа (`q`, `UI-18`). Порожній рядок — без пошуку.
   *
   * ⚠ Сире значення з адреси (поле показує його одразу); у запит воно йде через
   * `useDebouncedFilter` на сторінці, щоб набір не давав запит на кожну літеру.
   */
  readonly q: string;

  /** Лише документи цього проєкту (`projectId`); `null` — усі проєкти. */
  readonly projectId: number | null;

  /** Чи звужує перелік хоч один фільтр — те, що відрізняє «нічого не знайшлося» від «немає». */
  readonly active: boolean;

  readonly setState: (state: DocumentStateFilter | null) => void;
  readonly setMine: (mine: boolean) => void;
  readonly setHasLateEdits: (hasLateEdits: boolean) => void;
  readonly setResultsStale: (resultsStale: boolean) => void;
  readonly setQ: (q: string) => void;
  readonly setProjectId: (projectId: number | null) => void;

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
  const [rawHasLateEdits] = useUrlState('hasLateEdits');
  const [rawResultsStale] = useUrlState('resultsStale');
  const [rawQ] = useUrlState('q');
  const [rawProjectId] = useUrlState('projectId');
  const setParams = useUrlParamsSetter();

  const state = periodKey === null ? null : parseStateFilter(rawState);
  const mine = rawMine === 'true';
  const hasLateEdits = rawHasLateEdits === 'true';
  const resultsStale = periodKey !== null && rawResultsStale === 'true';
  const q = rawQ ?? '';
  const parsedProject = rawProjectId === null ? Number.NaN : Number(rawProjectId);
  const projectId = Number.isInteger(parsedProject) && parsedProject > 0 ? parsedProject : null;

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

  const setHasLateEdits = useCallback(
    (next: boolean) => {
      setParams({ hasLateEdits: next ? 'true' : null, cursor: null });
    },
    [setParams],
  );

  const setResultsStale = useCallback(
    (next: boolean) => {
      setParams({ resultsStale: next ? 'true' : null, cursor: null });
    },
    [setParams],
  );

  const setQ = useCallback(
    (next: string) => {
      setParams({ q: next === '' ? null : next, cursor: null });
    },
    [setParams],
  );

  const setProjectId = useCallback(
    (next: number | null) => {
      setParams({ projectId: next, cursor: null });
    },
    [setParams],
  );

  const reset = useCallback(() => {
    setParams({ state: null, mine: null, hasLateEdits: null, resultsStale: null, q: null, projectId: null, cursor: null });
  }, [setParams]);

  return {
    state,
    mine,
    hasLateEdits,
    resultsStale,
    q,
    projectId,
    active: state !== null || mine || hasLateEdits || resultsStale || q.trim() !== '' || projectId !== null,
    setState,
    setMine,
    setHasLateEdits,
    setResultsStale,
    setQ,
    setProjectId,
    reset,
  };
}
