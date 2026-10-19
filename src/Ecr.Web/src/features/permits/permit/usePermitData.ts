import { useMemo } from 'react';
import { useQueries, useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { UnitRef } from '@/api/types';
import { getRegistryRows } from '@/features/registries/rows/api';
import type { RegistryRow } from '@/features/registries/rows/api';
import { todayDateOnly } from '@/shared/format/dateOnly';
import { getPermit } from '../api/permitsApi';
import { getPermitType } from '../api/typesApi';
import type { Permit, PermitType } from '../api/types';
import { createLimiter, PickerConcurrency } from './readLimiter';
import { fieldsOf, loadRow, pickerKey, unitLabels, type LoadedRow, type PermitField, type PickerData } from './model';

/*
 * Ключі кешу. Усі під `['permits', …]`: після будь-якої дії над дозволом скидається весь піддомен екземплярів
 * (`permitScope`), а типи (`['permits','types',…]`) цей екран не чіпає — редактор типу має власний кеш.
 */
export const permitKeys = {
  item: (id: number) => ['permits', 'item', id] as const,
  type: (code: string) => ['permits', 'permit-type', code] as const,
  rows: (registryCode: string, permitId: number) => ['permits', 'rows', registryCode, permitId] as const,
  list: (code: string, scope: string) => ['permits', 'pick', code, scope] as const,
  scope: ['permits'] as const,
};

/** Сторінка читання рядків за раз: межа сервера — 500. */
const PageSize = 500;

/** Дозвіл: шапка, стан, таблиці з підсумками. */
export function usePermit(id: number) {
  return useQuery<Permit>({
    queryKey: permitKeys.item(id),
    queryFn: () => getPermit(id),
    enabled: Number.isInteger(id) && id > 0,
  });
}

/**
 * Визначення типу (стовпці таблиць).
 *
 * ⚠ `GET /permit-types/{code}` віддає ЧЕРНЕТКУ, якщо вона є, а рядки дозволу живуть у реєстрах ОПУБЛІКОВАНОЇ
 * версії. Поки в типі є неопубліковані зміни, стовпці тут можуть розходитись із реєстром таблиці; сервер на запис
 * відповість помилкою поля, а не мовчки втратить значення.
 */
export function usePermitTypeDefinition(code: string | undefined) {
  return useQuery<PermitType>({
    queryKey: permitKeys.type(code ?? ''),
    queryFn: () => getPermitType(code ?? ''),
    enabled: code !== undefined && code !== '',
  });
}

/** Одиниці виміру (кеш `['units']` спільний із редакторами). */
export function useUnitLabels(enabled: boolean): ReadonlyMap<number, string> {
  const query = useQuery({
    queryKey: ['units'],
    queryFn: () => apiFetch<UnitRef[]>('/api/v1/units'),
    enabled,
  });
  return useMemo(() => unitLabels(query.data ?? []), [query.data]);
}

/** Спільний пул читань пікерів: усі `usePickerData` разом тримають у польоті не більше {@link PickerConcurrency} запитів. */
export const pickerReads = createLimiter(PickerConcurrency);

async function readAll(
  code: string,
  parentEntryId?: number,
  run: <T>(task: () => Promise<T>) => Promise<T> = (task) => task(),
): Promise<RegistryRow[]> {
  const rows: RegistryRow[] = [];
  let cursor: string | null = null;
  do {
    // ⛔ `asOf` обов'язковий: реєстри темпоральні, без нього сервер відповідає `422 ECR-REQ-0422` (e2e-стенд 19.10).
    const after: string | null = cursor;
    const page: Awaited<ReturnType<typeof getRegistryRows>> = await run(() =>
      getRegistryRows(code, {
        asOf: todayDateOnly(),
        ...(parentEntryId === undefined ? {} : { parentEntryId }),
        cursor: after,
        limit: PageSize,
      }),
    );
    rows.push(...page.items);
    cursor = page.nextCursor ?? null;
  } while (cursor !== null);
  return rows;
}

/** Усі рядки таблиці дозволу (прихований реєстр `PRM_…`, батько — дозвіл), перетворені на рядки таблиці. */
export function usePermitRows(registryCode: string, permitId: number, fields: readonly PermitField[], enabled: boolean) {
  const query = useQuery<RegistryRow[]>({
    queryKey: permitKeys.rows(registryCode, permitId),
    queryFn: () => readAll(registryCode, permitId),
    enabled,
  });
  const rows = useMemo<LoadedRow[] | undefined>(
    () => query.data?.map((row) => loadRow(fields, row)),
    [query.data, fields],
  );
  return { query, rows };
}

/**
 * Значення пікерів форми рядка й вставки: спільні реєстри та списки типу — рядки їхніх реєстрів, «таблиця цього
 * дозволу» — рядки сестринської таблиці. Читається лише коли форма відкрита (`enabled`).
 */
export function usePickerData(permit: Permit, fields: readonly PermitField[], enabled: boolean) {
  const targets = useMemo(() => {
    const found = new Map<string, { key: string; code: string; parent: number | undefined }>();
    for (const { column, source } of fields) {
      const key = pickerKey(column, source);
      if (key === null || found.has(key)) continue;
      if (key.startsWith('permit:')) {
        const table = permit.tables.find((candidate) => candidate.code === key.slice('permit:'.length));
        if (table !== undefined) found.set(key, { key, code: table.registryCode, parent: permit.id });
      } else {
        found.set(key, { key, code: key, parent: undefined });
      }
    }
    return [...found.values()];
  }, [fields, permit]);

  const results = useQueries({
    queries: targets.map((target) => ({
      queryKey: permitKeys.list(target.code, String(target.parent ?? 'shared')),
      queryFn: () => readAll(target.code, target.parent, pickerReads),
      enabled,
    })),
  });

  const pending = enabled && results.some((result) => result.isPending);
  const failed = results.find((result) => result.isError)?.error ?? null;

  const data = useMemo<PickerData>(() => {
    const shared: Record<string, string[]> = {};
    const tables: Record<string, string[]> = {};
    const ids: Record<string, Map<string, string>> = {};
    targets.forEach((target, index) => {
      const rows = results[index]?.data ?? [];
      const labels = rows.map((row) => row.display);
      const map = new Map(rows.map((row) => [row.display, String(row.id)] as const));
      ids[target.key] = map;
      if (target.key.startsWith('permit:')) tables[target.key.slice('permit:'.length)] = labels;
      else shared[target.key] = labels;
    });
    return { sources: { shared, permitTables: tables }, ids };
    // `results` — новий масив на кожен рендер; залежність — самі дані запитів.
  }, [targets, ...results.map((result) => result.data)]);

  return { data, pending, failed };
}

/** Стовпці таблиці за кодом з визначення типу (порожньо, поки визначення не прочитано). */
export function useTableFields(type: PermitType | undefined, tableCode: string, units: ReadonlyMap<number, string>) {
  return useMemo<PermitField[]>(() => {
    const table = type?.tables.find((candidate) => candidate.code === tableCode);
    return table === undefined ? [] : fieldsOf(table, units);
  }, [type, tableCode, units]);
}
