import { useCallback, useMemo } from 'react';
import { useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import type { DocumentStateFilter } from './api';
import { parseVersionSort, parseVersionsParam, versionsToParam, type VersionSort } from './documentVersion';

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

/** ✎ D-450: значення фільтра «Власник»: усі, лише мої або конкретна людина (її `userId`). */
export type OwnerFilterValue = 'all' | 'mine' | number;

/** Значення з адреси → ідентифікатор людини; не додатне ціле — «без фільтра», а не `422`. */
export function parseOwnerFilter(raw: string | null): number | null {
  const parsed = raw === null ? Number.NaN : Number(raw);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
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

  /**
   * Лише «мої» — створені або подані мною.
   *
   * ✎ D-450: `false`, коли обрано конкретну людину (`owner`): сервер обидва разом відхиляє `422`, а старе посилання
   * з `mine=true` і новим `owner=` показує ту людину, яку видно в полі.
   */
  readonly mine: boolean;

  /** ✎ D-450: лише документи цієї людини (`owner=<id>` в адресі; власник = автор документа); `null` — будь-чиї. */
  readonly owner: number | null;

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

  /**
   * Колонка «Version»: лише документи з цими версіями шаблону (точний збіг; `versions=1.0,2.3` в адресі).
   *
   * ⚠ КЛІЄНТСЬКИЙ фільтр однієї завантаженої сторінки: сервер параметра не має (контракт RPT-STABLE віддає лише
   * `templateVersion`/`templateVersionByPeriod`), тому в запит і ключ кешу він НЕ йде.
   */
  readonly versions: readonly string[];

  /** Колонка «Version»: напрямок сортування сторінки (`vsort=asc|desc`); `null` — порядок сервера. Не фільтр. */
  readonly versionSort: VersionSort | null;

  /** Чи звужує перелік хоч один фільтр — те, що відрізняє «нічого не знайшлося» від «немає». */
  readonly active: boolean;

  readonly setState: (state: DocumentStateFilter | null) => void;
  readonly setMine: (mine: boolean) => void;
  /** ✎ D-450: «Власник» одним переходом — усі / лише мої / конкретна людина (решта прибирається з адреси). */
  readonly setOwner: (owner: OwnerFilterValue) => void;
  readonly setHasLateEdits: (hasLateEdits: boolean) => void;
  readonly setResultsStale: (resultsStale: boolean) => void;
  readonly setQ: (q: string) => void;
  readonly setProjectId: (projectId: number | null) => void;
  readonly setVersions: (versions: readonly string[]) => void;
  readonly setVersionSort: (sort: VersionSort | null) => void;

  /** Знімає всі фільтри; період лишається — він належить екрану. Сортування — не фільтр, воно лишається. */
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
  const [rawOwner] = useUrlState('owner');
  const [rawHasLateEdits] = useUrlState('hasLateEdits');
  const [rawResultsStale] = useUrlState('resultsStale');
  const [rawQ] = useUrlState('q');
  const [rawProjectId] = useUrlState('projectId');
  const [rawVersions] = useUrlState('versions');
  const [rawVersionSort] = useUrlState('vsort');
  const setParams = useUrlParamsSetter();

  const state = periodKey === null ? null : parseStateFilter(rawState);
  const owner = parseOwnerFilter(rawOwner);
  const mine = owner === null && rawMine === 'true';
  const hasLateEdits = rawHasLateEdits === 'true';
  const resultsStale = periodKey !== null && rawResultsStale === 'true';
  const q = rawQ ?? '';
  const parsedProject = rawProjectId === null ? Number.NaN : Number(rawProjectId);
  const projectId = Number.isInteger(parsedProject) && parsedProject > 0 ? parsedProject : null;

  // Новий масив на кожен рендер ламав би залежності; рядок з адреси стабільний, тож розбір - за ним.
  const versions = useMemo(() => parseVersionsParam(rawVersions), [rawVersions]);
  const versionSort = parseVersionSort(rawVersionSort);

  const setVersions = useCallback(
    (next: readonly string[]) => {
      // Клієнтський фільтр: курсор не міняється - сторінка та сама, звужується лише її показ.
      setParams({ versions: versionsToParam(next) });
    },
    [setParams],
  );

  const setVersionSort = useCallback(
    (next: VersionSort | null) => {
      setParams({ vsort: next });
    },
    [setParams],
  );

  const setState = useCallback(
    (next: DocumentStateFilter | null) => {
      setParams({ state: next, cursor: null });
    },
    [setParams],
  );

  const setMine = useCallback(
    (next: boolean) => {
      // ✎ D-450: «мої» знімає обрану людину — два «чиї» разом сервер відхиляє `422`.
      setParams({ mine: next ? 'true' : null, owner: null, cursor: null });
    },
    [setParams],
  );

  const setOwner = useCallback(
    (next: OwnerFilterValue) => {
      setParams({
        mine: next === 'mine' ? 'true' : null,
        owner: typeof next === 'number' ? String(next) : null,
        cursor: null,
      });
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
    setParams({
      state: null,
      mine: null,
      owner: null,
      hasLateEdits: null,
      resultsStale: null,
      q: null,
      projectId: null,
      versions: null,
      cursor: null,
    });
  }, [setParams]);

  return {
    state,
    mine,
    owner,
    hasLateEdits,
    resultsStale,
    q,
    projectId,
    versions,
    versionSort,
    active:
      state !== null ||
      mine ||
      owner !== null ||
      hasLateEdits ||
      resultsStale ||
      q.trim() !== '' ||
      projectId !== null ||
      versions.length > 0,
    setState,
    setMine,
    setOwner,
    setHasLateEdits,
    setResultsStale,
    setQ,
    setProjectId,
    setVersions,
    setVersionSort,
    reset,
  };
}
