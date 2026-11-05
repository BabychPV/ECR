import { useCallback, useEffect, useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { deletePreference, getPreferences, putPreference, type UserPreference } from './api';
import { reportFailure } from './sync';
import { PreferencesQueryKey } from './usePreferenceSync';

/**
 * Скільки колонок сітки закріплено - вибір КОРИСТУВАЧА на ВИЗНАЧЕННЯ таблиці
 * (так само, як ширини колонок, `columnWidthsSync.ts`).
 *
 * - Сервер: ключ `grid.frozenColumns.{tableDefId}` (простір `grid.*` дозволений сервером), значення -
 *   ціле число закріплених колонок разом з колонкою підпису рядка. Ключа немає - діє типове правило
 *   (`frozenColumns.ts`). `0` - свідоме «не закріплювати», це НЕ те саме, що відсутній ключ.
 * - `localStorage` - кеш першого рендера (без блимання типового закріплення); щойно відповідь сервера
 *   (`['me','preferences']`) є в кеші запиту, вона перемагає, доки людина сама не змінила вибір.
 *
 * ⛔ Ключ - `tableDefId`, а не `tableInstanceId`: екземпляр новий на кожен період, і вибір губився б
 * щомісяця (і вичерпував би ліміт налаштувань користувача).
 */

/** Верхня межа правдоподібного значення: сміття з сервера чи кешу не має ламати таблицю. */
const MaxStoredCount = 200;

/** Ключ налаштування на сервері. */
export function frozenColumnsKey(tableDefId: number): string {
  return `grid.frozenColumns.${String(tableDefId)}`;
}

/** Ключ кешу в `localStorage`. */
export function frozenColumnsCacheKey(tableDefId: number): string {
  return `ecr.frozenColumns.def:${String(tableDefId)}`;
}

/** Придатне значення - ціле `0..MaxStoredCount`; усе інше - «немає вибору». */
export function sanitizeFrozenCount(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= MaxStoredCount
    ? value
    : undefined;
}

/**
 * Серверний вибір таблиці з відповіді `GET /me/preferences`.
 *
 * @returns `undefined` - ключа на сервері немає (або значення непридатне).
 */
export function serverFrozenColumns(preferences: readonly UserPreference[], tableDefId: number): number | undefined {
  if (!Array.isArray(preferences)) return undefined;

  const key = frozenColumnsKey(tableDefId);

  return sanitizeFrozenCount(preferences.find((item) => item.key === key)?.value);
}

/** Кеш першого рендера. Ніколи не падає. */
function readCachedCount(tableDefId: number): number | undefined {
  try {
    const raw = globalThis.localStorage?.getItem(frozenColumnsCacheKey(tableDefId));
    if (raw === null || raw === undefined) return undefined;

    return sanitizeFrozenCount(JSON.parse(raw) as unknown);
  } catch {
    return undefined;
  }
}

/** Записує кеш; «немає вибору» прибирає ключ. */
function writeCachedCount(tableDefId: number, count: number | undefined): void {
  try {
    const key = frozenColumnsCacheKey(tableDefId);
    if (count === undefined) globalThis.localStorage?.removeItem(key);
    else globalThis.localStorage?.setItem(key, JSON.stringify(count));
  } catch {
    // Приватне вікно чи заблоковані дані сайту: лишиться лише сервер.
  }
}

function upsertPreference(
  current: UserPreference[] | undefined,
  key: string,
  value: number | undefined,
): UserPreference[] | undefined {
  // ⚠ Кешу ще немає - не вигадуємо його: відповідь `GET` прийде сама.
  if (current === undefined || !Array.isArray(current)) return current;

  const rest = current.filter((item) => item.key !== key);
  if (value === undefined) return rest;

  return [...rest, { key, value, updatedAt: new Date().toISOString() } as UserPreference];
}

/** Що хук віддає сітці. */
interface FrozenColumnsPreference {
  /** Вибір користувача; `undefined` - типове правило. */
  readonly count: number | undefined;
  /** Зберігає вибір; `undefined` - повернутися до типового (ключ видаляється). */
  readonly setCount: (count: number | undefined) => void;
}

interface LocalState {
  readonly tableDefId: number;
  readonly count: number | undefined;
  /** Людина змінювала вибір цієї таблиці: він свіжіший за сервер. */
  readonly touched: boolean;
}

/** Вибір закріплених колонок таблиці `tableDefId` для поточного користувача. */
export function useFrozenColumns(tableDefId: number): FrozenColumnsPreference {
  const queryClient = useQueryClient();
  const query = useQuery({
    queryKey: PreferencesQueryKey,
    queryFn: getPreferences,
    retry: false,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });

  const [state, setState] = useState<LocalState>(() => ({
    tableDefId,
    count: readCachedCount(tableDefId),
    touched: false,
  }));

  // Інша таблиця - інший стан (похідний стан під час рендера, без ефекту).
  let local = state;
  if (state.tableDefId !== tableDefId) {
    local = { tableDefId, count: readCachedCount(tableDefId), touched: false };
    setState(local);
  }

  const loaded = query.data !== undefined;
  const server = useMemo(
    () => (query.data === undefined ? undefined : serverFrozenColumns(query.data, tableDefId)),
    [query.data, tableDefId],
  );

  // Сервер перемагає кеш - доки людина сама не змінила вибір; "відповідь є, а ключа немає" теж відповідь.
  const count = !local.touched && loaded ? server : local.count;

  // Значення сервера стає кешем наступного першого рендера.
  useEffect(() => {
    if (!local.touched && loaded) writeCachedCount(tableDefId, server);
  }, [local.touched, loaded, server, tableDefId]);

  const setCount = useCallback(
    (next: number | undefined) => {
      const clean = sanitizeFrozenCount(next);
      const key = frozenColumnsKey(tableDefId);

      writeCachedCount(tableDefId, clean);
      setState({ tableDefId, count: clean, touched: true });

      const data = queryClient.getQueryData<UserPreference[]>(PreferencesQueryKey);
      const known = data === undefined ? null : serverFrozenColumns(data, tableDefId);
      // Сервер уже має таке значення - другого запиту не потрібно.
      if (known !== null && known === clean) return;

      queryClient.setQueryData<UserPreference[] | undefined>(PreferencesQueryKey, (old) =>
        upsertPreference(old, key, clean),
      );

      const request = clean === undefined ? deletePreference(key) : putPreference(key, clean);
      void request.catch((error: unknown) => {
        reportFailure(key, error);
      });
    },
    [tableDefId, queryClient],
  );

  return { count, setCount };
}
